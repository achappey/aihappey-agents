using System.Text.Json;
using AIHappey.Responses;

namespace AgentHappey.Core.ChatClient;

public partial class AgentChatClient
{
    private static readonly HashSet<string> RuntimeOwnedProviderOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "model", "input", "instructions", "stream"
    };

    /// <summary>
    /// Saved provider configuration is native request configuration, not application
    /// metadata. Merge through JSON once so known fields become typed properties and
    /// only unknown fields remain extension data. Provider-native token names and
    /// values pass through unchanged; endpoint mapping is the gateway's concern.
    /// </summary>
    private ResponseRequest ApplyProviderOptions(ResponseRequest request, bool sideInference = false)
    {
        if (EffectiveModel.ProviderMetadata is not { Count: > 0 } options)
            return request;

        var fields = JsonSerializer.SerializeToElement(options, JsonSerializerOptions.Web)
            .EnumerateObject()
            .Where(property => !RuntimeOwnedProviderOptions.Contains(property.Name))
            .Where(property => !sideInference || !SideInferenceOwnedProviderOptions.Contains(property.Name))
            .Where(property => !decisionExecution || sideInference
                || !string.Equals(property.Name, "text", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(property.Name, "response_format", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(property.Name, "responseFormat", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);

        var providerTools = !sideInference && fields.TryGetValue("tools", out var tools)
            && tools.ValueKind == JsonValueKind.Array
                ? tools.EnumerateArray().Select(tool => tool.Clone()).ToList()
                : [];

        // Runtime fields (including explicit decision limits/schema/safety) own
        // collisions. Null defaults are omitted so configured tuning can fill them.
        foreach (var property in JsonSerializer.SerializeToElement(request, ResponseJson.Default).EnumerateObject())
        {
            // Responses deserialization is case-insensitive. Remove alternate
            // spellings too so they cannot overwrite an authoritative runtime field.
            foreach (var key in fields.Keys.Where(key => string.Equals(key, property.Name, StringComparison.OrdinalIgnoreCase)).ToList())
                fields.Remove(key);
            fields[property.Name] = property.Value.Clone();
        }

        if (providerTools.Count > 0)
        {
            var combined = request.Tools?.Select(tool => JsonSerializer.SerializeToElement(tool, ResponseJson.Default)).ToList() ?? [];
            combined.AddRange(providerTools);
            fields["tools"] = JsonSerializer.SerializeToElement(combined, ResponseJson.Default);
        }

        var result = JsonSerializer.SerializeToElement(fields, ResponseJson.Default)
            .Deserialize<ResponseRequest>(ResponseJson.Default)!;
        result.Headers = request.Headers;
        result.ExecutionMetadata = request.ExecutionMetadata;
        return result;
    }
}
