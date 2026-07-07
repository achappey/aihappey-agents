using AIHappey.Responses;

namespace AgentHappey.AsyncResponses;

public interface IAsyncResponseStore
{
    Task SaveAsync(ResponseResult response, CancellationToken cancellationToken = default);

    Task<ResponseResult?> GetAsync(string responseId, CancellationToken cancellationToken = default);
}
