using System.Text.Json;
using AgentHappey.Common.Models;
using AgentHappey.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace AgentHappey.Agents.Blob;

public sealed class BlobModelSource(BlobAgentsConfig? config) : IModelSource
{
    public async Task<IReadOnlyList<Model>> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(config?.ConnectionString) || string.IsNullOrWhiteSpace(config.ContainerName))
            return [];

        var containerClient = new BlobContainerClient(config.ConnectionString, config.ContainerName);

        if (!await containerClient.ExistsAsync(cancellationToken))
            return [];

        var models = new List<Model>();
        var prefix = NormalizePrefix(config.Prefix);

        await foreach (var blobItem in containerClient.GetBlobsAsync(
            traits: BlobTraits.None,
             states: BlobStates.None,
            prefix: prefix, cancellationToken: cancellationToken))
        {
            if (!blobItem.Name.EndsWith("Agent.json", StringComparison.OrdinalIgnoreCase))
                continue;

            var blobClient = containerClient.GetBlobClient(blobItem.Name);
            var download = await blobClient.DownloadContentAsync(cancellationToken);
            var json = download.Value.Content.ToString();

            var agent = JsonSerializer.Deserialize<Agent>(json, JsonSerializerOptions.Web);
            if (agent == null || string.IsNullOrWhiteSpace(agent.Name))
                continue;

            models.Add(new Model
            {
                Id = agent.Name,
                OwnedBy = "agenthappey",
                Created = blobItem.Properties.LastModified?.ToUnixTimeSeconds()
            });
        }

        return models
            .OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .ToList()
            .AsReadOnly();
    }

    private static string? NormalizePrefix(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return null;

        return prefix.Replace('\\', '/').Trim('/');
    }
}
