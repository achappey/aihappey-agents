using System.Text.Json;
using AgentHappey.Common.Extensions;
using AgentHappey.Common.Models;
using AgentHappey.Core.ChatClient;
using AgentHappey.Core.Extensions;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using AIHappey.Vercel.Models;

namespace AgentHappey.Core.ChatRuntime;

public interface IChatRuntimeOrchestrator
{
    Task<ChatRuntimeContext> PrepareAsync(
        HttpResponse response,
        AgentRequest chatRequest,
        Func<Agent, AgentChatClient> agentClientFactory,
        Action<AgentChatClient, IReadOnlyList<ChatMessage>>? configureAgentClient = null,
        CancellationToken cancellationToken = default);

    Workflow BuildWorkflow(AgentRequest chatRequest, IReadOnlyList<AIAgent> agents);

    Task ExecuteAsync(
        HttpResponse response,
        AgentRequest chatRequest,
        ChatRuntimeContext context,
        CancellationToken cancellationToken = default);

    Task ExecuteWorkflowAsync<TInput>(
        HttpResponse response,
        Workflow workflow,
        TInput input,
        bool emitTurnToken,
        CancellationToken cancellationToken = default)
        where TInput : notnull;
}

public sealed record ChatRuntimeContext(
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<AIAgent> Agents,
    ChatClientAgentRunOptions? SingleAgentRunOptions)
{
    public AIAgent PrimaryAgent => Agents.FirstOrDefault() ?? throw new InvalidOperationException("No agent found");

    public InMemoryWorkflowAgentProvider CreateWorkflowAgentProvider() =>
        new(Agents.Select(agent => (agent.Name!, agent)));
}

public sealed class ChatRuntimeOrchestrator(IStreamingContentMapper mapper) : IChatRuntimeOrchestrator
{
    public async Task<ChatRuntimeContext> PrepareAsync(
        HttpResponse response,
        AgentRequest chatRequest,
        Func<Agent, AgentChatClient> agentClientFactory,
        Action<AgentChatClient, IReadOnlyList<ChatMessage>>? configureAgentClient = null,
        CancellationToken cancellationToken = default)
    {
        var agents = new List<AIAgent>();
        ChatClientAgentRunOptions? runOptions = null;
        var messages = chatRequest.Messages.ToMessages().ToList();

        ConfigureStreamingResponse(response);

        foreach (var agent in chatRequest.Agents)
        {
            var agentClient = agentClientFactory(agent);
            configureAgentClient?.Invoke(agentClient, messages);

            var tools = await agentClient.ConnectMcp(cancellationToken);

            agents.Add(new ChatClientAgent(
                agentClient,
                instructions: agent.Instructions,
                name: agent.Name,
                tools: tools,
                description: agent.Description));

            runOptions = new ChatClientAgentRunOptions(new ChatOptions
            {
                Tools = tools
            });

            await WriteConnectionPartsAsync(response, agentClient, cancellationToken);
        }

        return new ChatRuntimeContext(messages, agents, runOptions);
    }

    public Workflow BuildWorkflow(AgentRequest chatRequest, IReadOnlyList<AIAgent> agents) =>
        chatRequest.WorkflowType switch
        {
            "sequential" => AgentWorkflowBuilder.BuildSequential(agents),
            "concurrent" => AgentWorkflowBuilder.BuildConcurrent(agents),
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
    {
        if (context.Agents.Count > 1)
        {
            var workflow = BuildWorkflow(chatRequest, context.Agents);
            await ExecuteWorkflowAsync(response, workflow, context.Messages, emitTurnToken: true, cancellationToken);
            return;
        }

        var updates = context.PrimaryAgent.RunStreamingAsync(
            context.Messages,
            options: context.SingleAgentRunOptions,
            cancellationToken: cancellationToken);

        var mapped = mapper.MapAsync(updates, cancellationToken);
        await response.WritePartsAsync(mapped, cancellationToken);
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
                    StructuredContent = JsonSerializer.SerializeToElement(connection)
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
