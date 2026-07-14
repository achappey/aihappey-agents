using AgentHappey.AsyncResponses;
using AIHappey.Responses;

namespace AgentHappey.Tests;

public sealed class AsyncResponsesDeleteTests
{
    [Fact]
    public async Task Disabled_async_responses_delete_reports_missing_response()
    {
        var service = new DisabledAsyncResponsesService();

        var deleted = await service.DeleteAsync("resp_missing");

        Assert.False(deleted);
    }

    [Fact]
    public async Task Disabled_async_responses_delete_reports_missing_response_for_empty_id()
    {
        var service = new DisabledAsyncResponsesService();

        var deleted = await service.DeleteAsync(string.Empty);

        Assert.False(deleted);
    }

    [Fact]
    public async Task Disabled_async_responses_delete_reports_missing_response_for_scoped_user()
    {
        var service = new DisabledAsyncResponsesService();

        var deleted = await service.DeleteAsync("resp_missing", userId: "user-a");

        Assert.False(deleted);
    }

    [Fact]
    public async Task Disabled_async_responses_get_reports_missing_response_for_scoped_user()
    {
        var service = new DisabledAsyncResponsesService();

        var response = await service.GetAsync("resp_missing", userId: "user-a");

        Assert.Null(response);
    }

    [Fact]
    public async Task Disabled_async_responses_list_returns_empty_responses_for_scoped_user()
    {
        var service = new DisabledAsyncResponsesService();

        var responses = await service.ListAsync(userId: "user-a");

        Assert.Empty(responses);
    }

    [Fact]
    public void Azure_async_response_store_uses_legacy_blob_name_without_user_scope()
    {
        var blobName = AzureAsyncResponseStore.GetBlobName("resp_123");

        Assert.Equal("responses/resp_123.json", blobName);
    }

    [Fact]
    public void Azure_async_response_store_uses_user_folder_for_scoped_blob_name()
    {
        var blobName = AzureAsyncResponseStore.GetBlobName("resp_123", "user-a");

        Assert.Equal("responses/user-a/resp_123.json", blobName);
    }

    [Fact]
    public void Azure_async_response_store_escapes_user_and_response_segments()
    {
        var blobName = AzureAsyncResponseStore.GetBlobName(" resp/a ", " user/b ");

        Assert.Equal("responses/user%2Fb/resp%2Fa.json", blobName);
    }

    [Fact]
    public void Azure_async_response_store_uses_user_folder_for_scoped_blob_prefix()
    {
        var prefix = AzureAsyncResponseStore.GetBlobPrefix("user-a");

        Assert.Equal("responses/user-a/", prefix);
    }

    [Fact]
    public async Task User_scoped_store_does_not_return_responses_for_other_users()
    {
        var store = new InMemoryScopedAsyncResponseStore();
        await store.SaveAsync(new ResponseResult { Id = "resp_123", Object = "response" }, userId: "user-a");

        var currentUserResponse = await store.GetAsync("resp_123", userId: "user-a");
        var otherUserResponse = await store.GetAsync("resp_123", userId: "user-b");

        Assert.NotNull(currentUserResponse);
        Assert.Null(otherUserResponse);
    }

    [Fact]
    public async Task User_scoped_store_delete_only_deletes_current_users_response()
    {
        var store = new InMemoryScopedAsyncResponseStore();
        await store.SaveAsync(new ResponseResult { Id = "resp_123", Object = "response" }, userId: "user-a");
        await store.SaveAsync(new ResponseResult { Id = "resp_123", Object = "response" }, userId: "user-b");

        var deleted = await store.DeleteAsync("resp_123", userId: "user-a");

        Assert.True(deleted);
        Assert.Null(await store.GetAsync("resp_123", userId: "user-a"));
        Assert.NotNull(await store.GetAsync("resp_123", userId: "user-b"));
    }

    [Fact]
    public async Task User_scoped_store_keeps_unscoped_responses_separate_for_header_auth_compatibility()
    {
        var store = new InMemoryScopedAsyncResponseStore();
        await store.SaveAsync(new ResponseResult { Id = "resp_123", Object = "response" });
        await store.SaveAsync(new ResponseResult { Id = "resp_123", Object = "response" }, userId: "user-a");

        var unscopedResponse = await store.GetAsync("resp_123");
        var scopedResponse = await store.GetAsync("resp_123", userId: "user-a");

        Assert.NotNull(unscopedResponse);
        Assert.NotNull(scopedResponse);
    }

    [Fact]
    public async Task User_scoped_store_list_only_returns_current_users_responses()
    {
        var store = new InMemoryScopedAsyncResponseStore();
        await store.SaveAsync(new ResponseResult { Id = "resp_a", Object = "response", CreatedAt = 1 }, userId: "user-a");
        await store.SaveAsync(new ResponseResult { Id = "resp_b", Object = "response", CreatedAt = 2 }, userId: "user-b");
        await store.SaveAsync(new ResponseResult { Id = "resp_c", Object = "response", CreatedAt = 3 }, userId: "user-a");

        var responses = await store.ListAsync(userId: "user-a");

        Assert.Equal(["resp_c", "resp_a"], responses.Select(response => response.Id));
    }

    private sealed class InMemoryScopedAsyncResponseStore : IAsyncResponseStore
    {
        private readonly Dictionary<(string? UserId, string ResponseId), ResponseResult> responses = [];

        public Task SaveAsync(ResponseResult response, CancellationToken cancellationToken = default, string? userId = null)
        {
            responses[(NormalizeUserId(userId), response.Id!)] = response;
            return Task.CompletedTask;
        }

        public Task<ResponseResult?> GetAsync(string responseId, CancellationToken cancellationToken = default, string? userId = null)
            => Task.FromResult(responses.GetValueOrDefault((NormalizeUserId(userId), responseId)));

        public Task<IReadOnlyList<ResponseResult>> ListAsync(CancellationToken cancellationToken = default, string? userId = null)
            => Task.FromResult<IReadOnlyList<ResponseResult>>(responses
                .Where(response => response.Key.UserId == NormalizeUserId(userId))
                .Select(response => response.Value)
                .OrderByDescending(response => response.CreatedAt)
                .ThenByDescending(response => response.Id, StringComparer.Ordinal)
                .ToList());

        public Task<bool> DeleteAsync(string responseId, CancellationToken cancellationToken = default, string? userId = null)
            => Task.FromResult(responses.Remove((NormalizeUserId(userId), responseId)));

        private static string? NormalizeUserId(string? userId)
            => string.IsNullOrWhiteSpace(userId) ? null : userId.Trim();
    }
}
