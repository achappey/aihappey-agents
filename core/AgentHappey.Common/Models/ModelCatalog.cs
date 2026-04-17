namespace AgentHappey.Common.Models;

public sealed class ModelCatalog(IEnumerable<IModelSource> sources) : IModelCatalog
{
    public async Task<ModelListResponse> ListAsync(CancellationToken cancellationToken = default)
    {
        var merged = new Dictionary<string, Model>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in sources)
        {
            var models = await source.GetModelsAsync(cancellationToken);

            foreach (var model in models)
            {
                if (string.IsNullOrWhiteSpace(model.Id))
                    continue;

                merged[model.Id] = model;
            }
        }

        var ordered = merged.Values
            .OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .ToList()
            .AsReadOnly();

        return new ModelListResponse
        {
            Data = ordered
        };
    }

    public async Task<Agent?> ResolveAgentAsync(string modelId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return null;

        var merged = await GetMergedAgentsAsync(cancellationToken);
        return merged.TryGetValue(modelId, out var agent)
            ? agent
            : null;
    }

    public async Task<IReadOnlyList<Agent>> ResolveAgentsAsync(IEnumerable<string> modelIds, CancellationToken cancellationToken = default)
    {
        var ids = modelIds
            .Where(modelId => !string.IsNullOrWhiteSpace(modelId))
            .ToList();

        if (ids.Count == 0)
            return [];

        var merged = await GetMergedAgentsAsync(cancellationToken);
        var resolved = new List<Agent>(ids.Count);

        foreach (var modelId in ids)
        {
            if (!merged.TryGetValue(modelId, out var agent))
                continue;

            resolved.Add(agent);
        }

        return resolved.AsReadOnly();
    }

    private async Task<Dictionary<string, Agent>> GetMergedAgentsAsync(CancellationToken cancellationToken)
    {
        var merged = new Dictionary<string, Agent>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in sources)
        {
            var agents = await source.GetAgentsAsync(cancellationToken);

            foreach (var agent in agents)
            {
                if (string.IsNullOrWhiteSpace(agent.Name))
                    continue;

                merged[agent.Name] = agent;
            }
        }

        return merged;
    }
}
