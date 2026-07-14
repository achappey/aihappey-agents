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

    public async Task SaveAsync(ResponseResult response, CancellationToken cancellationToken = default, string? userId = null)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (string.IsNullOrWhiteSpace(response.Id))
            throw new InvalidOperationException("Cannot store a response without an id.");

        await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

        var json = JsonSerializer.Serialize(response, ResponseJson.Default);
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        await container.GetBlobClient(GetBlobName(response.Id, userId)).UploadAsync(stream, overwrite: true, cancellationToken);
    }

    public async Task<ResponseResult?> GetAsync(string responseId, CancellationToken cancellationToken = default, string? userId = null)
    {
        if (string.IsNullOrWhiteSpace(responseId))
            return null;

        try
        {
            var download = await container.GetBlobClient(GetBlobName(responseId, userId)).DownloadContentAsync(cancellationToken);
            return JsonSerializer.Deserialize<ResponseResult>(download.Value.Content.ToString(), ResponseJson.Default);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<ResponseResult>> ListAsync(CancellationToken cancellationToken = default, string? userId = null)
    {
        await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

        var responses = new List<ResponseResult>();
        await foreach (var blob in container.GetBlobsAsync(prefix: GetBlobPrefix(userId),
            traits: Azure.Storage.Blobs.Models.BlobTraits.All,
            states: Azure.Storage.Blobs.Models.BlobStates.All,
            cancellationToken: cancellationToken))
        {
            try
            {
                var download = await container.GetBlobClient(blob.Name).DownloadContentAsync(cancellationToken);
                var response = JsonSerializer.Deserialize<ResponseResult>(download.Value.Content.ToString(), ResponseJson.Default);
                if (response is not null)
                    responses.Add(response);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // The response may have been deleted between listing and download.
            }
        }

        return responses
            .OrderByDescending(response => response.CreatedAt)
            .ThenByDescending(response => response.Id, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<bool> DeleteAsync(string responseId, CancellationToken cancellationToken = default, string? userId = null)
    {
        if (string.IsNullOrWhiteSpace(responseId))
            return false;

        var response = await container.GetBlobClient(GetBlobName(responseId, userId)).DeleteIfExistsAsync(cancellationToken: cancellationToken);
        return response.Value;
    }

    public static string GetBlobName(string responseId, string? userId = null)
    {
        var responseSegment = Uri.EscapeDataString(responseId.Trim());
        if (string.IsNullOrWhiteSpace(userId))
            return $"responses/{responseSegment}.json";

        return $"responses/{Uri.EscapeDataString(userId.Trim())}/{responseSegment}.json";
    }

    public static string GetBlobPrefix(string? userId = null)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return "responses/";

        return $"responses/{Uri.EscapeDataString(userId.Trim())}/";
    }
}
