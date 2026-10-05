using AIHappey.Responses;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentHappey.AsyncResponses;

public sealed class LocalAsyncResponsesWorker(
    LocalAsyncResponsesQueue queue,
    IAsyncResponsesProcessor processor,
    IAsyncResponseStore store,
    ILogger<LocalAsyncResponsesWorker> logger) : BackgroundService
{
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await queue.RecoverAsync(store, cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await ProcessNextAsync(stoppingToken))
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Local response processing failed; unacknowledged work remains on disk.");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }

    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken = default)
    {
        var claimed = await queue.ClaimAsync(cancellationToken);
        if (claimed is null)
            return false;

        var (path, message) = claimed.Value;
        var response = await store.GetAsync(message.ResponseId, cancellationToken, message.Context.UserId);
        if (response?.Status is "completed" or "failed")
        {
            // Recovery after the terminal write but before acknowledgement must not
            // invoke an agent (and its external tools) again.
            queue.Acknowledge(path);
            return true;
        }

        response ??= AsyncResponseLifecycle.CreateQueuedResponse(message.Request);
        response.Id = message.ResponseId;
        response.CreatedAt = message.CreatedAt;
        response.Status = "in_progress";
        response.CompletedAt = null;
        response.Error = null;
        AsyncResponseLifecycle.EnsureBackgroundProperty(response);
        await store.SaveAsync(response, cancellationToken, message.Context.UserId);

        ResponseResult result;
        try
        {
            result = await processor.ProcessAsync(message, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Leave the claimed job on disk for the next host instance.
            throw;
        }
        catch (Exception exception)
        {
            response.Error = new ResponseResultError { Code = "server_error", Message = exception.Message };
            result = response;
            logger.LogWarning("Background agent response {ResponseId} failed.", message.ResponseId);
        }

        await store.SaveAsync(AsyncResponseLifecycle.NormalizeBackgroundResult(message, result),
            cancellationToken, message.Context.UserId);
        queue.Acknowledge(path);
        return true;
    }
}
