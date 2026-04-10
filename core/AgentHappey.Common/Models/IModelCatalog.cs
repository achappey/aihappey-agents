namespace AgentHappey.Common.Models;

public interface IModelCatalog
{
    Task<ModelListResponse> ListAsync(CancellationToken cancellationToken = default);
}
