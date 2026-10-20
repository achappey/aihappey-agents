using AIHappey.Responses;

namespace AgentHappey.HeaderAuth.AsyncResponses;

/// <summary>A response plus an optimistic concurrency token and a credential-free deadline.</summary>
public sealed record HeaderAuthStoredResponse(ResponseResult Response, string Version, DateTimeOffset? Deadline, string? UserId = null);

public interface IHeaderAuthBackgroundResponseStore
{
    Task CreateAsync(ResponseResult response, DateTimeOffset deadline, CancellationToken cancellationToken, string? userId);
    Task<HeaderAuthStoredResponse?> ReadAsync(string id, CancellationToken cancellationToken, string? userId);
    Task<bool> ReplaceAsync(HeaderAuthStoredResponse previous, ResponseResult response, CancellationToken cancellationToken, string? userId);
    Task<IReadOnlyList<HeaderAuthStoredResponse>> ListAsync(CancellationToken cancellationToken, string? userId);
    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken, string? userId);
}
