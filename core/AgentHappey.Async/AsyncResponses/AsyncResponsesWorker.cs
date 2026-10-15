using AgentHappey.Core;
using AIHappey.Responses;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentHappey.AsyncResponses;

public sealed class AsyncResponsesWorker : BackgroundService
{
    private static readonly TimeSpan EmptyQueueDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan VisibilityTimeout = TimeSpan.FromMinutes(10);
    private const int MaxAttempts = 5;

    private readonly AsyncAgentsConfig config;
    private readonly IAsyncResponsesProcessor processor;
    private readonly IAsyncResponseStore store;
    private readonly ILogger<AsyncResponsesWorker> logger;
    private readonly AzureAsyncResponsePayloadStore? payloads;
    private QueueClient? queue;

    public AsyncResponsesWorker(IOptions<AsyncAgentsConfig> options, IAsyncResponsesProcessor processor,
        IAsyncResponseStore store, ILogger<AsyncResponsesWorker> logger)
    {
        config = options.Value;
        this.processor = processor;
        this.store = store;
        this.logger = logger;
        if (!string.IsNullOrWhiteSpace(config.ConnectionString))
            payloads = new AzureAsyncResponsePayloadStore(AzureAsyncResponseStore.CreateContainerClient(config));
    }

    internal AsyncResponsesWorker(QueueClient queue, AzureAsyncResponsePayloadStore payloads,
        IAsyncResponsesProcessor processor, IAsyncResponseStore store, ILogger<AsyncResponsesWorker> logger)
    {
        config = new AsyncAgentsConfig();
        this.queue = queue;
        this.payloads = payloads;
        this.processor = processor;
        this.store = store;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(config.ConnectionString))
            return;

        queue = new QueueClient(
            config.ConnectionString,
            config.QueueName ?? "agents-async",
            new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 });
        await queue.CreateIfNotExistsAsync(cancellationToken: stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var messages = await queue.ReceiveMessagesAsync(1, VisibilityTimeout, stoppingToken);
                if (messages.Value.Length == 0)
                {
                    await Task.Delay(EmptyQueueDelay, stoppingToken);
                    continue;
                }

                foreach (var message in messages.Value)
                    await ProcessMessageAsync(message, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Async agent responses worker loop failed.");
                await Task.Delay(ErrorDelay, stoppingToken);
            }
        }
    }

    internal async Task ProcessMessageAsync(QueueMessage queueMessage, CancellationToken cancellationToken)
    {
        AsyncResponsesQueueMessage? message = null;

        try
        {
            message = await payloads!.ReadAsync(queueMessage.MessageText, cancellationToken);

            var existing = await store.GetAsync(message.ResponseId, cancellationToken, message.Context.UserId);
            if (existing?.Status is not ("completed" or "failed"))
            {
                await MarkInProgressAsync(message, cancellationToken);
                var result = await processor.ProcessAsync(message, cancellationToken);
                await store.SaveAsync(AsyncResponseLifecycle.NormalizeBackgroundResult(message, result), cancellationToken, message.Context.UserId);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError("Background agent response failed for queue message {MessageId}.", queueMessage.MessageId);

            if (message is not null && !await TryPersistFailureAsync(message, ex, cancellationToken))
                return;

            if (queueMessage.DequeueCount < MaxAttempts && message is null)
                return;
        }

        await DeleteMessageAsync(queueMessage, cancellationToken);
    }

    private async Task MarkInProgressAsync(AsyncResponsesQueueMessage message, CancellationToken cancellationToken)
    {
        var response = await store.GetAsync(message.ResponseId, cancellationToken, message.Context.UserId)
            ?? AzureAsyncResponsesService.CreateQueuedResponse(message.Request);

        response.Id = message.ResponseId;
        response.CreatedAt = message.CreatedAt;
        response.Status = "in_progress";
        response.CompletedAt = null;
        response.Error = null;
        AsyncResponseLifecycle.EnsureBackgroundProperty(response);
        await store.SaveAsync(response, cancellationToken, message.Context.UserId);
    }

    private async Task<bool> TryPersistFailureAsync(
        AsyncResponsesQueueMessage message,
        Exception exception,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await store.GetAsync(message.ResponseId, cancellationToken, message.Context.UserId)
                ?? AzureAsyncResponsesService.CreateQueuedResponse(message.Request);

            response.Id = message.ResponseId;
            response.CreatedAt = message.CreatedAt;
            response.Status = "failed";
            response.CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            response.Error = new ResponseResultError
            {
                Code = "server_error",
                Message = exception.Message
            };
            AsyncResponseLifecycle.EnsureBackgroundProperty(response);

            await store.SaveAsync(response, cancellationToken, message.Context.UserId);
            return true;
        }
        catch (Exception)
        {
            logger.LogError("Failed to persist failed background response {ResponseId}.", message.ResponseId);
            return false;
        }
    }

    private async Task DeleteMessageAsync(QueueMessage message, CancellationToken cancellationToken)
    {
        // A terminal response is durable before cleanup. Delete the payload first
        // so a failed cleanup can be retried while the queue entry still exists.
        // Redelivery after payload deletion follows the missing-payload retry path.
        await payloads!.DeleteAsync(message.MessageText, cancellationToken);
        if (queue is not null)
            await queue.DeleteMessageAsync(message.MessageId, message.PopReceipt, cancellationToken);
    }
}
