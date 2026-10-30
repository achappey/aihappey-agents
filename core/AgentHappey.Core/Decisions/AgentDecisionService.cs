using System.Text.Json;
using AgentHappey.Common.Models;
using AgentHappey.Core.ChatClient;
using AgentHappey.Core.ChatRuntime;
using AIHappey.Core.Models;
using AIHappey.Responses;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;

namespace AgentHappey.Core.DecisionRuntime;

/// <summary>Both protocols share one schema-constrained agent execution with the normal tool loop.</summary>
public sealed class AgentDecisionService(IChatRuntimeOrchestrator orchestrator, IModelCatalog catalog)
{
    public async Task<OpenAIDecisionResponse> ExecuteAsync(OpenAIDecisionRequest request, HttpResponse response,
        Func<Agent, AgentChatClient> clientFactory, CancellationToken cancellationToken)
    {
        var plan = DecisionPlan.Create(request);
        var result = await RunAsync(plan, response, clientFactory, cancellationToken);
        return new()
        {
            Model = plan.Model,
            Usage = result.Usage,
            Answers = request.Questions.Select((question, index) =>
                DecisionAnswerMapping.ToOpenAI(question, result.Answers[index])).ToArray()
        };
    }

    public async Task<DecisionResponse> ExecuteAsync(DecisionRequest request, HttpResponse response,
        Func<Agent, AgentChatClient> clientFactory, CancellationToken cancellationToken)
    {
        var plan = DecisionPlan.Create(request);
        var result = await RunAsync(plan, response, clientFactory, cancellationToken);
        return new()
        {
            Answers = request.Questions.Select((entry, index) => (entry.Key, Answer: DecisionAnswerMapping.ToVercel(entry.Value, result.Answers[index])))
                .ToDictionary(e => e.Key, e => e.Answer, StringComparer.Ordinal),
            Usage = new() { InputTokens = result.Usage.InputTokens, OutputTokens = result.Usage.OutputTokens },
            Response = new() { Id = result.ResponseId, ModelId = plan.Model, Timestamp = DateTimeOffset.UtcNow },
            ProviderMetadata = result.Gateway is { } gateway ? new() { ["gateway"] = gateway } : null,
            Warnings = []
        };
    }

    private async Task<DecisionRunResult> RunAsync(DecisionPlan plan, HttpResponse response,
        Func<Agent, AgentChatClient> clientFactory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var schema = plan.CreateSchema();
        // Resolve before connecting tools or executing any agent side effects.
        var agent = await catalog.ResolveAgentAsync(plan.Model, cancellationToken)
            ?? throw new ArgumentException($"Agent '{plan.Model}' is not available.");
        AgentChatClient? client = null;
        try
        {
            var runtimeRequest = new ChatRuntimeRequest(plan.Messages, null, null, [agent]);
            var context = await orchestrator.PrepareAsync(response, runtimeRequest, resolved =>
            {
                client = clientFactory(resolved);
                client.BeginDecisionExecution(schema, plan.MaxOutputTokens, plan.SafetyIdentifier);
                return client;
            }, (agentClient, messages) => agentClient.SetHistory(messages), cancellationToken);
            var result = await context.PrimaryAgent.RunAsync(context.Messages, options: context.SingleAgentRunOptions,
                cancellationToken: cancellationToken);
            if (client is null) throw new InvalidOperationException("No agent client was prepared.");
            EnsureNotSuspended(result.Messages);
            cancellationToken.ThrowIfCancellationRequested();
            var responseResult = client.GetDecisionResponse();
            var answers = DecisionAnswerMapping.Read(responseResult, plan.Questions);
            return new(answers, client.GetDecisionInferenceUsage(), responseResult.Id,
                client.GetDecisionGatewayMetadata(result.Messages));
        }
        finally { client?.Dispose(); }
    }

    private static void EnsureNotSuspended(IEnumerable<ChatMessage> messages)
    {
        if (messages.SelectMany(message => message.Contents).Any(content => content switch
            {
                DataContent data => data.Name?.StartsWith("elicitation-request-", StringComparison.Ordinal) == true,
                FunctionResultContent result => result.Result is McpToolInputRequiredException,
                FunctionCallContent call => call.Name == "ai_input_required",
                _ => false
            }))
            throw new InvalidOperationException("The agent requires interactive input; decisions cannot complete a suspended run.");
    }

    private sealed record DecisionRunResult(IReadOnlyList<JsonElement> Answers, OpenAIDecisionUsage Usage,
        string ResponseId, JsonElement? Gateway);
}
