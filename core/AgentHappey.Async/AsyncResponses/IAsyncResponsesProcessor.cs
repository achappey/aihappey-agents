using AIHappey.Responses;

namespace AgentHappey.AsyncResponses;

public interface IAsyncResponsesProcessor
{
    Task<ResponseResult> ProcessAsync(AsyncResponsesQueueMessage message, CancellationToken cancellationToken = default);
}
