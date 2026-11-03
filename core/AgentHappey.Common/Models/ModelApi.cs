using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;

namespace AgentHappey.Common.Models;

/// <summary>Discovery only. Never include instructions, provider settings, headers, or archive contents.</summary>
public sealed class AgentModelMetadata
{
    public ModelReference? Model { get; init; }
    public IReadOnlyList<ModelReference>? Models { get; init; }
    public ModelReference? Transcription { get; init; }
    public SpeechReference? Speech { get; init; }
    public IEnumerable<Icon>? Icons { get; init; }
    public string? ArgumentHint { get; init; }
    public bool HasMcpServers { get; init; }
    public bool CanSaveAsPlugin { get; init; }

    public sealed record ModelReference(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("type"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Type);
    public sealed record SpeechReference(ModelReference? Model);

    public static AgentModelMetadata FromAgent(Agent agent)
    {
        static ModelReference? Reference(AIModel? model) => string.IsNullOrWhiteSpace(model?.Id) ? null : new(model.Id, model.Type);
        var hasMcpServers = agent.McpServers?.Any(server => !string.IsNullOrWhiteSpace(server.Key) && server.Value.Disabled != true) == true;
        return new()
        {
            Model = Reference(agent.Model),
            Models = agent.Models?.Where(model => !string.IsNullOrWhiteSpace(model.Id))
                .DistinctBy(model => model.Id, StringComparer.Ordinal).Select(model => Reference(model)!).ToArray(),
            Transcription = Reference(agent.Transcription),
            Speech = agent.Speech is null ? null : new(Reference(agent.Speech.Model)),
            Icons = agent.Icons,
            ArgumentHint = agent.ArgumentHint,
            HasMcpServers = hasMcpServers,
            CanSaveAsPlugin = hasMcpServers || agent.Skills?.Any() == true
        };
    }
}

/// <summary>HTTP projections leave the internal catalog and its cached definitions untouched.</summary>
public static class ModelApi
{
    public static ModelListResponse Summary(ModelListResponse response) => new()
    {
        Object = response.Object,
        Data = response.Data.Select(model => new Model
        {
            Id = model.Id,
            Object = model.Object,
            Created = model.Created,
            OwnedBy = model.OwnedBy,
            Name = model.Name ?? model.Agent?.Name,
            Description = model.Description ?? model.Agent?.Description,
            Tags = model.Tags ?? model.Agent?.Tags,
            Type = model.Type,
            AgentMetadata = model.Agent is { } agent ? AgentModelMetadata.FromAgent(agent) : model.AgentMetadata
        }).ToArray()
    };

    public static async Task<Model?> RetrieveAsync(this IModelCatalog catalog, string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        // Use the model source, not ResolveAgentAsync: runtime resolution can rewrite MCP URLs.
        var models = await catalog.ListAsync(ct);
        ct.ThrowIfCancellationRequested();
        return models.Data.FirstOrDefault(model => string.Equals(model.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public static string DecodeId(string routeValue, string? rawTarget)
    {
        // ASP.NET intentionally leaves encoded slashes in route values. Decode the raw path
        // exactly once so both slash IDs and literal strings such as "%2F" round-trip.
        var path = rawTarget?.Split('?', 2)[0];
        const string prefix = "/v1/models/";
        var start = path?.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) ?? -1;
        return start < 0 ? routeValue : Uri.UnescapeDataString(path![(start + prefix.Length)..]);
    }
}
