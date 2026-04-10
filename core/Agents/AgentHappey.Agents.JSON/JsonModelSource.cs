using AgentHappey.Common.Models;

namespace AgentHappey.Agents.JSON;

public sealed class JsonModelSource(string basePath) : IModelSource
{
    public Task<IReadOnlyList<Model>> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Model> models = basePath
            .GetModels()
            .ToList()
            .AsReadOnly();

        return Task.FromResult(models);
    }
}
