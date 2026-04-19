using System.Text.Json;
using AgentHappey.Common.Models;
using AgentHappey.Core.ChatRuntime;
using AIHappey.Responses;
using AIHappey.Responses.Mapping;
using AIHappey.Unified.Models;
using Microsoft.Extensions.AI;

namespace AgentHappey.Core.Responses;

public static class ResponsesRuntimeMapper
{
    private const string ProviderId = "agenthappey-agents";

    public static ChatRuntimeRequest ToChatRuntimeRequest(this ResponseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var unified = request.ToUnifiedRequest(ProviderId);
        var messages = ToChatMessages(unified.Input).ToList();
        var models = ReadStringArray(request.AdditionalProperties, "models");
        var agents = ReadAgents(request.Metadata);
        var workflowType = ReadString(request.AdditionalProperties, "workflowType")
            ?? ReadString(request.AdditionalProperties, "workflow_type")
            ?? "sequential";
        var workflowMetadata = ReadObject<WorkflowMetadata>(request.AdditionalProperties, "workflowMetadata")
            ?? ReadObject<WorkflowMetadata>(request.AdditionalProperties, "workflow_metadata");

        return new ChatRuntimeRequest(
            messages,
            request.Model,
            models,
            agents,
            workflowType,
            workflowMetadata);
    }

    private static IEnumerable<ChatMessage> ToChatMessages(AIInput? input)
    {
        if (!string.IsNullOrWhiteSpace(input?.Text))
            yield return new ChatMessage(ChatRole.User, input.Text!);

        foreach (var item in input?.Items ?? [])
            foreach (var message in ToChatMessages(item))
                yield return message;
    }

    private static IEnumerable<ChatMessage> ToChatMessages(AIInputItem item)
    {
        switch (item.Type)
        {
            case "message":
                yield return new ChatMessage(ParseRole(item.Role), [.. ToContents(item)])
                {
                    MessageId = item.Id
                };
                yield break;

            case "function_call":
                {
                    var toolCall = item.Content?.OfType<AIToolCallContentPart>().FirstOrDefault();
                    if (toolCall is null)
                        yield break;

                    yield return new ChatMessage(ChatRole.Assistant,
                    [
                        new FunctionCallContent(
                            toolCall.ToolCallId,
                            toolCall.ToolName ?? toolCall.Title ?? "tool",
                            NormalizeObject(toolCall.Input) as IDictionary<string, object?> ?? new Dictionary<string, object?>())
                    ])
                    {
                        MessageId = item.Id ?? toolCall.ToolCallId
                    };
                    yield break;
                }

            case "function_call_output":
                {
                    var toolCall = item.Content?.OfType<AIToolCallContentPart>().FirstOrDefault();
                    if (toolCall is null)
                        yield break;

                    yield return new ChatMessage(ChatRole.Tool,
                    [
                        new FunctionResultContent(toolCall.ToolCallId, NormalizeObject(toolCall.Output) ?? new { })
                    ])
                    {
                        MessageId = item.Id ?? toolCall.ToolCallId
                    };
                    yield break;
                }

            case "reasoning":
                {
                    var text = string.Join("\n", item.Content?.OfType<AITextContentPart>().Select(part => part.Text) ?? []);
                    if (string.IsNullOrWhiteSpace(text))
                        yield break;

                    yield return new ChatMessage(ChatRole.Assistant,
                    [
                        new TextReasoningContent(text)
                    ])
                    {
                        MessageId = item.Id
                    };
                    yield break;
                }

            case "compaction":
                yield break;

            case "item_reference":
                throw new NotSupportedException("Responses item_reference replay is not supported by the agents runtime.");

            default:
                if (item.Content?.Count > 0)
                {
                    yield return new ChatMessage(ParseRole(item.Role), [.. ToContents(item)])
                    {
                        MessageId = item.Id
                    };
                }

                yield break;
        }
    }

    private static IEnumerable<AIContent> ToContents(AIInputItem item)
    {
        foreach (var part in item.Content ?? [])
        {
            switch (part)
            {
                case AITextContentPart text:
                    yield return new TextContent(text.Text);
                    break;

                case AIReasoningContentPart reasoning when !string.IsNullOrWhiteSpace(reasoning.Text):
                    yield return new TextReasoningContent(reasoning.Text!);
                    break;

                case AIFileContentPart file:
                    {
                        var data = file.Data?.ToString();
                        if (string.IsNullOrWhiteSpace(data))
                            break;

                        var mediaType = file.MediaType ?? "application/octet-stream";
                        yield return new DataContent(data, mediaType)
                        {
                            Name = file.Filename
                        };
                        break;
                    }

                case AIToolCallContentPart toolCall:
                    yield return new FunctionCallContent(
                        toolCall.ToolCallId,
                        toolCall.ToolName ?? toolCall.Title ?? "tool",
                        NormalizeObject(toolCall.Input) as IDictionary<string, object?> ?? new Dictionary<string, object?>());
                    break;
            }
        }
    }

    private static ChatRole ParseRole(string? role) => role?.Trim().ToLowerInvariant() switch
    {
        "assistant" => ChatRole.Assistant,
        "system" => ChatRole.System,
        "developer" => ChatRole.System,
        "tool" => ChatRole.Tool,
        _ => ChatRole.User
    };

    private static IReadOnlyList<string>? ReadStringArray(Dictionary<string, JsonElement>? source, string key)
    {
        if (source is null || !source.TryGetValue(key, out var element) || element.ValueKind != JsonValueKind.Array)
            return null;

        var values = element
            .EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .ToList();

        return values.Count == 0 ? null : values;
    }

    private static string? ReadString(Dictionary<string, JsonElement>? source, string key)
        => source is not null && source.TryGetValue(key, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static T? ReadObject<T>(Dictionary<string, JsonElement>? source, string key)
    {
        if (source is null || !source.TryGetValue(key, out var element))
            return default;

        return JsonSerializer.Deserialize<T>(element.GetRawText(), ResponseJson.Default);
    }

    private static IReadOnlyList<Agent>? ReadAgents(Dictionary<string, object?>? metadata)
    {
        if (metadata is null || !metadata.TryGetValue("agents", out var agents) || agents is null)
            return null;

        var normalized = NormalizeObject(agents);
        var result = JsonSerializer.Deserialize<List<Agent>>(
            JsonSerializer.Serialize(normalized, JsonSerializerOptions.Web),
            JsonSerializerOptions.Web);

        return result is { Count: > 0 } ? result : null;
    }

    private static object? NormalizeObject(object? value)
    {
        if (value is null)
            return null;

        if (value is JsonElement jsonElement)
            return JsonSerializer.Deserialize<object>(jsonElement.GetRawText(), JsonSerializerOptions.Web);

        return JsonSerializer.Deserialize<object>(JsonSerializer.Serialize(value, JsonSerializerOptions.Web), JsonSerializerOptions.Web);
    }
}
