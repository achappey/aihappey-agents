using AIHappey.Responses;

namespace AgentHappey.AsyncResponses;

public sealed class DisabledAsyncResponsesService : IAsyncResponsesService
{
    public bool IsEnabled => false;

    public Task<ResponseResult> EnqueueAsync(
        ResponseRequest request,
        AsyncResponsesRequestContext context,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("Async agent responses are not configured.");

    public Task<ResponseResult?> GetAsync(string responseId, CancellationToken cancellationToken = default, string? userId = null)
        => Task.FromResult<ResponseResult?>(null);

    public Task<bool> DeleteAsync(string responseId, CancellationToken cancellationToken = default, string? userId = null)
        => Task.FromResult(false);
}
