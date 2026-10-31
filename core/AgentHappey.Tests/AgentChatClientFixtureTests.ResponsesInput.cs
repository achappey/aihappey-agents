using System.Text.Json;
using AgentHappey.Core.Responses;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentHappey.Tests;

public sealed partial class AgentChatClientFixtureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Direct_responses_input_survives_framework_and_downstream_roundtrip(bool streaming)
    {
        var request = JsonSerializer.Deserialize<ResponseRequest>("""
            {"model":"openai/gpt-fixture","metadata":{"request_tag":"keep-me"},"input":[
              {"type":"reasoning","id":"rs-replay","encrypted_content":"encrypted-state",
               "agent":{"agent_name":"/root/provider-subagent"},
               "summary":[{"type":"summary_text","text":"first"},{"type":"summary_text","text":"second"}],
               "signature":"native-signature"},
              {"type":"message","id":"msg-replay","role":"assistant","status":"completed","phase":"final_answer",
               "agent":{"agent_name":"/root/provider-subagent"},
               "content":[{"type":"output_text","text":"Earlier answer","annotations":[{"type":"citation","index":1}]}]},
              {"type":"function_call","id":"fc-replay","call_id":"call-replay","name":"lookup","namespace":"catalog",
               "arguments":"{\"query\":\"news\"}","status":"completed"},
              {"type":"function_call_output","id":"output-replay","call_id":"call-replay","output":"plain tool result","status":"completed"},
              {"type":"message","role":"user","content":[
                {"type":"input_text","text":"Continue"},
                {"type":"input_image","image_url":"https://files.test/image?sig=a%2Fb","detail":"high"},
                {"type":"input_image","file_id":"file-image","detail":"low"},
                {"type":"input_file","file_id":"file-document","filename":"report.pdf"},
                {"type":"input_file","file_data":"data:application/pdf;base64,AQID","filename":"inline.pdf"}]}]}
            """, ResponseJson.Default)!;
        var mapped = request.ToChatRuntimeRequest();
        string? captured = null;
        using var http = CreateHttpClient(message =>
        {
            Assert.Null(captured); // No media download or other side request.
            captured = message.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return streaming ? CreateStreamingResponse(LoadFixture(StreamingGatewayFixturePath))
                : CreateJsonResponse(LoadFixture(StructuredFixturePath));
        });
        using var client = CreateClient(http, CreateAgent());
        var mapper = new ResponsesNativeMapper();
        ResponseResult result;
        if (streaming)
        {
            var updates = await CollectAsync(client.GetStreamingResponseAsync(mapped.Messages));
            var parts = await CollectAsync(mapper.MapStreamingAsync(request, "StructuredAgent", "openai",
                ToAsync(updates.Select(update => new AgentResponseUpdate(update.Role, update.Contents)
                { MessageId = update.MessageId }))));
            Assert.IsType<ResponseCreated>(parts[0]);
            Assert.IsType<ResponseInProgress>(parts[1]);
            result = Assert.IsType<ResponseCompleted>(parts[^1]).Response;
            Assert.Single(parts.OfType<ResponseCompleted>());
            Assert.Contains(parts, part => part is ResponseOutputTextDelta);
            var sequences = parts.Select(part => JsonSerializer.SerializeToElement(part, ResponseJson.Default)
                .GetProperty("sequence_number").GetInt32()).ToArray();
            Assert.Equal(Enumerable.Range(1, parts.Count), sequences);
        }
        else
        {
            var response = await client.GetResponseAsync(mapped.Messages);
            result = mapper.Map(request, "StructuredAgent", "openai", new AgentResponse(response.Messages)
            { Usage = response.Usage });
        }

        Assert.Equal("completed", result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.OutputText));
        Assert.Equal("keep-me", result.Metadata!["request_tag"]?.ToString());
        Assert.True(JsonSerializer.SerializeToElement(result.Usage).GetProperty("total_tokens").GetInt64() > 0);

        var original = JsonSerializer.SerializeToElement(request.Input, ResponseJson.Default);
        using var body = JsonDocument.Parse(Assert.IsType<string>(captured));
        var replay = body.RootElement.GetProperty("input");
        Assert.Equal(original.GetArrayLength(), replay.GetArrayLength());
        for (var index = 0; index < original.GetArrayLength(); index++)
        {
            // Native identity and raw tool payloads are retained, not reconstructed.
            if (index < 4)
                Assert.Equal(original[index].GetRawText(), replay[index].GetRawText());
        }
        Assert.Equal(original[4].GetProperty("content").GetRawText(), replay[4].GetProperty("content").GetRawText());
    }

    [Fact]
    public async Task Direct_responses_reasoning_roundtrips_through_native_output_and_replay()
    {
        var request = JsonSerializer.Deserialize<ResponseRequest>("""
            {"input":[{"type":"reasoning","id":"rs-encrypted-only","summary":[],
              "encrypted_content":"encrypted-only","signature":"keep-signature"}]}
            """, ResponseJson.Default)!;
        var messages = request.ToChatRuntimeRequest().Messages;
        var mapper = new ResponsesNativeMapper();
        var result = mapper.Map(request, "StructuredAgent", "openai", new AgentResponse(messages.ToList()));
        var events = await CollectAsync(mapper.MapStreamingAsync(request, "StructuredAgent", "openai",
            ToAsync(messages.Select(message => new AgentResponseUpdate(message.Role, message.Contents)
            { MessageId = message.MessageId }))));
        foreach (var output in new[] { result, Assert.Single(events.OfType<ResponseCompleted>()).Response })
        {
            var reasoning = JsonSerializer.SerializeToElement(Assert.Single(output.Output!), ResponseJson.Default);
            Assert.Equal("rs-encrypted-only", reasoning.GetProperty("id").GetString());
            Assert.Equal("encrypted-only", reasoning.GetProperty("encrypted_content").GetString());
            Assert.Equal("keep-signature", reasoning.GetProperty("signature").GetString());
            Assert.Empty(reasoning.GetProperty("summary").EnumerateArray());
            var replay = JsonSerializer.Deserialize<ResponseRequest>(JsonSerializer.Serialize(new { input = output.Output }, ResponseJson.Default), ResponseJson.Default)!;
            var content = Assert.IsType<TextReasoningContent>(Assert.Single(Assert.Single(replay.ToChatRuntimeRequest().Messages).Contents));
            Assert.Equal("encrypted-only", content.ProtectedData);
        }
    }

    [Fact]
    public async Task Direct_responses_native_multi_agent_items_remain_native_not_client_functions()
    {
        var request = JsonSerializer.Deserialize<ResponseRequest>("""
            {"input":[
              {"type":"multi_agent_call","id":"ma-1","call_id":"ma-call","action":"spawn",
               "agent":{"agent_name":"/root/child"},"arguments":"{\"task\":\"inspect\"}"},
              {"type":"multi_agent_call_output","id":"ma-out","call_id":"ma-call","action":"spawn",
               "agent":{"agent_name":"/root/child"},"output":[{"text":"finished"}]}]}
            """, ResponseJson.Default)!;
        var mapped = request.ToChatRuntimeRequest();
        Assert.True(Assert.IsType<FunctionCallContent>(Assert.Single(mapped.Messages[0].Contents)).InformationalOnly);
        Assert.Equal(ChatRole.Tool, mapped.Messages[1].Role);
        string? captured = null;
        using var http = CreateHttpClient(message =>
        {
            captured = message.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return CreateJsonResponse(LoadFixture(StructuredFixturePath));
        });
        using var client = CreateClient(http, CreateAgent());
        await client.GetResponseAsync(mapped.Messages);
        var original = JsonSerializer.SerializeToElement(request.Input, ResponseJson.Default);
        using var downstream = JsonDocument.Parse(captured!);
        Assert.Equal(original.GetRawText(), downstream.RootElement.GetProperty("input").GetRawText());
    }

    [Fact]
    public async Task Native_responses_stream_preserves_failure_and_cancellation_behavior()
    {
        var mapper = new ResponsesNativeMapper();
        var events = await CollectAsync(mapper.MapStreamingAsync(new ResponseRequest(), "Agent", "openai",
            ToAsync([new AgentResponseUpdate(ChatRole.Assistant, [new ErrorContent("downstream failure")])])));
        Assert.Equal("downstream failure", Assert.Single(events.OfType<ResponseFailed>()).Response.Error?.Message);
        Assert.DoesNotContain(events, part => part is ResponseCompleted);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await CollectAsync(mapper.MapStreamingAsync(new ResponseRequest(), "Agent", "openai",
                CancelledAgentUpdates(cancellation.Token), cancellation.Token)));
    }

    private static async IAsyncEnumerable<AgentResponseUpdate> CancelledAgentUpdates(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        yield break;
    }
}
