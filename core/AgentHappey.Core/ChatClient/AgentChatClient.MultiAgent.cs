using System.Text.Json;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using Microsoft.Extensions.AI;

namespace AgentHappey.Core.ChatClient;

public partial class AgentChatClient
{
    private const string NativeResponsesOutputMetadataKey = "responses.native_output";

    private bool TryCreateMultiAgentCallUpdate(ResponseStreamItem item, out ChatResponseUpdate update)
    {
        update = null!;
        var callId = item.CallId ?? GetAdditionalPropertyString(item.AdditionalProperties, "call_id") ?? item.Id;
        if (string.IsNullOrWhiteSpace(callId))
            return false;

        var action = GetAdditionalPropertyString(item.AdditionalProperties, "action") ?? "multi_agent";
        var arguments = item.Arguments is { } argumentsElement
            ? DeserializeArguments(argumentsElement.ValueKind == JsonValueKind.String
                ? argumentsElement.GetString()
                : argumentsElement.GetRawText())
            : new Dictionary<string, object?>();
        var nativeItem = JsonSerializer.SerializeToElement(item, ResponseJson.Default);

        update = CreateNativeMultiAgentUpdate(
            item,
            ChatRole.Assistant,
            [new FunctionCallContent(callId, action, arguments)
            {
                InformationalOnly = true,
                RawRepresentation = new Dictionary<string, object?>
                {
                    ["responses_type"] = "multi_agent_call",
                    ["item_id"] = item.Id,
                    ["call_id"] = callId,
                    ["title"] = action,
                    ["responses_item"] = nativeItem,
                    ["provider_metadata"] = CreateMultiAgentProviderMetadata(item, nativeItem)
                }
            }]);
        return true;
    }

    private bool TryCreateMultiAgentOutputUpdate(ResponseStreamItem item, out ChatResponseUpdate update)
    {
        update = null!;
        var callId = item.CallId ?? GetAdditionalPropertyString(item.AdditionalProperties, "call_id") ?? item.Id;
        if (string.IsNullOrWhiteSpace(callId))
            return false;

        var output = GetAdditionalPropertyValue(item.AdditionalProperties, "output") ?? Array.Empty<object>();
        var nativeItem = JsonSerializer.SerializeToElement(item, ResponseJson.Default);
        var envelope = new Dictionary<string, object?>
        {
            ["output"] = output,
            ["preliminary"] = false,
            ["provider_executed"] = true,
            ["provider_metadata"] = CreateMultiAgentProviderMetadata(item, nativeItem),
            ["responses_item"] = nativeItem
        };

        update = CreateNativeMultiAgentUpdate(
            item,
            ChatRole.Assistant,
            [new FunctionResultContent(callId, envelope)]);
        return true;
    }

    private ChatResponseUpdate CreateNativeMultiAgentUpdate(
        ResponseStreamItem item,
        ChatRole role,
        IReadOnlyList<AIContent> contents)
        => new(role, [.. contents])
        {
            MessageId = item.Id ?? item.CallId ?? Guid.NewGuid().ToString("N"),
            AuthorName = item.Agent?.AgentName ?? agent.Name,
            ModelId = GetStreamingModelId()
        };

    private Dictionary<string, Dictionary<string, object>?> CreateMultiAgentProviderMetadata(
        ResponseStreamItem item,
        JsonElement nativeItem)
        => new(StringComparer.Ordinal)
        {
            [GetProviderKey()] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = item.Type,
                ["id"] = item.Id,
                ["call_id"] = item.CallId ?? GetAdditionalPropertyString(item.AdditionalProperties, "call_id"),
                ["action"] = GetAdditionalPropertyString(item.AdditionalProperties, "action"),
                ["agent"] = item.Agent,
                ["agent_name"] = item.Agent?.AgentName,
                ["responses_item"] = nativeItem
            }
        };

    private bool TryCreateAuthoritativeMultiAgentFinalUpdate(
        ResponseResult response,
        out ChatResponseUpdate update)
    {
        update = null!;
        var final = (response.Output ?? [])
            .Select(item => JsonSerializer.SerializeToElement(item, ResponseJson.Default))
            .Where(item => item.ValueKind == JsonValueKind.Object
                           && item.TryGetProperty("type", out var type)
                           && type.GetString() == "message"
                           && item.TryGetProperty("role", out var role)
                           && role.GetString() == "assistant")
            .Select(item => new
            {
                Item = item,
                Text = ExtractNativeResponseMessageText(item),
                Phase = item.TryGetProperty("phase", out var phase) ? phase.GetString() : null
            })
            .Where(entry => !string.IsNullOrEmpty(entry.Text))
            .OrderBy(entry => string.Equals(entry.Phase, "final_answer", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .LastOrDefault(entry => string.Equals(entry.Phase, "final_answer", StringComparison.OrdinalIgnoreCase))
            ?? (response.Output ?? [])
                .Select(item => JsonSerializer.SerializeToElement(item, ResponseJson.Default))
                .Where(item => item.ValueKind == JsonValueKind.Object
                               && item.TryGetProperty("type", out var type)
                               && type.GetString() == "message"
                               && item.TryGetProperty("role", out var role)
                               && role.GetString() == "assistant")
                .Select(item => new
                {
                    Item = item,
                    Text = ExtractNativeResponseMessageText(item),
                    Phase = item.TryGetProperty("phase", out var phase) ? phase.GetString() : null
                })
                .LastOrDefault(entry => !string.IsNullOrEmpty(entry.Text));

        if (final is null)
            return false;

        var itemId = final.Item.TryGetProperty("id", out var id) ? id.GetString() : null;
        var authorName = final.Item.TryGetProperty("agent", out var agentElement)
                         && agentElement.ValueKind == JsonValueKind.Object
                         && agentElement.TryGetProperty("agent_name", out var agentName)
            ? agentName.GetString()
            : agent.Name;

        update = new ChatResponseUpdate(ChatRole.Assistant, [new TextContent(final.Text)])
        {
            MessageId = itemId ?? Guid.NewGuid().ToString("N"),
            AuthorName = authorName,
            ModelId = GetStreamingModelId()
        };
        return true;
    }

    private static string ExtractNativeResponseMessageText(JsonElement item)
    {
        if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return string.Empty;

        return string.Concat(content.EnumerateArray()
            .Where(part => part.ValueKind == JsonValueKind.Object
                           && part.TryGetProperty("type", out var type)
                           && type.GetString() == "output_text")
            .Select(part => part.TryGetProperty("text", out var text) ? text.GetString() : null)
            .Where(text => text is not null));
    }

    private Dictionary<string, object?> CreateResponseCompletionMetadata(ResponseResult response)
    {
        var metadata = response.Metadata?.ToDictionary(entry => entry.Key, entry => entry.Value)
                       ?? new Dictionary<string, object?>(StringComparer.Ordinal);
        var providerMetadata = metadata.TryGetValue("providerMetadata", out var providerValue)
            ? ToMetadataDictionary(providerValue ?? new { })
            : new Dictionary<string, object?>(StringComparer.Ordinal);
        var providerKey = GetProviderKey();
        var scoped = providerMetadata.TryGetValue(providerKey, out var scopedValue)
            ? ToMetadataDictionary(scopedValue ?? new { })
            : new Dictionary<string, object?>(StringComparer.Ordinal);

        if ((response.Output ?? []).Any(item => string.Equals(
                JsonSerializer.SerializeToElement(item, ResponseJson.Default).GetProperty("type").GetString(),
                "multi_agent_call",
                StringComparison.OrdinalIgnoreCase)))
        {
            scoped[NativeResponsesOutputMetadataKey] = JsonSerializer.SerializeToElement(response.Output, ResponseJson.Default);
        }

        providerMetadata[providerKey] = scoped;
        metadata["providerMetadata"] = providerMetadata;
        return metadata;
    }

    private static Dictionary<string, object?> ToMetadataDictionary(object value)
    {
        if (value is Dictionary<string, object?> dictionary)
            return dictionary.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        if (value is JsonElement json && json.ValueKind == JsonValueKind.Object)
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(json.GetRawText(), JsonSerializerOptions.Web) ?? [];
        return JsonSerializer.Deserialize<Dictionary<string, object?>>(
                   JsonSerializer.Serialize(value, JsonSerializerOptions.Web),
                   JsonSerializerOptions.Web)
               ?? [];
    }
}
