using System.Text.Json;

namespace AgentHappey.Common.Extensions;

/// <summary>
/// Identifies output-only gateway artifacts by transport identity, never by tool
/// name. This lives below the runtime so UI history and inference replay share
/// the same policy without depending on provider implementations.
/// </summary>
public static class GatewayTransport
{
    public const string ServerLabel = "aihappey";
    public const string MetadataKey = "aihappey/gateway";

    public static bool IsItem(JsonElement item)
        => item.ValueKind == JsonValueKind.Object
            && String(item, "type") == "mcp_call"
            && String(item, "server_label") == ServerLabel;

    public static bool IsOutputOnly(object? value)
    {
        if (value is null) return false;
        var json = value is JsonElement element ? element : JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);
        return IsOutputOnly(json, 0);
    }

    private static bool IsOutputOnly(JsonElement value, int depth)
    {
        if (value.ValueKind != JsonValueKind.Object || depth > 5) return false;
        if (IsItem(value)) return true;
        if (value.TryGetProperty("_meta", out var meta) && meta.ValueKind == JsonValueKind.Object
            && meta.TryGetProperty(MetadataKey, out var gateway) && gateway.ValueKind == JsonValueKind.Object)
            return true;
        if (String(value, "server_label") == ServerLabel
            && value.TryGetProperty("gateway_synthetic", out var synthetic) && synthetic.ValueKind == JsonValueKind.True)
            return true;

        // Visit only known envelope fields, not arbitrary tool arguments/content.
        foreach (var name in new[] { "responses_item", "output", "provider_metadata" })
        {
            if (!value.TryGetProperty(name, out var nested)) continue;
            if (IsOutputOnly(nested, depth + 1)) return true;
            if (name == "provider_metadata" && nested.ValueKind == JsonValueKind.Object
                && nested.EnumerateObject().Any(provider => IsOutputOnly(provider.Value, depth + 1)))
                return true;
        }
        return false;
    }

    public static bool IsProviderMetadata(object? metadata)
        => metadata is not null && IsOutputOnly(new Dictionary<string, object?> { ["provider_metadata"] = metadata });

    public static string? String(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
}
