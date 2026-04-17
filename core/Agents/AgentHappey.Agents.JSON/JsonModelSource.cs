using AgentHappey.Common.Models;

namespace AgentHappey.Agents.JSON;

public sealed class JsonModelSource(string basePath, string? mcpBaseUrl) : IModelSource
{
    public Task<IReadOnlyList<Model>> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Model> models = basePath
            .GetModels()
            .ToList()
            .AsReadOnly();

        return Task.FromResult(models);
    }

    public Task<IReadOnlyList<Agent>> GetAgentsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Agent> agents = basePath
            .GetAgents(mcpBaseUrl)
            .ToList()
            .AsReadOnly();

        return Task.FromResult(agents);
    }
}
