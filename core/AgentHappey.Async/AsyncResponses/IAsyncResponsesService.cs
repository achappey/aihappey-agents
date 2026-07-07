using AIHappey.Responses;

namespace AgentHappey.AsyncResponses;

public interface IAsyncResponsesService
{
    bool IsEnabled { get; }

    Task<ResponseResult> EnqueueAsync(
        ResponseRequest request,
        AsyncResponsesRequestContext context,
        CancellationToken cancellationToken = default);

    Task<ResponseResult?> GetAsync(string responseId, CancellationToken cancellationToken = default, string? userId = null);

    Task<bool> DeleteAsync(string responseId, CancellationToken cancellationToken = default, string? userId = null);
}
