using System.Text.Json;
using System.Runtime.CompilerServices;
using AgentHappey.Common.Extensions;
using AgentHappey.Common.Models;
using AgentHappey.Core.ChatClient;
using AgentHappey.Core.Evaluations;
using AgentHappey.Core.Extensions;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.AspNetCore.Http;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.AI;
using System.Text.Json.Serialization;

namespace AgentHappey.Core.ChatRuntime;

public interface IChatRuntimeOrchestrator
{
    Task<ChatRuntimeContext> PrepareAsync(
        HttpResponse response,
        AgentRequest chatRequest,
        Func<Agent, AgentChatClient> agentClientFactory,
        Action<AgentChatClient, IReadOnlyList<ChatMessage>>? configureAgentClient = null,
        CancellationToken cancellationToken = default);

    Task<ChatRuntimeContext> PrepareAsync(
        HttpResponse response,
        ChatRuntimeRequest chatRequest,
        Func<Agent, AgentChatClient> agentClientFactory,
        Action<AgentChatClient, IReadOnlyList<ChatMessage>>? configureAgentClient = null,
        CancellationToken cancellationToken = default);

    Workflow BuildWorkflow(AgentRequest chatRequest, IReadOnlyList<AIAgent> agents);

    Workflow BuildWorkflow(ChatRuntimeRequest chatRequest, IReadOnlyList<AIAgent> agents);

    Task ExecuteAsync(
        HttpResponse response,
        AgentRequest chatRequest,
        ChatRuntimeContext context,
        CancellationToken cancellationToken = default);

    Task ExecuteAsync(
        HttpResponse response,
        ChatRuntimeRequest chatRequest,
        ChatRuntimeContext context,
        CancellationToken cancellationToken = default);

    Task ExecuteWorkflowAsync<TInput>(
        HttpResponse response,
        Workflow workflow,
        TInput input,
        bool emitTurnToken,
        CancellationToken cancellationToken = default)
        where TInput : notnull;

    Task<AgentResponse> RunAgentAsync(
        ChatRuntimeContext context,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<AgentResponseUpdate> StreamAgentAsync(
        ChatRuntimeContext context,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkflowEvent>> RunWorkflowAsync(
        ChatRuntimeRequest chatRequest,
        ChatRuntimeContext context,
        bool emitTurnToken,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<WorkflowEvent> StreamWorkflowAsync(
        ChatRuntimeRequest chatRequest,
        ChatRuntimeContext context,
        bool emitTurnToken,
        CancellationToken cancellationToken = default);
}

public sealed record ChatRuntimeContext(
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<AIAgent> Agents,
    ChatClientAgentRunOptions? SingleAgentRunOptions,
    IReadOnlyList<Agent> ResolvedAgents)
{
    public AIAgent PrimaryAgent => Agents.FirstOrDefault() ?? throw new InvalidOperationException("No agent found");

    public Agent PrimaryResolvedAgent => ResolvedAgents.FirstOrDefault() ?? throw new InvalidOperationException("No resolved agent found");

    public InMemoryWorkflowAgentProvider CreateWorkflowAgentProvider() =>
        new(Agents.Select(agent => (agent.Name!, agent)));
}

public sealed class ChatRuntimeOrchestrator(IStreamingContentMapper mapper, IModelCatalog modelCatalog) : IChatRuntimeOrchestrator
{


    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerOptions.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };


    public async Task<ChatRuntimeContext> PrepareAsync(
        HttpResponse response,
        AgentRequest chatRequest,
        Func<Agent, AgentChatClient> agentClientFactory,
        Action<AgentChatClient, IReadOnlyList<ChatMessage>>? configureAgentClient = null,
        CancellationToken cancellationToken = default)
    {
        var runtimeRequest = CreateRuntimeRequest(chatRequest);
        ConfigureStreamingResponse(response);

        return await PrepareCoreAsync(
            response,
            runtimeRequest,
            agentClientFactory,
            configureAgentClient,
            emitConnectionParts: true,
            cancellationToken);
    }

    public Task<ChatRuntimeContext> PrepareAsync(
        HttpResponse response,
        ChatRuntimeRequest chatRequest,
        Func<Agent, AgentChatClient> agentClientFactory,
        Action<AgentChatClient, IReadOnlyList<ChatMessage>>? configureAgentClient = null,
        CancellationToken cancellationToken = default)
        => PrepareCoreAsync(
            response,
            chatRequest,
            agentClientFactory,
            configureAgentClient,
            emitConnectionParts: false,
            cancellationToken);

    private async Task<ChatRuntimeContext> PrepareCoreAsync(
        HttpResponse response,
        ChatRuntimeRequest chatRequest,
        Func<Agent, AgentChatClient> agentClientFactory,
        Action<AgentChatClient, IReadOnlyList<ChatMessage>>? configureAgentClient,
        bool emitConnectionParts,
        CancellationToken cancellationToken)
    {
        var agents = new List<AIAgent>();
        ChatClientAgentRunOptions? runOptions = null;
        var messages = chatRequest.Messages.ToList();
        var resolvedAgents = await ResolveAgentsAsync(chatRequest, cancellationToken);

        foreach (var agent in resolvedAgents)
        {
            var agentClient = agentClientFactory(agent);
            configureAgentClient?.Invoke(agentClient, messages);

            var tools = await agentClient.ConnectMcp(cancellationToken);
            var instructions = agentClient.GetComposedInstructions();

            agents.Add(new ChatClientAgent(
                agentClient,
                instructions: instructions,
                name: agent.Name,
                tools: tools,
                description: agent.Description));

            runOptions = new ChatClientAgentRunOptions(new ChatOptions
            {
                Tools = tools
            });

            if (emitConnectionParts)
                await WriteConnectionPartsAsync(response, agentClient, cancellationToken);
        }

        return new ChatRuntimeContext(messages, agents, runOptions, resolvedAgents);
    }

    private async Task<IReadOnlyList<Agent>> ResolveAgentsAsync(
    ChatRuntimeRequest chatRequest,
    CancellationToken cancellationToken)
    {
        var agents = new List<Agent>();

        if (chatRequest.Agents is { Count: > 0 })
            agents.AddRange(chatRequest.Agents);

        var requestedModels = chatRequest.Models?
            .Where(modelId => !string.IsNullOrWhiteSpace(modelId))
            .ToList();

        if (requestedModels is { Count: > 0 })
        {
            var resolvedAgents = await modelCatalog.ResolveAgentsAsync(
                requestedModels,
                cancellationToken);

            agents.AddRange(resolvedAgents);
        }

        if (!string.IsNullOrWhiteSpace(chatRequest.Model))
        {
            var resolvedAgent = await modelCatalog.ResolveAgentAsync(
                chatRequest.Model,
                cancellationToken);

            if (resolvedAgent != null)
                agents.Add(resolvedAgent);
        }

        return agents;
    }

    public Workflow BuildWorkflow(AgentRequest chatRequest, IReadOnlyList<AIAgent> agents) =>
        BuildWorkflow(CreateRuntimeRequest(chatRequest), agents);

    public Workflow BuildWorkflow(ChatRuntimeRequest chatRequest, IReadOnlyList<AIAgent> agents) =>
        chatRequest.WorkflowType switch
        {
            "sequential" => AgentWorkflowBuilder.BuildSequential(agents),
            "concurrent" => AgentWorkflowBuilder.BuildConcurrent(agents),
            "magentic" => agents.BuildMagenticWorkflow(chatRequest.WorkflowMetadata?.Magentic),
            "groupchat" => AgentWorkflowBuilder.CreateGroupChatBuilderWith(team =>
                    new RoundRobinGroupChatManager(team)
                    {
                        MaximumIterationCount = chatRequest.WorkflowMetadata?.Groupchat?.MaximumIterationCount ?? 5
                    })
                .AddParticipants(agents)
                .Build(),
            "handoff" => agents.BuildHandoffWorkflow(chatRequest.WorkflowMetadata?.Handoff?.Handoffs),
            _ => throw new InvalidOperationException("Invalid workflow type.")
        };

    public async Task ExecuteAsync(
        HttpResponse response,
        AgentRequest chatRequest,
        ChatRuntimeContext context,
        CancellationToken cancellationToken = default)
        => await ExecuteAsync(
            response,
            CreateRuntimeRequest(chatRequest, context.Messages),
            context,
            cancellationToken);

    public async Task ExecuteAsync(
        HttpResponse response,
        ChatRuntimeRequest chatRequest,
        ChatRuntimeContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.Agents.Count > 1)
        {
            var workflow = BuildWorkflow(chatRequest, context.Agents);
            await ExecuteWorkflowAsync(response, workflow, context.Messages, emitTurnToken: true, cancellationToken);
            return;
        }

        var updates = StreamAgentAsync(context, cancellationToken);
        var mapped = mapper.MapAsync(updates, cancellationToken);
        await response.WritePartsAsync(mapped, cancellationToken);
    }

    public async Task<AgentResponse> RunAgentAsync(
        ChatRuntimeContext context,
        CancellationToken cancellationToken = default)
    {
        var response = await context.PrimaryAgent.RunAsync(
            context.Messages,
            options: context.SingleAgentRunOptions,
            cancellationToken: cancellationToken);

        var evaluator = LocalEvaluation.Create(context.PrimaryResolvedAgent.Evaluations?.LocalEvaluator);
        if (evaluator is null)
            return response;

        var evaluation = await EvaluateAgentAsync(
            context.PrimaryAgent,
            response,
            LocalEvaluation.GetQuery(context.Messages),
            evaluator,
            context.PrimaryResolvedAgent.Name,
            cancellationToken);

        response.Messages.Add(new ChatMessage(
            ChatRole.Assistant,
            LocalEvaluation.CreateMetadataUpdate(evaluation).Contents));

        return response;
    }

    public IAsyncEnumerable<AgentResponseUpdate> StreamAgentAsync(
        ChatRuntimeContext context,
        CancellationToken cancellationToken = default)
    {
        var updates = context.PrimaryAgent.RunStreamingAsync(
            context.Messages,
            options: context.SingleAgentRunOptions,
            cancellationToken: cancellationToken);
        var evaluator = LocalEvaluation.Create(context.PrimaryResolvedAgent.Evaluations?.LocalEvaluator);

        return evaluator is null
            ? updates
            : ObserveAndEvaluateAgentAsync(context, updates, evaluator, cancellationToken);
    }

    public async Task<IReadOnlyList<WorkflowEvent>> RunWorkflowAsync(
        ChatRuntimeRequest chatRequest,
        ChatRuntimeContext context,
        bool emitTurnToken,
        CancellationToken cancellationToken = default)
    {
        var workflow = BuildWorkflow(chatRequest, context.Agents);

        await using var run = await InProcessExecution.RunAsync(
            workflow,
            context.Messages,
            cancellationToken: cancellationToken);

        var events = run.OutgoingEvents.ToList();
        var evaluation = await EvaluateWorkflowAsync(run, context, cancellationToken);

        if (evaluation is not null)
            events.Add(new AgentResponseUpdateEvent(
                nameof(LocalEvaluation),
                LocalEvaluation.CreateMetadataUpdate(evaluation)));

        return events;
    }

    public async IAsyncEnumerable<WorkflowEvent> StreamWorkflowAsync(
        ChatRuntimeRequest chatRequest,
        ChatRuntimeContext context,
        bool emitTurnToken,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var workflow = BuildWorkflow(chatRequest, context.Agents);

        await using var run = await InProcessExecution.RunStreamingAsync(
            workflow,
            context.Messages,
            cancellationToken: cancellationToken);

        if (emitTurnToken)
            await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

        var events = new List<WorkflowEvent>();
        await foreach (var update in run.WatchStreamAsync(cancellationToken).WithCancellation(cancellationToken))
        {
            events.Add(update);
            yield return update;
        }

        var evaluation = await EvaluateWorkflowEventsAsync(events, context, cancellationToken);
        if (evaluation is not null)
            yield return new AgentResponseUpdateEvent(
                nameof(LocalEvaluation),
                LocalEvaluation.CreateMetadataUpdate(evaluation));
    }

    private static async IAsyncEnumerable<AgentResponseUpdate> ObserveAndEvaluateAgentAsync(
        ChatRuntimeContext context,
        IAsyncEnumerable<AgentResponseUpdate> updates,
        IAgentEvaluator evaluator,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var observed = new List<AgentResponseUpdate>();

        await foreach (var update in updates.WithCancellation(cancellationToken))
        {
            observed.Add(update);
            yield return update;
        }

        var response = observed.ToAgentResponse();
        var evaluation = await EvaluateAgentAsync(
            context.PrimaryAgent,
            response,
            LocalEvaluation.GetQuery(context.Messages),
            evaluator,
            context.PrimaryResolvedAgent.Name,
            cancellationToken);

        yield return LocalEvaluation.CreateMetadataUpdate(evaluation);
    }

    private static async Task<AgentEvaluationResults> EvaluateAgentAsync(
        AIAgent agent,
        AgentResponse response,
        string query,
        IAgentEvaluator evaluator,
        string evalName,
        CancellationToken cancellationToken)
    {
        try
        {
            return await agent.EvaluateAsync(
                [response],
                [query],
                evaluator,
                evalName,
                cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return LocalEvaluation.CreateFailedResult(exception);
        }
    }

    private static async Task<AgentEvaluationResults?> EvaluateWorkflowAsync(
        Run run,
        ChatRuntimeContext context,
        CancellationToken cancellationToken)
        => await EvaluateWorkflowEventsAsync(run.OutgoingEvents, context, cancellationToken);

    private static async Task<AgentEvaluationResults?> EvaluateWorkflowEventsAsync(
        IEnumerable<WorkflowEvent> events,
        ChatRuntimeContext context,
        CancellationToken cancellationToken)
    {
        var evaluators = context.ResolvedAgents
            .Select((agent, index) => new
            {
                Agent = agent,
                RuntimeAgent = context.Agents[index],
                Evaluator = LocalEvaluation.Create(agent.Evaluations?.LocalEvaluator)
            })
            .Where(item => item.Evaluator is not null)
            .ToList();

        if (evaluators.Count == 0)
            return null;

        var subResults = new Dictionary<string, AgentEvaluationResults>(StringComparer.Ordinal);

        foreach (var item in evaluators)
        {
            try
            {
                var agentResult = await EvaluateWorkflowAgentAsync(
                    events,
                    item.RuntimeAgent,
                    item.Agent.Name,
                    item.Evaluator!,
                    cancellationToken);

                subResults[item.Agent.Name] = agentResult;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                subResults[item.Agent.Name] = LocalEvaluation.CreateFailedResult(exception);
            }
        }

        return new AgentEvaluationResults("Local", [], [])
        {
            Status = subResults.Values.All(result => !string.Equals(result.Status, "failed", StringComparison.OrdinalIgnoreCase))
                ? "completed"
                : "failed",
            Error = subResults.Values.FirstOrDefault(result => !string.IsNullOrWhiteSpace(result.Error))?.Error,
            SubResults = subResults
        };
    }

    private static async Task<AgentEvaluationResults> EvaluateWorkflowAgentAsync(
        IEnumerable<WorkflowEvent> events,
        AIAgent agent,
        string agentName,
        IAgentEvaluator evaluator,
        CancellationToken cancellationToken)
    {
        var updates = events
            .OfType<AgentResponseUpdateEvent>()
            .Where(item => string.Equals(item.Update.AuthorName, agentName, StringComparison.Ordinal)
                || string.Equals(item.Update.AgentId, agent.Id, StringComparison.Ordinal))
            .Select(item => item.Update)
            .ToList();

        if (updates.Count == 0)
            return new AgentEvaluationResults("Local", [], []);

        var response = updates.ToAgentResponse();
        var conversation = response.Messages.ToList();
        var item = new EvalItem(conversation, ConversationSplitters.LastTurn);
        return await evaluator.EvaluateAsync([item], agentName, cancellationToken);
    }

    public async Task ExecuteWorkflowAsync<TInput>(
      HttpResponse response,
      Workflow workflow,
     TInput input,
     bool emitTurnToken,
     CancellationToken cancellationToken = default)
     where TInput : notnull
    {
        await using var run = await InProcessExecution.RunStreamingAsync(
            workflow,
            input,
            cancellationToken: cancellationToken);

        if (emitTurnToken)
            await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

        var updates = run.WatchStreamAsync(cancellationToken);
        var mapped = mapper.MapAsync(updates, cancellationToken);
        await response.WritePartsAsync(mapped, cancellationToken);
    }

    private static void ConfigureStreamingResponse(HttpResponse response)
    {
        response.ContentType = "text/event-stream";
        response.Headers["x-vercel-ai-ui-message-stream"] = "v1";
    }

    private static ChatRuntimeRequest CreateRuntimeRequest(
        AgentRequest chatRequest,
        IReadOnlyList<ChatMessage>? messages = null)
        => new(
            messages ?? chatRequest.Messages.ToMessages(chatRequest.Agents?.Select(agent => agent.Name)).ToList(),
            chatRequest.Model,
            chatRequest.Models?
                .Where(modelId => !string.IsNullOrWhiteSpace(modelId))
                .ToList(),
            chatRequest.Agents?.ToList(),
            chatRequest.WorkflowType,
            chatRequest.WorkflowMetadata);

    private static async Task WriteConnectionPartsAsync(
        HttpResponse response,
        AgentChatClient agentClient,
        CancellationToken cancellationToken)
    {
        var connections = await agentClient.GetConnections();

        List<UIMessagePart> items =
        [
            .. connections.Select(connection => ToolCallPart.CreateProviderExecuted(
                connection.SessionId!,
                "connect_mcp",
                new { connection.Url }))
        ];

        await response.WritePartsAsync(ToAsync(items), cancellationToken);

        List<UIMessagePart> connectedItems =
        [
            .. connections.Select(connection => new ToolOutputAvailablePart
            {
                ToolCallId = connection.SessionId!,
                Output = new ModelContextProtocol.Protocol.CallToolResult
                {
                    IsError = false,
                    StructuredContent = JsonSerializer.SerializeToElement(connection, JsonOptions)
                },
                ProviderExecuted = true
            })
        ];

        await response.WritePartsAsync(ToAsync(connectedItems), cancellationToken);
    }

    private static async IAsyncEnumerable<T> ToAsync<T>(IEnumerable<T> source)
    {
        foreach (var item in source)
        {
            yield return item;
            await Task.Yield();
        }
    }
}
