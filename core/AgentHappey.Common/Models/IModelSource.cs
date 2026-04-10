namespace AgentHappey.Common.Models;

public interface IModelSource
{
    Task<IReadOnlyList<Model>> GetModelsAsync(CancellationToken cancellationToken = default);
}
