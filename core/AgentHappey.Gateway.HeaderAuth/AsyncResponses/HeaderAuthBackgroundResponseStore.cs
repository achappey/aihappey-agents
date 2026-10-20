using System.Globalization;
using System.Text.Json;
using AgentHappey.AsyncResponses;
using AgentHappey.Core;
using AIHappey.Responses;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;

namespace AgentHappey.HeaderAuth.AsyncResponses;

/// <summary>Only response JSON is stored. No request, context, queue message or credential sidecar.</summary>
public sealed class HeaderAuthBackgroundResponseStore : IHeaderAuthBackgroundResponseStore
{
    private const string DeadlineKey = "headerauth_execution_deadline";
    private readonly BlobContainerClient container;

    public HeaderAuthBackgroundResponseStore(IOptions<AsyncAgentsConfig> options)
        : this(new BlobContainerClient(options.Value.ConnectionString,
            string.IsNullOrWhiteSpace(options.Value.ResponseContainerName)
                ? "agents-async-responses" : options.Value.ResponseContainerName.Trim().ToLowerInvariant())) { }

    internal HeaderAuthBackgroundResponseStore(BlobContainerClient container) => this.container = container;

    public async Task CreateAsync(ResponseResult response, DateTimeOffset deadline, CancellationToken cancellationToken, string? userId)
    {
        await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        await WriteAsync(response, deadline, new BlobRequestConditions { IfNoneMatch = ETag.All }, cancellationToken, userId);
    }

    public async Task<HeaderAuthStoredResponse?> ReadAsync(string id, CancellationToken cancellationToken, string? userId)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return await ReadBlobAsync(AzureAsyncResponseStore.GetBlobName(id, userId), cancellationToken);
    }

    private async Task<HeaderAuthStoredResponse?> ReadBlobAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            var download = (await container.GetBlobClient(name).DownloadContentAsync(cancellationToken)).Value;
            var response = JsonSerializer.Deserialize<ResponseResult>(download.Content.ToString(), ResponseJson.Default);
            if (response is null) return null;
            DateTimeOffset? deadline = download.Details.Metadata.TryGetValue(DeadlineKey, out var value)
                ? DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : null;
            var path = name["responses/".Length..].Split('/');
            var userId = path.Length == 2 ? Uri.UnescapeDataString(path[0]) : null;
            return new(response, download.Details.ETag.ToString(), deadline, userId);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { return null; }
    }

    public async Task<bool> ReplaceAsync(HeaderAuthStoredResponse previous, ResponseResult response,
        CancellationToken cancellationToken, string? userId)
    {
        try
        {
            await WriteAsync(response, previous.Deadline, new BlobRequestConditions { IfMatch = new ETag(previous.Version) },
                cancellationToken, userId);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status is 404 or 412) { return false; }
    }

    private async Task WriteAsync(ResponseResult response, DateTimeOffset? deadline, BlobRequestConditions conditions,
        CancellationToken cancellationToken, string? userId)
    {
        // Defence in depth: callers also redact request-known credential echoes before this boundary.
        var safe = HeaderAuthResponsePrivacy.ForResponse(response).Sanitize(response);
        var metadata = new Dictionary<string, string>();
        if (deadline is not null) metadata[DeadlineKey] = deadline.Value.ToString("O", CultureInfo.InvariantCulture);
        using var content = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(safe, ResponseJson.Default));
        await container.GetBlobClient(AzureAsyncResponseStore.GetBlobName(response.Id, userId)).UploadAsync(content,
            new BlobUploadOptions { Conditions = conditions, Metadata = metadata,
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" } }, cancellationToken);
    }

    public async Task<IReadOnlyList<HeaderAuthStoredResponse>> ListAsync(CancellationToken cancellationToken, string? userId)
    {
        await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        var results = new List<HeaderAuthStoredResponse>();
        // Only current live responses, never requests/, versions or soft-deleted blobs.
        await foreach (var blob in container.GetBlobsAsync(BlobTraits.None, BlobStates.None,
            prefix: AzureAsyncResponseStore.GetBlobPrefix(userId),
            cancellationToken: cancellationToken))
        {
            var response = await ReadBlobAsync(blob.Name, cancellationToken);
            if (response is not null) results.Add(response);
        }
        return results;
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken, string? userId)
        => !string.IsNullOrWhiteSpace(id) && (await container.GetBlobClient(AzureAsyncResponseStore.GetBlobName(id, userId))
            .DeleteIfExistsAsync(cancellationToken: cancellationToken)).Value;
}
