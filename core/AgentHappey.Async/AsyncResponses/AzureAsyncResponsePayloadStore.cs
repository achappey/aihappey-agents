using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Responses;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace AgentHappey.AsyncResponses;

internal sealed class AzureAsyncResponsePayloadStore(BlobContainerClient container)
{
    internal sealed record Reference([property: JsonPropertyName("payload_id")] string PayloadId);

    internal static string CreateReference()
        => JsonSerializer.Serialize(new Reference(Guid.NewGuid().ToString("N")), ResponseJson.Default);

    private static string? GetBlobName(string queueMessage)
    {
        using var document = JsonDocument.Parse(queueMessage);
        if (!document.RootElement.TryGetProperty("payload_id", out var id))
            return null; // Messages queued before payload externalization contain the request inline.

        if (id.ValueKind != JsonValueKind.String || !Guid.TryParseExact(id.GetString(), "N", out var payloadId))
            throw new InvalidOperationException("Invalid background response payload reference.");

        return $"requests/{payloadId:N}.json";
    }

    internal async Task SaveAsync(string reference, AsyncResponsesQueueMessage message, CancellationToken cancellationToken)
    {
        await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        await using var stream = new MemoryStream();
        await JsonSerializer.SerializeAsync(stream, message, ResponseJson.Default, cancellationToken);
        stream.Position = 0;
        await container.GetBlobClient(GetBlobName(reference)!).UploadAsync(stream, overwrite: true, cancellationToken);
    }

    internal async Task<AsyncResponsesQueueMessage> ReadAsync(string queueMessage, CancellationToken cancellationToken)
    {
        var blobName = GetBlobName(queueMessage);
        if (blobName is null)
            return JsonSerializer.Deserialize<AsyncResponsesQueueMessage>(queueMessage, ResponseJson.Default)
                ?? throw new InvalidOperationException("Async response queue message was empty.");

        await using var stream = await container.GetBlobClient(blobName).OpenReadAsync(new BlobOpenReadOptions(false), cancellationToken);
        return await JsonSerializer.DeserializeAsync<AsyncResponsesQueueMessage>(stream, ResponseJson.Default, cancellationToken)
            ?? throw new InvalidOperationException("Async response payload was empty.");
    }

    internal async Task DeleteAsync(string queueMessage, CancellationToken cancellationToken)
    {
        string? blobName;
        try
        {
            blobName = GetBlobName(queueMessage);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return; // Malformed legacy/poison messages cannot identify a stored payload.
        }
        if (blobName is not null)
            await container.GetBlobClient(blobName).DeleteIfExistsAsync(cancellationToken: cancellationToken);
    }
}
