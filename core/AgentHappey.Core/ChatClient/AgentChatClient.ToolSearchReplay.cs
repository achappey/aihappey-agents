using System.Text.Json;
using AIHappey.Responses;
using Microsoft.Extensions.AI;

namespace AgentHappey.Core.ChatClient;

public partial class AgentChatClient
{
    private sealed class ToolSearchReplayState
    {
        public Dictionary<string, ResponseToolSearchCallItem> Calls { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Outputs { get; } = new(StringComparer.Ordinal);
        public HashSet<string> IgnoredCalls { get; } = new(StringComparer.Ordinal);
        public HashSet<string> OrdinaryCalls { get; } = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> itemCallIds = new(StringComparer.Ordinal);
        public Dictionary<string, string> OutputItemCallIds { get; } = new(StringComparer.Ordinal);

        public bool AddCall(string lifecycleId, ResponseToolSearchCallItem call)
        {
            if (string.IsNullOrWhiteSpace(lifecycleId))
                throw new InvalidOperationException("Tool search call is missing its lifecycle ID.");
            if (OrdinaryCalls.Contains(lifecycleId))
                throw new InvalidOperationException("Tool search call ID conflicts with an ordinary function call.");
            if (!string.IsNullOrWhiteSpace(call.Id))
            {
                if (itemCallIds.TryGetValue(call.Id, out var previousCallId) && previousCallId != lifecycleId)
                    throw new InvalidOperationException("Native tool search item ID is reused by a different call.");
                itemCallIds[call.Id] = lifecycleId;
            }
            if (Calls.TryGetValue(lifecycleId, out var previous))
            {
                if (JsonSerializer.Serialize(previous, ResponseJson.Default) != JsonSerializer.Serialize(call, ResponseJson.Default))
                    throw new InvalidOperationException($"Conflicting tool search calls for '{lifecycleId}'.");
                return false;
            }
            Calls.Add(lifecycleId, call);
            return true;
        }
    }

    private static bool HasToolSearchMetadata(object? raw)
    {
        var json = TryReadRawRepresentation(raw);
        if (json is not { ValueKind: JsonValueKind.Object }) return false;
        if (ReadResponseMetadataString(raw, "responses_type") is "tool_search_call" or "tool_search_output"
            || ReadNestedResponseItemType(raw) is "tool_search_call" or "tool_search_output") return true;
        return json.Value.TryGetProperty("provider_metadata", out var providers)
            && providers.ValueKind == JsonValueKind.Object
            && providers.EnumerateObject().Any(p => IsSearchMetadataObject(p.Value));
    }

    private static bool IsSearchMetadataObject(JsonElement value)
        => value.ValueKind == JsonValueKind.Object
            && (value.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                && type.GetString() is "tool_search_call" or "tool_search_output"
                || value.TryGetProperty("responses_item", out var item) && item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("type", out var itemType) && itemType.ValueKind == JsonValueKind.String
                && itemType.GetString() is "tool_search_call" or "tool_search_output");

    private bool IsCurrentToolSearchProvider(object? raw)
    {
        // Native OpenAI search items must not be replayed into another provider.
        if (!string.Equals(GetProviderKey(), "openai", StringComparison.Ordinal)) return false;
        var json = TryReadRawRepresentation(raw);
        if (json is { ValueKind: JsonValueKind.Object }
            && json.Value.TryGetProperty("provider_metadata", out var providers)
            && providers.ValueKind == JsonValueKind.Object
            && providers.EnumerateObject().Any(p => IsSearchMetadataObject(p.Value)))
            return providers.TryGetProperty("openai", out var scoped) && IsSearchMetadataObject(scoped);
        return true; // Live/legacy raw representations carry top-level native identity.
    }

    private bool TryReadSearchItem<T>(object? raw, out T item) where T : ResponseInputItem, new()
    {
        item = null!;
        if (!IsCurrentToolSearchProvider(raw) || !IsOwnedContinuation(raw)) return false;
        var json = TryReadRawRepresentation(raw);
        if (json is not { ValueKind: JsonValueKind.Object }) return false;
        if (json.Value.TryGetProperty("provider_metadata", out var providers)
            && providers.ValueKind == JsonValueKind.Object
            && providers.TryGetProperty("openai", out var provider)
            && provider.ValueKind == JsonValueKind.Object)
        {
            if (TryReadResponseItem<T>(provider, out item)) return true;
            if (provider.TryGetProperty("type", out var type) && type.GetString() == new T().Type)
            {
                item = provider.Deserialize<T>(ResponseJson.Default)!;
                return item is not null;
            }
        }
        return TryReadResponseItem<T>(raw, out item);
    }

    private static void ValidateSearchCall(string lifecycleId, ResponseToolSearchCallItem call)
    {
        if (call.Execution is not ("client" or "server") || call.Status is not (null or "completed" or "in_progress" or "incomplete")
            || call.Arguments.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Invalid native tool search call metadata.");
        if (call.Execution == "client" && (string.IsNullOrWhiteSpace(call.CallId) || call.CallId != lifecycleId))
            throw new InvalidOperationException("Native client tool search call IDs are missing or mismatched.");
    }

    private bool TryCreateToolSearchOutputItem(FunctionResultContent result, ToolSearchReplayState replay,
        out ResponseToolSearchOutputItem? output)
    {
        output = null;
        if (replay.IgnoredCalls.Contains(result.CallId)) return true;
        var hasNativeOutput = TryReadSearchItem<ResponseToolSearchOutputItem>(result.RawRepresentation, out var native)
            || TryReadSearchItem<ResponseToolSearchOutputItem>(result.Result, out native);
        if (!replay.Calls.TryGetValue(result.CallId, out var call))
        {
            if (hasNativeOutput || !replay.OrdinaryCalls.Contains(result.CallId)
                && ContainsSelectedTools(result.Result))
                throw new InvalidOperationException("Orphan or mismatched native tool search output.");
            return false;
        }
        ThrowIfToolSearchFailed(result.Result);
        if (call.Execution == "server")
        {
            // Preserve hosted identity, but persisted history is not an authority
            // for tools: hosted results must obey the same current catalog policy.
            if (!hasNativeOutput) throw new InvalidOperationException("Hosted tool search is missing its native output.");
            if (native.Execution != "server" || native.CallId != call.CallId)
                throw new InvalidOperationException("Hosted tool search output does not match its call.");
            native.Tools = ResolveAuthorizedSearchTools(native.Tools);
            output = native;
        }
        else
        {
            // Inspect failure flags even when a native output is retained alongside
            // the result. Native identity never overrides a failed/denied execution.
            if (hasNativeOutput && (native.CallId != result.CallId || native.Execution != "client"))
                throw new InvalidOperationException("Native tool search output call IDs or execution mode do not match.");
            if (call.Status is not (null or "completed"))
                throw new InvalidOperationException("Cannot load tools from an unfinished tool search call.");
            if (hasNativeOutput && native.Status is not (null or "completed"))
                throw new InvalidOperationException("Cannot load tools from an unfinished tool search output.");
            var selected = hasNativeOutput ? native.Tools : ExtractSelectedToolDefinitions(result.Result);
            output = new ResponseToolSearchOutputItem
            {
                Id = hasNativeOutput ? native.Id : null,
                CallId = result.CallId, Execution = "client", Status = hasNativeOutput ? native.Status : "completed",
                Tools = ResolveAuthorizedSearchTools(selected)
            };
        }
        if (!string.IsNullOrWhiteSpace(output.Id))
        {
            if (replay.OutputItemCallIds.TryGetValue(output.Id, out var previousCallId) && previousCallId != result.CallId)
                throw new InvalidOperationException("Native tool search output item ID is reused by a different call.");
            replay.OutputItemCallIds[output.Id] = result.CallId;
        }
        var fingerprint = JsonSerializer.Serialize(output, ResponseJson.Default);
        if (replay.Outputs.TryGetValue(result.CallId, out var previous))
        {
            if (previous != fingerprint) throw new InvalidOperationException("Conflicting duplicate tool search outputs.");
            output = null;
        }
        else replay.Outputs.Add(result.CallId, fingerprint);
        return true;
    }

    private List<ResponseToolDefinition> ResolveAuthorizedSearchTools(IEnumerable<ResponseToolDefinition> selected)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var selectionOrder = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var tool in FlattenSelectedSearchTools(selected))
        {
            if (tool.Type != "function" || tool.Extra?.TryGetValue("name", out var name) != true
                || name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString()))
                throw new InvalidOperationException("Tool search returned an invalid function definition.");
            var key = name.GetString()!;
            if (!Tools.TryGetValue(key, out var authorized) || authorized is not AIFunctionDeclaration
                || key is ClientToolSearchName or ToolSearchType)
                throw new InvalidOperationException($"Tool search selected unauthorized tool '{key}'.");
            if (seen.Add(key)) selectionOrder[key] = selectionOrder.Count;
        }
        // History selects names only. Schemas, caller restrictions and namespace
        // membership always come from today's authorized, policy-filtered catalog.
        var definitions = BuildResponseToolDefinitions(Tools.Values.ToList(), loadedSearchResults: true) ?? [];
        var loaded = new List<ResponseToolDefinition>();
        foreach (var definition in definitions)
        {
            if (definition.Type == "function" && definition.Extra?.TryGetValue("name", out var name) == true
                && seen.Contains(name.GetString()!))
            {
                definition.Extra.Remove("defer_loading");
                loaded.Add(definition);
            }
            else if (definition.Type == "namespace" && definition.Extra?.TryGetValue("tools", out var nested) == true)
            {
                var members = (nested.Deserialize<List<ResponseToolDefinition>>(ResponseJson.Default) ?? [])
                    .Where(t => t.Extra?.TryGetValue("name", out var member) == true && seen.Contains(member.GetString()!))
                    .OrderBy(t => selectionOrder[t.Extra!["name"].GetString()!]).ToList();
                if (members.Count == 0) continue;
                foreach (var member in members) member.Extra?.Remove("defer_loading");
                definition.Extra["tools"] = JsonSerializer.SerializeToElement(members, ResponseJson.Default);
                loaded.Add(definition);
            }
        }
        return loaded.OrderBy(definition => definition.Type == "namespace"
            ? definition.Extra!["tools"].EnumerateArray().Min(t => selectionOrder[t.GetProperty("name").GetString()!])
            : selectionOrder[definition.Extra!["name"].GetString()!]).ToList();
    }

    private static IEnumerable<ResponseToolDefinition> FlattenSelectedSearchTools(IEnumerable<ResponseToolDefinition> selected)
    {
        foreach (var tool in selected)
        {
            if (tool.Type == "namespace" && tool.Extra?.TryGetValue("tools", out var members) == true
                && members.ValueKind == JsonValueKind.Array)
            {
                foreach (var member in members.Deserialize<List<ResponseToolDefinition>>(ResponseJson.Default) ?? [])
                {
                    if (member.Type != "function") throw new InvalidOperationException("Invalid loaded namespace member.");
                    yield return member;
                }
            }
            else yield return tool;
        }
    }

    private static bool ContainsSelectedTools(object? result)
    {
        var json = TryReadRawRepresentation(result);
        for (var depth = 0; depth < 8 && json is { ValueKind: JsonValueKind.Object }; depth++)
        {
            if (json.Value.TryGetProperty("selectedTools", out _)) return true;
            if (json.Value.TryGetProperty("structuredContent", out var structured)) { json = structured; continue; }
            if (json.Value.TryGetProperty("output", out var output)) { json = output; continue; }
            break;
        }
        return false;
    }

    private static void ThrowIfToolSearchFailed(object? result)
    {
        var json = TryReadRawRepresentation(result);
        for (var depth = 0; depth < 8 && json is { ValueKind: JsonValueKind.Object }; depth++)
        {
            if (json.Value.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True
                || json.Value.TryGetProperty("error", out _)
                || json.Value.TryGetProperty("approved", out var approved) && approved.ValueKind == JsonValueKind.False)
                throw new InvalidOperationException($"Tool search failed or was denied: {json.Value.GetRawText()}");
            if (!json.Value.TryGetProperty("output", out var output)) break;
            json = output;
        }
    }

    private static JsonElement ReadSuccessfulToolSearchPayload(object? result)
    {
        ThrowIfToolSearchFailed(result);
        var json = ToJsonElement(result ?? new { });
        for (var depth = 0; depth < 8; depth++)
        {
            if (json.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Tool search result must be an object.");
            if (json.TryGetProperty("output", out var inner)) { json = inner; continue; }
            return json;
        }
        throw new InvalidOperationException("Tool search result envelope is too deeply nested.");
    }
}
