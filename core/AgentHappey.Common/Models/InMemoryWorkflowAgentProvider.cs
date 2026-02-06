using System.Collections.Concurrent;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows.Declarative;
using Microsoft.Extensions.AI;

namespace AgentHappey.Common.Models;

public sealed class InMemoryWorkflowAgentProvider : WorkflowAgentProvider
{
    private sealed class ConversationState
    {
        public ConcurrentQueue<ChatMessage> OrderedMessages { get; } = new();
        public ConcurrentDictionary<string, ChatMessage> MessagesById { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, AgentSession> ThreadsByAgentId { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly ConcurrentDictionary<string, ConversationState> _conversations = new();
    private readonly ConcurrentDictionary<string, AIAgent> _agents = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<AITool> _tools = [];

    public InMemoryWorkflowAgentProvider(
        IEnumerable<(string agentKey, AIAgent agent)> agents,
        IEnumerable<AITool>? functions = null,
        bool allowConcurrentInvocation = false,
        bool allowMultipleToolCalls = false)
    {
        foreach (var (key, agent) in agents)
            _agents[key] = agent;

        Functions = functions?.Cast<AIFunction>();
        _tools = functions?.ToList() ?? [];
        AllowConcurrentInvocation = allowConcurrentInvocation;
        AllowMultipleToolCalls = allowMultipleToolCalls;
    }

    public override Task<string> CreateConversationAsync(CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid().ToString("n");
        _conversations[id] = new ConversationState();
        return Task.FromResult(id);
    }

    public override Task<ChatMessage> CreateMessageAsync(
        string conversationId,
        ChatMessage conversationMessage,
        CancellationToken cancellationToken = default)
    {
        var state = GetConversation(conversationId);

        if (string.IsNullOrWhiteSpace(conversationMessage.MessageId))
            conversationMessage.MessageId = Guid.NewGuid().ToString("n");

        state.OrderedMessages.Enqueue(conversationMessage);
        state.MessagesById[conversationMessage.MessageId] = conversationMessage;

        return Task.FromResult(conversationMessage);
    }

    public override Task<ChatMessage> GetMessageAsync(
        string conversationId,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        var state = GetConversation(conversationId);

        if (state.MessagesById.TryGetValue(messageId, out var msg))
            return Task.FromResult(msg);

        throw new KeyNotFoundException($"Message '{messageId}' not found in conversation '{conversationId}'.");
    }

    public override async IAsyncEnumerable<ChatMessage> GetMessagesAsync(
        string conversationId,
        int? limit = null,
        string? after = null,
        string? before = null,
        bool newestFirst = false,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var state = GetConversation(conversationId);
        var list = state.OrderedMessages.ToArray();

        int start = 0;
        int end = list.Length;

        if (!string.IsNullOrEmpty(after))
        {
            var idx = Array.FindIndex(list, m => m.MessageId == after);
            if (idx >= 0) start = idx + 1;
        }

        if (!string.IsNullOrEmpty(before))
        {
            var idx = Array.FindIndex(list, m => m.MessageId == before);
            if (idx >= 0) end = idx;
        }

        var slice = list.Skip(start).Take(Math.Max(0, end - start));
        if (newestFirst) slice = slice.Reverse();
        if (limit is > 0) slice = slice.Take(limit.Value);

        foreach (var msg in slice)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return msg;
        }

        await Task.CompletedTask;
    }

    public override async IAsyncEnumerable<AgentResponseUpdate> InvokeAgentAsync(
        string agentId,
        string? agentVersion,
        string? conversationId,
        IEnumerable<ChatMessage>? messages,
        IDictionary<string, object?>? inputArguments,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!_agents.TryGetValue(agentId, out var agent))
            throw new KeyNotFoundException($"Unknown agentId '{agentId}'. (Registered: {string.Join(", ", _agents.Keys)})");

        conversationId ??= await CreateConversationAsync(cancellationToken);
        var state = GetConversation(conversationId);

        if (messages is not null)
        {
            foreach (var m in messages)
                await CreateMessageAsync(conversationId, m, cancellationToken);
        }

        var runMessages = messages ?? state.OrderedMessages.ToArray();

        // per conversation + agent een eigen thread
        var threadItem = await agent.CreateSessionAsync(cancellationToken);
        var thread = state.ThreadsByAgentId.GetOrAdd(agentId, _ => threadItem);

        var chatOptions = new ChatOptions { Tools = [.. _tools ?? []] };

        // minimal: inputArguments nog niet gemapt naar AgentRunOptions (kan later)
        await foreach (var update in agent.RunStreamingAsync(runMessages, thread, options: new ChatClientAgentRunOptions()
        {
            ChatOptions = chatOptions
        }, cancellationToken))
        {
            yield return update;
        }
    }

    private ConversationState GetConversation(string conversationId)
        => _conversations.TryGetValue(conversationId, out var s)
            ? s
            : throw new KeyNotFoundException($"Conversation '{conversationId}' not found.");
}
