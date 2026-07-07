using System.Text.Json;
using AgentHappey.Core;
using AIHappey.Responses;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentHappey.AsyncResponses;

public sealed class AsyncResponsesWorker(
    IOptions<AsyncAgentsConfig> options,
    IAsyncResponsesProcessor processor,
    IAsyncResponseStore store,
    ILogger<AsyncResponsesWorker> logger) : BackgroundService
{
    private static readonly TimeSpan EmptyQueueDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan VisibilityTimeout = TimeSpan.FromMinutes(10);
    private const int MaxAttempts = 5;

    private readonly AsyncAgentsConfig config = options.Value;
    private QueueClient? queue;

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

    private async Task ProcessMessageAsync(QueueMessage queueMessage, CancellationToken cancellationToken)
    {
        AsyncResponsesQueueMessage? message = null;

        try
        {
            message = JsonSerializer.Deserialize<AsyncResponsesQueueMessage>(queueMessage.MessageText, ResponseJson.Default)
                ?? throw new InvalidOperationException("Async response queue message was empty.");

            await MarkInProgressAsync(message, cancellationToken);
            var result = await processor.ProcessAsync(message, cancellationToken);
            await store.SaveAsync(NormalizeBackgroundResult(message, result), cancellationToken);
            await DeleteMessageAsync(queueMessage, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Background agent response failed for queue message {MessageId}.", queueMessage.MessageId);

            if (message is not null)
                await TryPersistFailureAsync(message, ex, cancellationToken);

            if (queueMessage.DequeueCount >= MaxAttempts || message is not null)
                await DeleteMessageAsync(queueMessage, cancellationToken);
        }
    }

    private async Task MarkInProgressAsync(AsyncResponsesQueueMessage message, CancellationToken cancellationToken)
    {
        var response = await store.GetAsync(message.ResponseId, cancellationToken)
            ?? AzureAsyncResponsesService.CreateQueuedResponse(message.Request);

        response.Id = message.ResponseId;
        response.CreatedAt = message.CreatedAt;
        response.Status = "in_progress";
        response.CompletedAt = null;
        response.Error = null;
        EnsureBackgroundProperty(response);
        await store.SaveAsync(response, cancellationToken);
    }

    private async Task TryPersistFailureAsync(
        AsyncResponsesQueueMessage message,
        Exception exception,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await store.GetAsync(message.ResponseId, cancellationToken)
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
            EnsureBackgroundProperty(response);

            await store.SaveAsync(response, cancellationToken);
        }
        catch (Exception persistException)
        {
            logger.LogError(persistException, "Failed to persist failed background response {ResponseId}.", message.ResponseId);
        }
    }

    private static ResponseResult NormalizeBackgroundResult(AsyncResponsesQueueMessage message, ResponseResult result)
    {
        result.Id = message.ResponseId;
        result.CreatedAt = message.CreatedAt;
        result.Status = result.Error is null ? "completed" : "failed";
        result.CompletedAt ??= DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        result.Model = string.IsNullOrWhiteSpace(result.Model) ? (message.Request.Model ?? "agent") : result.Model;
        EnsureBackgroundProperty(result);
        return result;
    }

    private static void EnsureBackgroundProperty(ResponseResult response)
    {
        response.AdditionalProperties ??= new Dictionary<string, JsonElement>();
        response.AdditionalProperties["background"] = JsonSerializer.SerializeToElement(true, ResponseJson.Default);
    }

    private Task DeleteMessageAsync(QueueMessage message, CancellationToken cancellationToken)
        => queue?.DeleteMessageAsync(message.MessageId, message.PopReceipt, cancellationToken) ?? Task.CompletedTask;
}
