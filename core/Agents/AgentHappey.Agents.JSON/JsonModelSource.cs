using AgentHappey.Common.Models;

namespace AgentHappey.Agents.JSON;

public sealed class JsonModelSource(string basePath, string? mcpBaseUrl) : IModelSource
{
    public Task<IReadOnlyList<Model>> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<Model> models = basePath
            .GetModels(cancellationToken)
            .ToList()
            .AsReadOnly();

        return Task.FromResult(models);
    }

    public Task<IReadOnlyList<Agent>> GetAgentsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<Agent> agents = basePath
            .GetAgents(mcpBaseUrl, cancellationToken)
            .ToList()
            .AsReadOnly();

        return Task.FromResult(agents);
    }
}
