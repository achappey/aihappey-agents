using AIHappey.Responses;
using AgentHappey.Common.Models;

namespace AgentHappey.AsyncResponses;

public interface IAsyncResponsesService
{
    bool IsEnabled { get; }

    Task<ResponseResult> EnqueueAsync(
        AgentResponseRequest request,
        AsyncResponsesRequestContext context,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ResponseResult>> ListAsync(CancellationToken cancellationToken = default, string? userId = null);

    Task<ResponseResult?> GetAsync(string responseId, CancellationToken cancellationToken = default, string? userId = null);

    Task<bool> DeleteAsync(string responseId, CancellationToken cancellationToken = default, string? userId = null);
}
