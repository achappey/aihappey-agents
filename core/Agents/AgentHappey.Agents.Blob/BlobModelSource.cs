using System.Text.Json;
using System.Runtime.CompilerServices;
using AgentHappey.Common.Models;
using AgentHappey.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace AgentHappey.Agents.Blob;

public sealed class BlobModelSource(BlobAgentsConfig? config) : IModelSource
{
    public async Task<IReadOnlyList<Model>> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        var models = new List<Model>();

        await foreach (var entry in GetAgentEntriesAsync(cancellationToken))
        {
            models.Add(new Model
            {
                Id = entry.Agent.Name,
                OwnedBy = "agenthappey",
                Created = entry.Created
            });
        }

        return models
            .OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .ToList()
            .AsReadOnly();
    }

    public async Task<IReadOnlyList<Agent>> GetAgentsAsync(CancellationToken cancellationToken = default)
    {
        var agents = new List<Agent>();

        await foreach (var entry in GetAgentEntriesAsync(cancellationToken))
            agents.Add(entry.Agent);

        return agents
            .OrderBy(agent => agent.Name, StringComparer.OrdinalIgnoreCase)
            .ToList()
            .AsReadOnly();
    }

    private async IAsyncEnumerable<(Agent Agent, long? Created)> GetAgentEntriesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(config?.ConnectionString) || string.IsNullOrWhiteSpace(config.ContainerName))
            yield break;

        var containerClient = new BlobContainerClient(config.ConnectionString, config.ContainerName);

        if (!await containerClient.ExistsAsync(cancellationToken))
            yield break;

        var prefix = NormalizePrefix(config.Prefix);

        await foreach (var blobItem in containerClient.GetBlobsAsync(
            traits: BlobTraits.None,
            states: BlobStates.None,
            prefix: prefix,
            cancellationToken: cancellationToken))
        {
            if (!blobItem.Name.EndsWith("Agent.json", StringComparison.OrdinalIgnoreCase))
                continue;

            var blobClient = containerClient.GetBlobClient(blobItem.Name);
            var download = await blobClient.DownloadContentAsync(cancellationToken);
            var json = download.Value.Content.ToString();

            var agent = JsonSerializer.Deserialize<Agent>(json, JsonSerializerOptions.Web);
            if (agent == null || string.IsNullOrWhiteSpace(agent.Name))
                continue;

            yield return (agent, blobItem.Properties.LastModified?.ToUnixTimeSeconds());
        }
    }

    private static string? NormalizePrefix(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return null;

        return prefix.Replace('\\', '/').Trim('/');
    }
}
