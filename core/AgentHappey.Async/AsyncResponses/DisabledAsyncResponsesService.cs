using AIHappey.Responses;
using AgentHappey.Common.Models;

namespace AgentHappey.AsyncResponses;

public sealed class DisabledAsyncResponsesService : IAsyncResponsesService
{
    public bool IsEnabled => false;

    public Task<ResponseResult> EnqueueAsync(
        AgentResponseRequest request,
        AsyncResponsesRequestContext context,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("Async agent responses are not configured.");

    public Task<ResponseResult?> GetAsync(string responseId, CancellationToken cancellationToken = default, string? userId = null)
        => Task.FromResult<ResponseResult?>(null);

    public Task<IReadOnlyList<ResponseResult>> ListAsync(CancellationToken cancellationToken = default, string? userId = null)
        => Task.FromResult<IReadOnlyList<ResponseResult>>([]);

    public Task<bool> DeleteAsync(string responseId, CancellationToken cancellationToken = default, string? userId = null)
        => Task.FromResult(false);
}
