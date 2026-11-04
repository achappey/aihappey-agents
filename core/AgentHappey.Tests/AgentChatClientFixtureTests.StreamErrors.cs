using System.Net;
using System.Text.Json;
using AgentHappey.Core;
using AgentHappey.Core.ChatClient;
using AgentHappey.Core.Diagnostics;
using AgentHappey.Core.Responses;
using AIHappey.Responses;
using AIHappey.Responses.Diagnostics;
using AIHappey.Responses.Streaming;
using AIHappey.Vercel.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentHappey.Tests;

public sealed partial class AgentChatClientFixtureTests
{
    private const string RemoteDownloadError = "Remote attachment download from 'upload.wikimedia.org' failed: HTTP 403 Forbidden.";
    private const string ContextLimitError = "Your input exceeds the context window of this model. Please adjust your input and try again.";

    public static IEnumerable<object[]> ProviderStreamErrorCases()
    {
        var message = JsonSerializer.Serialize(ContextLimitError);
        var nested = $"{{\"type\":\"invalid_request_error\",\"code\":\"context_length_exceeded\",\"message\":{message},\"param\":\"input\"}}";
        yield return [$"{{\"sequence_number\":2,\"message\":null,\"param\":null,\"code\":null,\"type\":\"error\",\"error\":{nested}}}", ContextLimitError];
        yield return [$"{{\"type\":\"error\",\"message\":\" \",\"error\":{nested}}}", ContextLimitError];
        yield return [$"{{\"type\":\"error\",\"message\":{message},\"code\":\"context_length_exceeded\",\"param\":\"input\"}}", ContextLimitError];
        yield return [$"{{\"error\":{nested}}}", ContextLimitError];
        yield return [$"{{\"type\":\"error\",\"error\":{message}}}", ContextLimitError];
        yield return [$"{{\"type\":\"error\",\"message\":42,\"sequence_number\":null,\"code\":123,\"error\":{nested}}}", ContextLimitError];
        yield return [$"{{\"type\":\"response.failed\",\"sequence_number\":2,\"response\":{{\"id\":\"failed\",\"status\":\"failed\",\"error\":{nested}}}}}", ContextLimitError];
        yield return ["{\"type\":\"error\",\"message\":null,\"error\":{\"code\":\"server_error\"}}", ProviderErrorMessages.DefaultMessage];
        yield return ["{\"type\":\"response.failed\",\"response\":{\"status\":\"failed\",\"error\":null}}", ProviderErrorMessages.DefaultMessage];
    }

    [Theory]
    [MemberData(nameof(ProviderStreamErrorCases))]
    public async Task Provider_stream_errors_reach_chat_and_native_responses_as_message_only(string payload, string expected)
    {
        using var httpClient = CreateHttpClient(_ => CreateStreamingResponse($"data: {payload}\n\ndata: [DONE]\n\n"));
        using var client = CreateClient(httpClient, CreateAgent(modelId: "openai/gpt-fixture"));

        var updates = await CollectAsync(client.GetStreamingResponseAsync(CreateUserMessages("hello")));
        Assert.Equal(expected, Assert.Single(updates.SelectMany(update => update.Contents).OfType<ErrorContent>()).Message);

        var agentUpdates = updates.Select(update => new AgentResponseUpdate(update.Role, update.Contents)).ToList();
        var ui = await CollectAsync(new StreamingContentMapper().MapAsync(ToAsync(agentUpdates)));
        var uiError = Assert.Single(ui.OfType<ErrorUIPart>());
        using var serialized = JsonDocument.Parse(JsonSerializer.Serialize<UIMessagePart>(uiError, JsonSerializerOptions.Web));
        Assert.Equal("error", serialized.RootElement.GetProperty("type").GetString());
        Assert.Equal(expected, serialized.RootElement.GetProperty("errorText").GetString());

        var native = await CollectAsync(new ResponsesNativeMapper().MapStreamingAsync(
            new ResponseRequest(), "Agent", "openai", ToAsync(agentUpdates)));
        Assert.Equal(expected, Assert.Single(native.OfType<ResponseFailed>()).Response.Error?.Message);
        Assert.Empty(native.OfType<ResponseCompleted>());
    }

    [Fact]
    public async Task Screenshot_error_keeps_nested_diagnostics_and_sequence_number()
    {
        var payload = (string)ProviderStreamErrorCases().First()[0];
        var emitter = new ErrorRecordingEmitter();
        using var scope = ResponsesTransportScope.Begin(new ResponsesTransportDebugObserver(emitter));
        using var http = CreateHttpClient(_ => CreateStreamingResponse($"data: {payload}\n\n"));

        var parts = await CollectAsync(http.GetResponsesUpdates(new ResponseRequest { Model = "openai/gpt-fixture" }));

        var error = Assert.IsType<ResponseError>(Assert.Single(parts));
        Assert.Equal(ContextLimitError, error.Message);
        Assert.Equal(2, error.SequenceNumber);
        Assert.Equal("context_length_exceeded", error.Code);
        Assert.Equal("input", error.Param);
        Assert.Equal("invalid_request_error", error.AdditionalProperties!["error"].GetProperty("type").GetString());
        var diagnostic = Assert.Single(emitter.Payloads, item => item.Kind == "response-event").Payload;
        Assert.Contains(payload, diagnostic.Content);
        Assert.Equal(JsonValueKind.Null, diagnostic.Data!.Value.GetProperty("message").ValueKind);
        Assert.Equal("context_length_exceeded", diagnostic.Data.Value.GetProperty("error").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("{\"status\":\"failed\",\"error\":{\"message\":\"Account quota exhausted.\",\"code\":\"quota\",\"param\":\"model\"}}", "Account quota exhausted.")]
    [InlineData("{\"error\":\"The model is unavailable.\"}", "The model is unavailable.")]
    [InlineData("{\"message\":null,\"error\":{\"message\":\"Invalid API key.\"}}", "Invalid API key.")]
    [InlineData("{\"status\":\"failed\",\"error\":null}", ProviderErrorMessages.DefaultMessage)]
    public async Task Failed_non_streaming_results_are_not_empty_successes(string body, string expected)
    {
        var emitter = new ErrorRecordingEmitter();
        using var scope = ResponsesTransportScope.Begin(new ResponsesTransportDebugObserver(emitter));
        using var http = CreateHttpClient(_ => CreateJsonResponse(body));
        using var client = CreateClient(http, CreateAgent());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(CreateUserMessages("hello")));

        Assert.Equal(expected, error.Message);
        Assert.Equal(body, error.Data[ProviderErrorMessages.ResponseBodyKey]);
        Assert.Equal(body, Assert.Single(emitter.Payloads, item => item.Kind == "response").Payload.Content);
    }

    [Theory]
    [InlineData(401, "{\"error\":{\"message\":\"Invalid API key.\",\"code\":\"invalid_api_key\"}}", "Invalid API key.")]
    [InlineData(429, "{\"error\":{\"message\":\"Rate limit reached.\",\"code\":\"rate_limit_exceeded\"}}", "Rate limit reached.")]
    [InlineData(500, "{\"message\":\"Provider temporarily unavailable.\"}", "Provider temporarily unavailable.")]
    [InlineData(503, "Service unavailable.", "Service unavailable.")]
    [InlineData(502, "<html>private proxy diagnostics</html>", null)]
    [InlineData(500, "{not-json private diagnostics", null)]
    [InlineData(500, "{\"error\":{\"message\":42,\"code\":123}}", null)]
    [InlineData(500, "", null)]
    public async Task Http_failures_preserve_status_and_raw_diagnostics_but_display_only_readable_text(int status, string body, string? expected)
    {
        foreach (var streaming in new[] { false, true })
        {
            var emitter = new ErrorRecordingEmitter();
            using var scope = ResponsesTransportScope.Begin(new ResponsesTransportDebugObserver(emitter));
            using var http = CreateHttpClient(_ => CreateJsonResponse(body, (HttpStatusCode)status));
            using var client = CreateClient(http, CreateAgent());

            var error = await Assert.ThrowsAsync<HttpRequestException>(async () =>
            {
                if (streaming) await CollectAsync(client.GetStreamingResponseAsync(CreateUserMessages("hello")));
                else await client.GetResponseAsync(CreateUserMessages("hello"));
            });

            Assert.Equal((HttpStatusCode)status, error.StatusCode);
            Assert.Equal(body, error.Data[ProviderErrorMessages.ResponseBodyKey]);
            Assert.Equal(body, Assert.Single(emitter.Payloads, item => item.Kind == "response").Payload.Content);
            if (expected is not null) Assert.Equal(expected, error.Message);
            else
            {
                Assert.Contains($"HTTP {status}", error.Message);
                Assert.DoesNotContain("diagnostics", error.Message);
                Assert.DoesNotContain("{", error.Message);
            }
            Assert.Equal(error.Message, ProviderErrorMessages.FromException(error));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transport_errors_and_cancellation_are_not_replaced_by_parse_errors(bool streaming)
    {
        using var http = CreateHttpClient(_ => throw new HttpRequestException("The connection to the model was lost."));
        using var client = CreateClient(http, CreateAgent());
        var error = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            if (streaming) await CollectAsync(client.GetStreamingResponseAsync(CreateUserMessages("hello")));
            else await client.GetResponseAsync(CreateUserMessages("hello"));
        });
        Assert.Equal("The connection to the model was lost.", ProviderErrorMessages.FromException(error));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (streaming) await CollectAsync(client.GetStreamingResponseAsync(CreateUserMessages("hello"), cancellationToken: cancellation.Token));
            else await client.GetResponseAsync(CreateUserMessages("hello"), cancellationToken: cancellation.Token);
        });
    }

    private sealed class ErrorRecordingEmitter : IProviderDebugEmitter
    {
        public bool Enabled => true;
        public List<(string Kind, ProviderDebugPayload Payload)> Payloads { get; } = [];
        public ValueTask EmitAsync(string provider, string operation, string operationId, string kind,
            ProviderDebugPayload payload, CancellationToken cancellationToken = default)
        {
            Payloads.Add((kind, payload));
            return ValueTask.CompletedTask;
        }
    }

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
