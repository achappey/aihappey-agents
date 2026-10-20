using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using AgentHappey.AsyncResponses;
using AIHappey.Responses;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentHappey.HeaderAuth.AsyncResponses;

/// <summary>
/// Durable responses, volatile execution. Only the accepting process can execute a request.
/// No execution retries: tool side effects cannot in general be replayed safely.
/// </summary>
public sealed class HeaderAuthBackgroundResponsesService : BackgroundService, IAsyncResponsesService
{
    private readonly IHeaderAuthBackgroundResponseStore store;
    private readonly IAsyncResponsesProcessor processor;
    private readonly HeaderAuthBackgroundOptions options;
    private readonly TimeProvider clock;
    private readonly ILogger<HeaderAuthBackgroundResponsesService> logger;
    private readonly ConcurrentDictionary<string, Execution> executions = new();
    // The channel holds IDs only. Deletion/expiry can release queued credentials immediately.
    private readonly Channel<string> queue;
    private readonly SemaphoreSlim capacity;
    private bool accepting = true;

    public HeaderAuthBackgroundResponsesService(IHeaderAuthBackgroundResponseStore store, IAsyncResponsesProcessor processor,
        IOptions<HeaderAuthBackgroundOptions> options, TimeProvider clock, ILogger<HeaderAuthBackgroundResponsesService> logger)
    {
        this.store = store;
        this.processor = processor;
        this.options = options.Value;
        this.clock = clock;
        this.logger = logger;
        capacity = new(this.options.Capacity, this.options.Capacity);
        queue = Channel.CreateBounded<string>(new BoundedChannelOptions(this.options.Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    }

    public bool IsEnabled => true;

    public async Task<ResponseResult> EnqueueAsync(ResponseRequest request, AsyncResponsesRequestContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (!Volatile.Read(ref accepting) || !await capacity.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("Background execution capacity is unavailable. Retry the request later.");

        Execution? execution = null;
        ResponseResult? response = null;
        var registered = false;
        try
        {
            var message = new AsyncResponsesQueueMessage
            {
                Request = AsyncResponseLifecycle.CloneForQueue(request),
                Context = JsonSerializer.Deserialize<AsyncResponsesRequestContext>(
                    JsonSerializer.Serialize(context), JsonSerializerOptions.Web)!
            };
            var privacy = new HeaderAuthResponsePrivacy(message.Request, message.Context);
            response = privacy.Sanitize(AsyncResponseLifecycle.CreateQueuedResponse(message.Request));
            message.ResponseId = response.Id;
            message.CreatedAt = response.CreatedAt;
            execution = new(message, privacy, clock.GetUtcNow() + options.ExecutionLifetime);

            // This is the first and only durable enqueue payload: a safe Response, not an execution request.
            await store.CreateAsync(response, execution.Deadline, cancellationToken, context.UserId);
            cancellationToken.ThrowIfCancellationRequested();
            executions[response.Id] = execution;
            registered = true;
            if (!Volatile.Read(ref accepting) || !queue.Writer.TryWrite(response.Id))
                throw new InvalidOperationException("Background execution capacity is unavailable. Retry the request later.");
            return response;
        }
        catch
        {
            if (response is not null)
            {
                // An upload acknowledgement can be lost. Only a credential-free Response can be orphaned.
                try { await store.DeleteAsync(response.Id, CancellationToken.None, context.UserId); }
                catch { logger.LogWarning("Unable to clean up an unpublished background response."); }
            }
            if (registered) Release(response!.Id);
            else
            {
                execution?.Dispose();
                capacity.Release();
            }
            // Do not expose storage/serialization exception messages, which can include request material.
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
            throw new InvalidOperationException("Unable to enqueue background response.");
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Task.WhenAll(WorkLoopAsync(stoppingToken), SweepLoopAsync(stoppingToken));

    private async Task WorkLoopAsync(CancellationToken cancellationToken)
    {
        await foreach (var id in queue.Reader.ReadAllAsync(cancellationToken))
            await ProcessAsync(id, cancellationToken);
    }

    internal async Task<bool> ProcessNextAsync(CancellationToken cancellationToken = default)
    {
        if (!queue.Reader.TryRead(out var id)) return false;
        await ProcessAsync(id, cancellationToken);
        return true;
    }

    private async Task ProcessAsync(string id, CancellationToken cancellationToken)
    {
        if (!executions.TryGetValue(id, out var execution)) return;
        try
        {
            var current = await store.ReadAsync(id, cancellationToken, execution.Message.Context.UserId);
            if (current is null || IsTerminal(current.Response)) return;
            if (clock.GetUtcNow() >= execution.Deadline)
            {
                await ExpireAsync(current, cancellationToken, execution.Message.Context.UserId);
                return;
            }

            var inProgress = execution.Privacy.Sanitize(current.Response);
            inProgress.Status = "in_progress";
            if (!await store.ReplaceAsync(current, inProgress, cancellationToken, execution.Message.Context.UserId)) return;

            var remaining = execution.Deadline - clock.GetUtcNow();
            using var timeout = new CancellationTokenSource(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, execution.Token, timeout.Token);
            ResponseResult result;
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                result = await processor.ProcessAsync(execution.Message, linked.Token);
                result = execution.Privacy.Sanitize(AsyncResponseLifecycle.NormalizeBackgroundResult(execution.Message, result));
                // Generated lifecycle identifiers must not depend on a downstream result or redaction.
                result.Id = id;
                result.CreatedAt = execution.Message.CreatedAt;
            }
            catch
            {
                logger.LogWarning("HeaderAuth background execution failed for response {ResponseId}.", id);
                result = Failure(inProgress, "background_execution_failed", "Background execution failed or was interrupted.");
            }

            // Retry only safe response persistence, never agent/tool execution.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    current = await store.ReadAsync(id, cancellationToken, execution.Message.Context.UserId);
                    if (current is null || IsTerminal(current.Response)) return;
                    if (clock.GetUtcNow() >= execution.Deadline)
                    {
                        await ExpireAsync(current, cancellationToken, execution.Message.Context.UserId);
                        return;
                    }
                    if (await store.ReplaceAsync(current, result, cancellationToken, execution.Message.Context.UserId)) return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch
                {
                    logger.LogWarning("Unable to persist HeaderAuth background result for response {ResponseId}.", id);
                }
                await Task.Delay(TimeSpan.FromSeconds(1), clock, cancellationToken);
            }
            // If persistence stays unavailable the durable deadline makes the response fail when storage recovers.
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch
        {
            logger.LogWarning("HeaderAuth background processing interrupted for response {ResponseId}.", id);
            // No raw exception logging: downstream exceptions can contain auth headers or echoed bodies.
        }
        finally { Release(id); }
    }

    private async Task SweepLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await SweepAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch { logger.LogWarning("HeaderAuth background deadline sweep could not access response storage."); }
            await Task.Delay(options.SweepInterval, clock, cancellationToken);
        }
    }

    internal async Task SweepAsync(CancellationToken cancellationToken = default)
    {
        // Drop expired execution credentials even when durable storage is unavailable.
        foreach (var pair in executions)
            if (clock.GetUtcNow() >= pair.Value.Deadline) Release(pair.Key);

        foreach (var current in await store.ListAsync(cancellationToken, null))
            await ExpireAsync(current, cancellationToken, current.UserId);
    }

    private async Task<bool> ExpireAsync(HeaderAuthStoredResponse current, CancellationToken cancellationToken, string? userId)
    {
        if (current.Deadline is null || clock.GetUtcNow() < current.Deadline || IsTerminal(current.Response)) return false;
        var failed = Failure(current.Response, "background_execution_expired",
            "The memory-only execution deadline elapsed. The accepting process may have stopped. Submit a new request with credentials to run again.");
        return await store.ReplaceAsync(current, failed, cancellationToken, userId);
    }

    public async Task<ResponseResult?> GetAsync(string responseId, CancellationToken cancellationToken = default, string? userId = null)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var current = await store.ReadAsync(responseId, cancellationToken, userId);
            if (current is null) return null;
            if (current.Deadline is null || clock.GetUtcNow() < current.Deadline || IsTerminal(current.Response)) return current.Response;
            if (await ExpireAsync(current, cancellationToken, userId))
                return (await store.ReadAsync(responseId, cancellationToken, userId))?.Response;
        }
        return (await store.ReadAsync(responseId, cancellationToken, userId))?.Response;
    }

    public async Task<IReadOnlyList<ResponseResult>> ListAsync(CancellationToken cancellationToken = default, string? userId = null)
    {
        var results = new List<ResponseResult>();
        foreach (var item in await store.ListAsync(cancellationToken, userId))
        {
            var response = await GetAsync(item.Response.Id, cancellationToken, item.UserId);
            if (response is not null) results.Add(response);
        }
        return results.OrderByDescending(response => response.CreatedAt).ThenByDescending(response => response.Id).ToList();
    }

    public async Task<bool> DeleteAsync(string responseId, CancellationToken cancellationToken = default, string? userId = null)
    {
        var deleted = await store.DeleteAsync(responseId, cancellationToken, userId);
        if (deleted) Release(responseId);
        return deleted;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref accepting, false);
        queue.Writer.TryComplete();
        foreach (var id in executions.Keys) Release(id);
        // Lost executions become durably failed on deadline, also if shutdown cannot reach storage.
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        Volatile.Write(ref accepting, false);
        foreach (var id in executions.Keys) Release(id);
        base.Dispose();
    }

    private void Release(string id)
    {
        if (!executions.TryRemove(id, out var execution)) return;
        execution.Dispose();
        capacity.Release();
    }

    private static bool IsTerminal(ResponseResult response) => response.Status is "completed" or "failed" or "cancelled" or "incomplete";

    private ResponseResult Failure(ResponseResult response, string code, string message)
    {
        // Clone: a failed CAS must never mutate the snapshot that another reader sees.
        var failed = HeaderAuthResponsePrivacy.ForResponse(response).Sanitize(response);
        failed.Status = "failed";
        failed.CompletedAt = clock.GetUtcNow().ToUnixTimeSeconds();
        failed.Error = new ResponseResultError { Code = code, Message = message };
        AsyncResponseLifecycle.EnsureBackgroundProperty(failed);
        return failed;
    }

    private sealed class Execution : IDisposable
    {
        private readonly CancellationTokenSource cancellation = new();
        public AsyncResponsesQueueMessage Message { get; }
        public HeaderAuthResponsePrivacy Privacy { get; }
        public DateTimeOffset Deadline { get; }
        public CancellationToken Token { get; }

        public Execution(AsyncResponsesQueueMessage message, HeaderAuthResponsePrivacy privacy, DateTimeOffset deadline)
        {
            Message = message;
            Privacy = privacy;
            Deadline = deadline;
            Token = cancellation.Token;
        }

        public void Dispose()
        {
            try { cancellation.Cancel(); }
            catch { /* Cancellation callbacks must not prevent memory cleanup or expose downstream errors. */ }
            finally { cancellation.Dispose(); }
        }
    }
}
