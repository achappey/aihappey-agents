using AIHappey.Responses;
using Microsoft.Extensions.Options;

namespace AgentHappey.AsyncResponses;

public sealed class LocalAsyncResponseStore(IOptions<LocalResponseStorageOptions> options) : IAsyncResponseStore
{
    private readonly string root = Path.Combine(options.Value.GetRootPath(), "responses");

    public Task SaveAsync(ResponseResult response, CancellationToken cancellationToken = default, string? userId = null)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (string.IsNullOrWhiteSpace(response.Id))
            throw new InvalidOperationException("Cannot store a response without an id.");

        return LocalResponseFiles.WriteAsync(GetPath(response.Id, userId), response, cancellationToken);
    }

    public Task<ResponseResult?> GetAsync(string responseId, CancellationToken cancellationToken = default, string? userId = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return string.IsNullOrWhiteSpace(responseId)
            ? Task.FromResult<ResponseResult?>(null)
            : LocalResponseFiles.ReadAsync<ResponseResult>(GetPath(responseId, userId), cancellationToken);
    }

    public async Task<IReadOnlyList<ResponseResult>> ListAsync(CancellationToken cancellationToken = default, string? userId = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = GetDirectory(userId);
        if (!Directory.Exists(directory))
            return [];

        var responses = new List<ResponseResult>();
        // Like the blob prefix, an unscoped listing covers all stored responses.
        foreach (var path in Directory.EnumerateFiles(directory, "*.json",
            string.IsNullOrWhiteSpace(userId) ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
        {
            var response = await LocalResponseFiles.ReadAsync<ResponseResult>(path, cancellationToken);
            if (response is not null)
                responses.Add(response);
        }

        return responses.OrderByDescending(response => response.CreatedAt)
            .ThenByDescending(response => response.Id, StringComparer.Ordinal).ToList();
    }

    public Task<bool> DeleteAsync(string responseId, CancellationToken cancellationToken = default, string? userId = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(responseId))
            return Task.FromResult(false);

        var path = GetPath(responseId, userId);
        // A same-volume move atomically distinguishes missing items and concurrent deletions.
        var deletedPath = path + "." + Guid.NewGuid().ToString("N") + ".deleted";
        try
        {
            File.Move(path, deletedPath);
        }
        catch (FileNotFoundException) { return Task.FromResult(false); }
        catch (DirectoryNotFoundException) { return Task.FromResult(false); }

        File.Delete(deletedPath);
        return Task.FromResult(true);
    }

    private string GetDirectory(string? userId)
        => string.IsNullOrWhiteSpace(userId) ? root : Path.Combine(root, LocalResponseFiles.Segment(userId));

    private string GetPath(string responseId, string? userId)
        => Path.Combine(GetDirectory(userId), LocalResponseFiles.Segment(responseId) + ".json");
}
