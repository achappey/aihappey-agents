using AgentHappey.Core;
using AIHappey.Responses;
using Azure;
using Azure.Storage.Queues;
using Microsoft.Extensions.Options;

namespace AgentHappey.AsyncResponses;

public sealed class AzureAsyncResponsesService : IAsyncResponsesService
{
    private readonly QueueClient queue;
    private readonly IAsyncResponseStore store;
    private readonly AzureAsyncResponsePayloadStore payloads;

    public AzureAsyncResponsesService(IOptions<AsyncAgentsConfig> options, IAsyncResponseStore store)
    {
        var config = options.Value;
        if (string.IsNullOrWhiteSpace(config.ConnectionString))
            throw new InvalidOperationException("AsyncAgents:ConnectionString is required.");

        queue = new QueueClient(
            config.ConnectionString,
            config.QueueName ?? "agents-async",
            new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 });
        this.store = store;
        payloads = new AzureAsyncResponsePayloadStore(AzureAsyncResponseStore.CreateContainerClient(config));
    }

    internal AzureAsyncResponsesService(QueueClient queue, IAsyncResponseStore store, AzureAsyncResponsePayloadStore payloads)
    {
        this.queue = queue;
        this.store = store;
        this.payloads = payloads;
    }

    public bool IsEnabled => true;

    public async Task<ResponseResult> EnqueueAsync(
        ResponseRequest request,
        AsyncResponsesRequestContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var response = CreateQueuedResponse(request);
        var message = new AsyncResponsesQueueMessage
        {
            ResponseId = response.Id,
            CreatedAt = response.CreatedAt,
            Request = CloneForQueue(request),
            Context = context
        };

        var reference = AzureAsyncResponsePayloadStore.CreateReference();
        await queue.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        await payloads.SaveAsync(reference, message, cancellationToken);
        try
        {
            await store.SaveAsync(response, cancellationToken, context.UserId);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            await payloads.DeleteAsync(reference, CancellationToken.None);
            await store.DeleteAsync(response.Id, CancellationToken.None, context.UserId);
            throw;
        }
        // A send can succeed remotely even if the client observes cancellation or a
        // transport failure. Keep the payload in that case so delivery remains retryable.
        try
        {
            await queue.SendMessageAsync(reference, cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.Status is >= 400 and < 500 && ex.Status != 408)
        {
            await payloads.DeleteAsync(reference, CancellationToken.None);
            await store.DeleteAsync(response.Id, CancellationToken.None, context.UserId);
            throw;
        }

        return response;
    }

    public Task<ResponseResult?> GetAsync(string responseId, CancellationToken cancellationToken = default, string? userId = null)
        => store.GetAsync(responseId, cancellationToken, userId);

    public Task<IReadOnlyList<ResponseResult>> ListAsync(CancellationToken cancellationToken = default, string? userId = null)
        => store.ListAsync(cancellationToken, userId);

    public Task<bool> DeleteAsync(string responseId, CancellationToken cancellationToken = default, string? userId = null)
        => store.DeleteAsync(responseId, cancellationToken, userId);

    public static ResponseResult CreateQueuedResponse(ResponseRequest request)
        => AsyncResponseLifecycle.CreateQueuedResponse(request);

    public static ResponseRequest CloneForQueue(ResponseRequest request)
        => AsyncResponseLifecycle.CloneForQueue(request);
}
