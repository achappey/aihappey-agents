using System.Text;
using System.Text.Json;
using AgentHappey.Core;
using AIHappey.Responses;
using Azure;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Options;

namespace AgentHappey.AsyncResponses;

public sealed class AzureAsyncResponseStore : IAsyncResponseStore
{
    private readonly BlobContainerClient container;

    public AzureAsyncResponseStore(IOptions<AsyncAgentsConfig> options)
    {
        var config = options.Value;
        if (string.IsNullOrWhiteSpace(config.ConnectionString))
            throw new InvalidOperationException("AsyncAgents:ConnectionString is required.");

        container = new BlobContainerClient(
            config.ConnectionString,
            string.IsNullOrWhiteSpace(config.ResponseContainerName)
                ? "agents-async-responses"
                : config.ResponseContainerName.Trim().ToLowerInvariant());
    }

    public async Task SaveAsync(ResponseResult response, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (string.IsNullOrWhiteSpace(response.Id))
            throw new InvalidOperationException("Cannot store a response without an id.");

        await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

        var json = JsonSerializer.Serialize(response, ResponseJson.Default);
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        await container.GetBlobClient(GetBlobName(response.Id)).UploadAsync(stream, overwrite: true, cancellationToken);
    }

    public async Task<ResponseResult?> GetAsync(string responseId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(responseId))
            return null;

        try
        {
            var download = await container.GetBlobClient(GetBlobName(responseId)).DownloadContentAsync(cancellationToken);
            return JsonSerializer.Deserialize<ResponseResult>(download.Value.Content.ToString(), ResponseJson.Default);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    private static string GetBlobName(string responseId) => $"responses/{responseId}.json";
}
