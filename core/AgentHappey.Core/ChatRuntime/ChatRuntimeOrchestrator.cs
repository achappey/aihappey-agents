using System.Text.Json;
using System.Runtime.CompilerServices;
using AgentHappey.Common.Extensions;
using AgentHappey.Common.Models;
using AgentHappey.Core.ChatClient;
using AgentHappey.Core.Extensions;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.AspNetCore.Http;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.AI;

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
    ChatClientAgentRunOptions? SingleAgentRunOptions)
{
    public AIAgent PrimaryAgent => Agents.FirstOrDefault() ?? throw new InvalidOperationException("No agent found");

    public InMemoryWorkflowAgentProvider CreateWorkflowAgentProvider() =>
        new(Agents.Select(agent => (agent.Name!, agent)));
}

public sealed class ChatRuntimeOrchestrator(IStreamingContentMapper mapper, IModelCatalog modelCatalog) : IChatRuntimeOrchestrator
{
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

            if (emitConnectionParts)
                await WriteConnectionPartsAsync(response, agentClient, cancellationToken);
        }

        return new ChatRuntimeContext(messages, agents, runOptions);
    }

    private async Task<IReadOnlyList<Agent>> ResolveAgentsAsync(
        ChatRuntimeRequest chatRequest,
        CancellationToken cancellationToken)
    {
        var requestedModels = chatRequest.Models?
            .Where(modelId => !string.IsNullOrWhiteSpace(modelId))
            .ToList();

        if (requestedModels is { Count: > 0 })
        {
            var resolvedAgents = await modelCatalog.ResolveAgentsAsync(requestedModels, cancellationToken);

            if (resolvedAgents.Count == requestedModels.Count)
                return resolvedAgents;
        }

        if (!string.IsNullOrWhiteSpace(chatRequest.Model))
        {
            var resolvedAgent = await modelCatalog.ResolveAgentAsync(chatRequest.Model, cancellationToken);

            if (resolvedAgent != null)
                return [resolvedAgent];
        }

        return chatRequest.Agents?.ToList() ?? [];
    }

    public Workflow BuildWorkflow(AgentRequest chatRequest, IReadOnlyList<AIAgent> agents) =>
        BuildWorkflow(CreateRuntimeRequest(chatRequest), agents);

    public Workflow BuildWorkflow(ChatRuntimeRequest chatRequest, IReadOnlyList<AIAgent> agents) =>
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

    public Task<AgentResponse> RunAgentAsync(
        ChatRuntimeContext context,
        CancellationToken cancellationToken = default)
        => context.PrimaryAgent.RunAsync(
            context.Messages,
            options: context.SingleAgentRunOptions,
            cancellationToken: cancellationToken);

    public IAsyncEnumerable<AgentResponseUpdate> StreamAgentAsync(
        ChatRuntimeContext context,
        CancellationToken cancellationToken = default)
        => context.PrimaryAgent.RunStreamingAsync(
            context.Messages,
            options: context.SingleAgentRunOptions,
            cancellationToken: cancellationToken);

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

        return run.OutgoingEvents.ToList();
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

        await foreach (var update in run.WatchStreamAsync(cancellationToken).WithCancellation(cancellationToken))
            yield return update;
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
            messages ?? chatRequest.Messages.ToMessages().ToList(),
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
