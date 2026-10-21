using System.Text.Json;
using AgentHappey.Core.ChatClient;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using Microsoft.Extensions.AI;

namespace AgentHappey.Tests;

public sealed partial class AgentChatClientFixtureTests
{
    private const string RemoteDownloadError = "Remote attachment download from 'upload.wikimedia.org' failed: HTTP 403 Forbidden.";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Gateway_stream_error_envelope_preserves_the_downstream_error(bool typed)
    {
        var payload = typed
            ? JsonSerializer.Serialize(new { type = "error", message = RemoteDownloadError, code = "download_failed", param = "image_url" })
            : JsonSerializer.Serialize(new { error = new { message = RemoteDownloadError, type = "server_error", code = (string?)null } });
        using var httpClient = CreateHttpClient(_ => CreateStreamingResponse($"data: {payload}\n\ndata: [DONE]\n\n"));

        var events = await CollectAsync(httpClient.GetResponsesUpdates(new ResponseRequest { Model = "openai/gpt-fixture" }));

        var error = Assert.IsType<ResponseError>(Assert.Single(events));
        Assert.Equal(RemoteDownloadError, error.Message);
        Assert.Equal(typed ? "download_failed" : null, error.Code);
    }

    [Fact]
    public async Task Gateway_stream_error_envelope_becomes_agent_error_content_not_a_parse_exception()
    {
        var payload = JsonSerializer.Serialize(new { error = new { message = RemoteDownloadError, type = "server_error", code = (string?)null } });
        using var httpClient = CreateHttpClient(_ => CreateStreamingResponse($"data: {payload}\n\n"));
        using var client = CreateClient(httpClient, CreateAgent(modelId: "openai/gpt-fixture"));

        var updates = await CollectAsync(client.GetStreamingResponseAsync(CreateUserMessages("Describe the image")));

        var error = Assert.Single(updates.SelectMany(update => update.Contents).OfType<ErrorContent>());
        Assert.Equal(RemoteDownloadError, error.Message);
    }

    [Fact]
    public async Task Malformed_stream_json_still_reports_a_parse_failure()
    {
        using var httpClient = CreateHttpClient(_ => CreateStreamingResponse("data: {not-json}\n\n"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await CollectAsync(httpClient.GetResponsesUpdates(new ResponseRequest { Model = "openai/gpt-fixture" })));

        Assert.Contains("Failed to parse SSE json event", error.Message);
    }
}
