namespace AgentHappey.Common.Models;

public interface IModelCatalog
{
    Task<ModelListResponse> ListAsync(CancellationToken cancellationToken = default);

    Task<Agent?> ResolveAgentAsync(string modelId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Agent>> ResolveAgentsAsync(IEnumerable<string> modelIds, CancellationToken cancellationToken = default);
}
