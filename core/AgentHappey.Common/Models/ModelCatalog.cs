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
}
