using System.Text.Json;
using AIHappey.Vercel.Models;

namespace AgentHappey.Core.ChatClient;

public partial class AgentChatClient
{
    private readonly object decisionCostLock = new();
    private decimal? pendingDecisionCost;

    private void CaptureDecisionCost(DecisionResponse? response)
    {
        if (response?.ProviderMetadata?.TryGetValue("gateway", out var gateway) == true
            && gateway.ValueKind == JsonValueKind.Object
            && gateway.TryGetProperty("cost", out var value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var cost))
            AddDecisionCost(cost);
    }

    internal void AddDecisionCost(decimal? cost)
    {
        if (cost is null) return;
        lock (decisionCostLock)
            pendingDecisionCost = (pendingDecisionCost ?? 0) + cost.Value;
    }

    // A cost is moved, never copied: temporary routers and subsequent inference/tool
    // turns must not report the same decision more than once.
    internal decimal? TakeDecisionCost()
    {
        lock (decisionCostLock)
        {
            var cost = pendingDecisionCost;
            pendingDecisionCost = null;
            return cost;
        }
    }

    private Dictionary<string, object?>? IncludeDecisionCost(Dictionary<string, object?>? metadata)
    {
        if (TakeDecisionCost() is not { } decisionCost) return metadata;

        var result = metadata?.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal) ?? [];
        var scopedGateways = new List<Dictionary<string, object?>>();
        Dictionary<string, object?>? preferredGateway = null;
        foreach (var key in new[] { "providerMetadata", "provider_metadata" })
        {
            if (!result.TryGetValue(key, out var value) || !TryGetMetadataObject(value, out var providers)) continue;
            // Match native Responses precedence: the execution provider, then other providers.
            foreach (var provider in providers.OrderBy(item => item.Key == GetProviderKey() ? 0 : 1).ToList())
            {
                if (!TryGetMetadataObject(provider.Value, out var scoped)
                    || !scoped.TryGetValue("gateway", out var gatewayValue)
                    || !TryGetMetadataObject(gatewayValue, out var gateway)
                    || !TryGetMetadataCost(gateway, out _)) continue;

                preferredGateway ??= gateway;
                scopedGateways.Add(gateway);
                scoped["gateway"] = gateway;
                providers[provider.Key] = scoped;
            }
            result[key] = providers;
        }

        // UI output consumes direct gateway metadata; native Responses may consume a
        // provider-scoped equivalent. Update each representation, not their sum, so
        // existing mapper precedence continues to avoid counting aliases twice.
        var directGateway = result.TryGetValue("gateway", out var directValue)
            && TryGetMetadataObject(directValue, out var direct)
                ? direct
                : preferredGateway?.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal) ?? [];
        foreach (var gateway in scopedGateways)
        {
            TryGetMetadataCost(gateway, out var executionCost);
            gateway["cost"] = executionCost + decisionCost;
        }
        TryGetMetadataCost(directGateway, out var directCost);
        directGateway["cost"] = directCost + decisionCost;
        result["gateway"] = directGateway;
        return result;
    }

    private static bool TryGetMetadataObject(object? value, out Dictionary<string, object?> result)
    {
        result = [];
        if (value is null) return false;
        var json = value is JsonElement element ? element : JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);
        if (json.ValueKind != JsonValueKind.Object) return false;
        result = ToMetadataDictionary(json);
        return true;
    }

    private static bool TryGetMetadataCost(Dictionary<string, object?> gateway, out decimal cost)
    {
        cost = 0;
        if (!gateway.TryGetValue("cost", out var value) || value is null) return false;
        var json = value is JsonElement element ? element : JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);
        return json.ValueKind == JsonValueKind.Number && json.TryGetDecimal(out cost);
    }
}
