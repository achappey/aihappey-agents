using AIHappey.Responses;

namespace AgentHappey.AsyncResponses;

public interface IAsyncResponseStore
{
    Task SaveAsync(ResponseResult response, CancellationToken cancellationToken = default, string? userId = null);

    Task<ResponseResult?> GetAsync(string responseId, CancellationToken cancellationToken = default, string? userId = null);

    Task<bool> DeleteAsync(string responseId, CancellationToken cancellationToken = default, string? userId = null);
}
