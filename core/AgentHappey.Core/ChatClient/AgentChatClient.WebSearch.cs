using System.Text.Json;
using AgentHappey.Common.Extensions;
using Microsoft.Extensions.AI;

namespace AgentHappey.Core.ChatClient;

public partial class AgentChatClient
{
    private void AppendWebSearchCall(List<AIContent> parts, JsonElement item)
    {
        var id = GatewayTransport.String(item, "id");
        if (string.IsNullOrWhiteSpace(id)
            || !item.TryGetProperty("action", out var action)
            || action.ValueKind != JsonValueKind.Object)
            return;

        // Preserve provider metadata verbatim. The agent-owned entry carries the
        // complete gateway item without pretending that an Anthropic search is
        // an OpenAI tool or replacing the provider's own type/id fields.
        var metadata = item.TryGetProperty("provider_metadata", out var providerMetadata)
            && providerMetadata.ValueKind == JsonValueKind.Object
                ? providerMetadata.Deserialize<Dictionary<string, Dictionary<string, object>?>>(JsonSerializerOptions.Web)
                : null;
        metadata ??= new(StringComparer.Ordinal);
        var owned = metadata.TryGetValue(agent.Name, out var existing) && existing is not null
            ? new Dictionary<string, object>(existing, StringComparer.Ordinal)
            : new Dictionary<string, object>(StringComparer.Ordinal);
        owned["agent_name"] = agent.Name;
        owned["type"] = "web_search_call";
        owned["id"] = id;
        owned["responses_item"] = item.Clone();
        metadata[agent.Name] = owned;

        var raw = CreateResponseItemRawRepresentation(item);
        raw["responses_type"] = "web_search_call";
        raw["provider_metadata"] = metadata;
        raw["title"] = "web_search";
        parts.Add(new FunctionCallContent(id, "web_search", DeserializeArguments(action.GetRawText()))
        {
            InformationalOnly = true,
            RawRepresentation = raw
        });

        // A web_search_call contains its own result, unlike a client function.
        // This paired result completes the UI lifecycle only; neither native
        // Responses output nor inference replay may turn it into a function output.
        if (GatewayTransport.String(item, "status") is null or "completed")
            parts.Add(new FunctionResultContent(id, new Dictionary<string, object?>
            {
                ["output"] = new { action = action.Clone(), status = "completed" },
                ["provider_executed"] = true,
                ["preliminary"] = false,
                ["provider_metadata"] = metadata,
                ["responses_item"] = item.Clone()
            }) { RawRepresentation = raw });
    }

    private IEnumerable<ChatResponseUpdate> CreateWebSearchUpdates(JsonElement item, StreamingResponseState state)
    {
        var id = GatewayTransport.String(item, "id");
        if (string.IsNullOrWhiteSpace(id) || state.CompletedWebSearchItems.Contains(id))
            yield break;
        var parts = new List<AIContent>();
        AppendWebSearchCall(parts, item);
        if (parts.Count == 0) yield break;
        if (parts.OfType<FunctionResultContent>().Any())
            state.CompletedWebSearchItems.Add(id);
        yield return CreateStreamingUpdate(ChatRole.Assistant, parts, id);
    }
}
