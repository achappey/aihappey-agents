using System.Text.Json;
using AIHappey.Responses;
using Microsoft.Extensions.Options;

namespace AgentHappey.AsyncResponses;

/// <summary>A single-owner durable queue. Atomic renames publish and claim work.</summary>
public sealed class LocalAsyncResponsesQueue : IDisposable
{
    private readonly string root;
    private readonly FileStream ownership;
    private readonly SemaphoreSlim mutation = new(1, 1);

    public LocalAsyncResponsesQueue(IOptions<LocalResponseStorageOptions> options)
    {
        root = Path.Combine(options.Value.GetRootPath(), "queue");
        foreach (var directory in new[] { "staging", "pending", "processing", "invalid" })
            Directory.CreateDirectory(Path.Combine(root, directory));
        try
        {
            ownership = new FileStream(Path.Combine(root, "worker.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException("The local response queue is already in use or could not be locked. Use a separate LocalResponses:RootPath for another host.", exception);
        }
    }

    public async Task EnqueueAsync(AsyncResponsesQueueMessage message, ResponseResult response,
        IAsyncResponseStore store, CancellationToken cancellationToken)
    {
        var name = $"{message.CreatedAt:D20}-{LocalResponseFiles.Segment(message.ResponseId)}.json";
        var stagedPath = Path.Combine(root, "staging", name);
        await mutation.WaitAsync(cancellationToken);
        try
        {
            // A crash between these steps is recovered from the staged message.
            await LocalResponseFiles.WriteAsync(stagedPath, message, cancellationToken);
            await store.SaveAsync(response, cancellationToken, message.Context.UserId);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(stagedPath, Path.Combine(root, "pending", name));
        }
        catch
        {
            File.Delete(stagedPath);
            throw;
        }
        finally
        {
            mutation.Release();
        }
    }

    public async Task RecoverAsync(IAsyncResponseStore store, CancellationToken cancellationToken)
    {
        await mutation.WaitAsync(cancellationToken);
        try
        {
            foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "staging"), "*.json"))
            {
                var message = await ReadMessageAsync(path, cancellationToken);
                if (message is null)
                    continue;
                if (await store.GetAsync(message.ResponseId, cancellationToken, message.Context.UserId) is null)
                {
                    var response = AsyncResponseLifecycle.CreateQueuedResponse(message.Request);
                    response.Id = message.ResponseId;
                    response.CreatedAt = message.CreatedAt;
                    await store.SaveAsync(response, cancellationToken, message.Context.UserId);
                }
                File.Move(path, Path.Combine(root, "pending", Path.GetFileName(path)));
            }
            // Unpublished temporary writes never contained an acknowledged job.
            foreach (var path in Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories))
                File.Delete(path);
        }
        finally
        {
            mutation.Release();
        }
    }

    public async Task<(string Path, AsyncResponsesQueueMessage Message)?> ClaimAsync(CancellationToken cancellationToken)
    {
        await mutation.WaitAsync(cancellationToken);
        try
        {
            // Interrupted/infrastructure-failed work is retried before newer jobs.
            foreach (var directory in new[] { "processing", "pending" })
            {
                foreach (var path in Directory.EnumerateFiles(Path.Combine(root, directory), "*.json")
                    .OrderBy(path => path, StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var message = await ReadMessageAsync(path, cancellationToken);
                    if (message is null)
                        continue;
                    var claimedPath = Path.Combine(root, "processing", Path.GetFileName(path));
                    if (directory == "pending")
                        File.Move(path, claimedPath);
                    return (claimedPath, message);
                }
            }
            return null;
        }
        finally
        {
            mutation.Release();
        }
    }

    public void Acknowledge(string claimedPath) => File.Delete(claimedPath);

    private async Task<AsyncResponsesQueueMessage?> ReadMessageAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var message = await LocalResponseFiles.ReadAsync<AsyncResponsesQueueMessage>(path, cancellationToken);
            if (message is null || string.IsNullOrWhiteSpace(message.ResponseId)
                || message.Request is null || message.Context is null)
                throw new JsonException("Invalid local queue message.");
            return message;
        }
        catch (JsonException)
        {
            // Keep corrupt documents for inspection, without blocking other work or
            // logging their contents (request context can contain credentials).
            File.Move(path, Path.Combine(root, "invalid", Guid.NewGuid().ToString("N") + ".json"));
            return null;
        }
    }

    public void Dispose()
    {
        ownership.Dispose();
        mutation.Dispose();
    }
}
