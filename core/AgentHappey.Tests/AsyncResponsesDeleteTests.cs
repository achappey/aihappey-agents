using AgentHappey.AsyncResponses;

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
}
