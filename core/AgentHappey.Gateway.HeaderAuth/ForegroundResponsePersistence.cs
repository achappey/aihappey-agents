using AgentHappey.AsyncResponses;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using Microsoft.Extensions.Options;

namespace AgentHappey.HeaderAuth;

public sealed class ForegroundResponsePersistence(
    IOptions<HeaderAuthHostOptions> options,
    IServiceProvider services)
{
    public Task SaveAsync(ResponseRequest request, ResponseResult response, CancellationToken cancellationToken = default)
        => options.Value.PersistForegroundResponses && request.Store == true
            ? services.GetRequiredService<IAsyncResponseStore>().SaveAsync(response, cancellationToken)
            : Task.CompletedTask;

    public Task SaveTerminalAsync(ResponseRequest request, ResponseStreamPart part, CancellationToken cancellationToken = default)
        => part switch
        {
            ResponseCompleted completed => SaveAsync(request, completed.Response, cancellationToken),
            ResponseFailed failed => SaveAsync(request, failed.Response, cancellationToken),
            _ => Task.CompletedTask
        };
}
