using System.Text.Json;
using AgentHappey.Common.Extensions;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using Microsoft.Extensions.AI;

namespace AgentHappey.Core.ChatClient;

public partial class AgentChatClient
{
    private void AppendGatewayTransport(List<AIContent> parts, JsonElement item)
    {
        var id = GatewayTransport.String(item, "id");
        var name = GatewayTransport.String(item, "name");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) return;

        JsonElement? result = null;
        var output = GatewayTransport.String(item, "output");
        if (!string.IsNullOrWhiteSpace(output))
        {
            // Keep a JSON copy rather than narrowing through CallToolResult: MCP
            // _meta, unknown content blocks and future result fields must survive.
            result = JsonSerializer.Deserialize<JsonElement>(output);
            if (result.Value.ValueKind != JsonValueKind.Object
                || !result.Value.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                throw new JsonException("Gateway MCP output must be a complete CallToolResult.");
        }

        var metadata = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
        string? title = null;
        var provider = GetProviderKey();
        if (result is { } payload && payload.TryGetProperty("_meta", out var meta) && meta.ValueKind == JsonValueKind.Object
            && meta.TryGetProperty(GatewayTransport.MetadataKey, out var transport) && transport.ValueKind == JsonValueKind.Object)
        {
            provider = GatewayTransport.String(transport, "provider") ?? provider;
            title = GatewayTransport.String(transport, "title");
            if (transport.TryGetProperty("providerMetadata", out var original) && original.ValueKind == JsonValueKind.Object)
                foreach (var scope in original.EnumerateObject().Where(scope => scope.Value.ValueKind == JsonValueKind.Object))
                    metadata[scope.Name] = scope.Value.EnumerateObject().ToDictionary(field => field.Name, field => (object)field.Value.Clone());
        }
        if (!metadata.TryGetValue(provider, out var values)) metadata[provider] = values = [];
        values["server_label"] = GatewayTransport.ServerLabel;
        values["gateway_synthetic"] = true;
        values["type"] = "mcp_call";

        var raw = CreateResponseItemRawRepresentation(item);
        raw["provider_metadata"] = metadata;
        raw["title"] = title ?? name;
        parts.Add(new FunctionCallContent(id, name, DeserializeArguments(GatewayTransport.String(item, "arguments")))
        {
            InformationalOnly = true,
            RawRepresentation = raw
        });
        if (result is { } completed)
            parts.Add(new FunctionResultContent(id, new Dictionary<string, object?>
            {
                ["output"] = completed,
                ["provider_executed"] = true,
                ["provider_metadata"] = metadata
            }) { RawRepresentation = raw });
    }

    private IEnumerable<ChatResponseUpdate> CreateGatewayTransportUpdates(JsonElement item, StreamingResponseState state)
    {
        var id = GatewayTransport.String(item, "id");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(GatewayTransport.String(item, "output"))
            || !state.CompletedGatewayItems.Add(id)) yield break;
        var contents = new List<AIContent>();
        AppendGatewayTransport(contents, item);
        yield return CreateStreamingUpdate(ChatRole.Assistant, contents, id);
    }

    private static bool IsGatewayStreamItem(ResponseStreamItem item)
        => item.Type == "mcp_call" && item.AdditionalProperties?.TryGetValue("server_label", out var label) == true
            && label.ValueKind == JsonValueKind.String && label.GetString() == GatewayTransport.ServerLabel;
}
