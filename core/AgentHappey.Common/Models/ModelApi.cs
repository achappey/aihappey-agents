namespace AgentHappey.Common.Models;

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
            Tags = DiscoveryTags(model),
            Type = model.Type
        }).ToArray()
    };

    private static string[]? DiscoveryTags(Model model)
    {
        var tags = model.Tags ?? model.Agent?.Tags;
        // A configured audio input advertises a realtime candidate using the same tag
        // as the AI gateway. Clients validate the actual model/provider on selection.
        if (string.IsNullOrWhiteSpace(model.Agent?.Transcription?.Id)) return tags;
        return (tags ?? []).Append("real-time").Distinct(StringComparer.Ordinal).ToArray();
    }

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
