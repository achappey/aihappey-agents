using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;

namespace AgentHappey.Core.ChatClient;

public partial class AgentChatClient
{
    // The UI history is the entire continuation. Never register ai_input_required
    // as a model-visible tool or retain a suspended MCP operation in this client.
    private async Task<IReadOnlyList<ChatMessage>> RestoreInputRequiredAsync(
        IEnumerable<ChatMessage> history, CancellationToken cancellationToken)
    {
        var messages = history.ToList();
        var restored = new List<ChatMessage>();
        var syntheticIds = messages.SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>()
            .Where(call => call.Name == "ai_input_required")
            .Select(call => call.CallId).ToHashSet(StringComparer.Ordinal);
        // A fulfilled form is resumable only at the current conversation frontier.
        // Once a normal assistant/user turn follows it, the original MCP call has
        // already been consumed; historical forms must never execute again.
        var frontierCallId = FindInputRequiredFrontier(messages);
        foreach (var message in messages)
        {
            var contents = new List<AIContent>();
            foreach (var content in message.Contents)
            {
                if (content is FunctionResultContent resultContent
                    && syntheticIds.Contains(resultContent.CallId))
                    continue;
                if (content is not FunctionCallContent { Name: "ai_input_required" } call)
                {
                    contents.Add(content);
                    continue;
                }

                // Old synthetic interactions have no persisted MCP output. Omit
                // both sides rather than inventing a result or retrying the tool.
                if (call.CallId != frontierCallId)
                    continue;

                var metadata = JsonSerializer.SerializeToElement(call.RawRepresentation, JsonSerializerOptions.Web);
                if (!metadata.TryGetProperty("provider_metadata", out var providers)
                    || !providers.TryGetProperty(agent.Name, out var owner)
                    || !owner.TryGetProperty("agent_name", out var ownerName)
                    || ownerName.GetString() != agent.Name)
                    continue;

                var resultMessage = messages.FirstOrDefault(m => m.Role == ChatRole.Tool
                    && m.Contents.OfType<FunctionResultContent>().Any(r => r.CallId == call.CallId));
                var result = resultMessage?.Contents.OfType<FunctionResultContent>()
                    .FirstOrDefault(r => r.CallId == call.CallId);
                // State-only requests have no form and no implicit retry. They are
                // eligible only after the client defines an explicit continuation.
                if (result is null)
                    continue;

                var toolName = owner.TryGetProperty("mcp_tool_name", out var toolProperty)
                    ? toolProperty.GetString() : null;
                var url = owner.TryGetProperty("mcp_server_url", out var serverProperty)
                    ? serverProperty.GetString() : null;
                if (string.IsNullOrWhiteSpace(toolName) || string.IsNullOrWhiteSpace(url))
                    throw new InvalidOperationException("Fulfilled ai_input_required is missing its original MCP tool identity.");
                if (!inputRequiredTools.TryGetValue(toolName, out var tool)
                    || tool.ServerUrl != url || tool.McpName != toolName)
                    throw new InvalidOperationException("Fulfilled ai_input_required does not match an enabled MCP tool and server.");

                var inputKey = owner.TryGetProperty("mcp_input_key", out var keyProperty)
                    ? keyProperty.GetString() : null;
                if (string.IsNullOrWhiteSpace(inputKey))
                    throw new InvalidOperationException("Fulfilled ai_input_required is missing its MCP input key.");

                var output = JsonSerializer.SerializeToElement(result.Result, JsonSerializerOptions.Web);
                // ToolInvocationPart maps its CallToolResult directly; ToolCallPart
                // maps it through the ordinary tool-output envelope.
                if (output.ValueKind == JsonValueKind.Object
                    && output.TryGetProperty("output", out var wrapped))
                    output = wrapped;
                if (output.ValueKind != JsonValueKind.Object
                    || !output.TryGetProperty("structuredContent", out var structured)
                    || structured.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException("Fulfilled ai_input_required must contain an MCP elicitation result in structuredContent.");
                var elicitation = structured.Deserialize<ElicitResult>(JsonSerializerOptions.Web)
                    ?? throw new InvalidOperationException("Invalid elicitation result.");
                if (!owner.TryGetProperty("mcp_arguments", out var argumentsProperty))
                    throw new InvalidOperationException("Fulfilled ai_input_required is missing the original MCP arguments.");
                var arguments = argumentsProperty
                    .Deserialize<Dictionary<string, JsonElement>>(JsonSerializerOptions.Web);
                var request = new CallToolRequestParams
                {
                    Name = toolName,
                    Arguments = arguments,
                    InputResponses = new Dictionary<string, InputResponse>
                    {
                        [inputKey] = InputResponse.FromElicitResult(elicitation)
                    },
                    RequestState = owner.TryGetProperty("mcp_request_state", out var state)
                        && state.ValueKind == JsonValueKind.String
                        ? state.GetString() : null
                };

                var retry = await tool.RetryAsync(request, cancellationToken);
                if (retry is McpToolInputRequiredException)
                    throw new InvalidOperationException("MCP requires another input request; a new explicit user interaction is required.");

                contents.Add(new FunctionCallContent(call.CallId, toolName,
                    arguments?.ToDictionary(entry => entry.Key, entry => (object?)entry.Value)
                        ?? new Dictionary<string, object?>()));
                restored.Add(new ChatMessage(ChatRole.Assistant, contents) { MessageId = message.MessageId });
                contents = [];
                restored.Add(new ChatMessage(ChatRole.Tool,
                    [new FunctionResultContent(call.CallId, retry)]) { MessageId = call.CallId });
            }
            if (contents.Count > 0)
                restored.Add(new ChatMessage(message.Role, contents) { MessageId = message.MessageId });
        }

        return restored;
    }

    private static string? FindInputRequiredFrontier(IReadOnlyList<ChatMessage> messages)
    {
        var calls = messages.SelectMany((message, index) => message.Contents
                .OfType<FunctionCallContent>()
                .Where(call => call.Name == "ai_input_required")
                .Select(call => (call, index)))
            .ToList();
        if (calls.Count == 0)
            return null;

        var (latest, callIndex) = calls[^1];
        var resultIndex = -1;
        for (var index = callIndex; index < messages.Count; index++)
        {
            if (messages[index].Role == ChatRole.Tool && messages[index].Contents
                .OfType<FunctionResultContent>().Any(result => result.CallId == latest.CallId))
            {
                resultIndex = index;
                break;
            }
        }
        if (resultIndex < 0)
            return null;

        for (var index = resultIndex + 1; index < messages.Count; index++)
        {
            var message = messages[index];
            if (message.Role != ChatRole.User && message.Role != ChatRole.Assistant)
                continue;
            if (message.Contents.Any(content => content switch
                {
                    TextContent text => !string.IsNullOrWhiteSpace(text.Text),
                    FunctionCallContent call => call.Name != "ai_input_required"
                        && !call.InformationalOnly,
                    DataContent => true,
                    _ => false
                }))
                return null;
        }

        return latest.CallId;
    }
}
