using AIHappey.Responses;

namespace AgentHappey.AsyncResponses;

public sealed class LocalAsyncResponsesService(LocalAsyncResponsesQueue queue, IAsyncResponseStore store) : IAsyncResponsesService
{
    public bool IsEnabled => true;

    public async Task<ResponseResult> EnqueueAsync(ResponseRequest request, AsyncResponsesRequestContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        var response = AsyncResponseLifecycle.CreateQueuedResponse(request);
        var message = new AsyncResponsesQueueMessage
        {
            ResponseId = response.Id,
            CreatedAt = response.CreatedAt,
            Request = AsyncResponseLifecycle.CloneForQueue(request),
            Context = context
        };
        await queue.EnqueueAsync(message, response, store, cancellationToken);
        return response;
    }

    public Task<ResponseResult?> GetAsync(string responseId, CancellationToken cancellationToken = default, string? userId = null)
        => store.GetAsync(responseId, cancellationToken, userId);

    public Task<IReadOnlyList<ResponseResult>> ListAsync(CancellationToken cancellationToken = default, string? userId = null)
        => store.ListAsync(cancellationToken, userId);

    public Task<bool> DeleteAsync(string responseId, CancellationToken cancellationToken = default, string? userId = null)
        => store.DeleteAsync(responseId, cancellationToken, userId);
}
