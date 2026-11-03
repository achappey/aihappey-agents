using System.Text.Json;
using AgentHappey.Core.Responses;
using AIHappey.Responses;
using AIHappey.Responses.Output;
using AIHappey.Responses.Streaming;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentHappey.Tests;

public sealed class ResponsesCanonicalOutputTests
{
    [Fact]
    public void Constructed_agent_outputs_use_canonical_items_and_nested_content()
    {
        var response = new AgentResponse([
            new ChatMessage(ChatRole.Assistant, [
                new TextContent("Answer"),
                new TextReasoningContent("Think"),
                new FunctionCallContent("call_1", "lookup", new Dictionary<string, object?> { ["query"] = "news" })
            ]) { MessageId = "msg_1" },
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call_1", "plain result")])
        ]);
        var result = new ResponsesNativeMapper().Map(new ResponseRequest(), "Agent", "test", response);
        Assert.Collection(result.Output,
            value =>
            {
                var message = Assert.IsType<ResponseOutputMessage>(value);
                Assert.Equal("msg_1", message.Id);
                Assert.Equal("completed", message.Status);
                Assert.Equal(ResponseRole.Assistant, message.Role);
                Assert.Empty(Assert.IsType<ResponseOutputText>(Assert.Single(message.Content)).Annotations);
            },
            value => Assert.NotNull(Assert.IsType<ResponseOutputReasoningItem>(value).Summary),
            value => Assert.Equal("call_1", Assert.IsType<ResponseOutputItem<ResponseFunctionCallItem>>(value).Item.CallId),
            value => Assert.Equal("plain result", Assert.IsType<ResponseOutputFunctionCallResult>(value).Output.Text));

        foreach (var value in result.Output)
            Assert.NotNull(JsonSerializer.SerializeToElement(value, ResponseJson.Default).Deserialize<ResponseOutputItem>(ResponseJson.Default));
        Assert.Equal("Answer", result.Output.GetAssistantOutputText());
        Assert.Equal(4, result.OutputItems.Count());
        var replay = JsonSerializer.Deserialize<ResponseRequest>(JsonSerializer.Serialize(new { input = result.Output }, ResponseJson.Default), ResponseJson.Default)!;
        Assert.Equal("Think", Assert.Single(replay.ToChatRuntimeRequest().Messages.SelectMany(message => message.Contents)
            .OfType<TextReasoningContent>()).Text);
    }

    [Fact]
    public async Task Agent_streaming_events_and_completion_share_canonical_output()
    {
        var parts = new List<ResponseStreamPart>();
        await foreach (var part in new ResponsesNativeMapper().MapStreamingAsync(new ResponseRequest(), "Agent", "test",
            Updates(new AgentResponseUpdate(ChatRole.Assistant, [new TextContent("Answer")]) { MessageId = "msg_stream" })))
            parts.Add(part);
        var added = Assert.Single(parts.OfType<ResponseOutputItemAdded>());
        var initial = JsonSerializer.SerializeToElement(added.Item, ResponseJson.Default).Deserialize<ResponseOutputMessage>(ResponseJson.Default)!;
        Assert.Equal("in_progress", initial.Status);
        Assert.Empty(initial.Content);
        var done = Assert.Single(parts.OfType<ResponseOutputItemDone>());
        var completed = Assert.Single(parts.OfType<ResponseCompleted>()).Response;
        Assert.IsType<ResponseOutputMessage>(Assert.Single(completed.Output));
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(done.Item, ResponseJson.Default),
            JsonSerializer.SerializeToElement(completed.Output.Single(), ResponseJson.Default)));
    }

    [Fact]
    public void Native_agent_replay_and_generated_file_content_are_preserved()
    {
        const string native = """{"type":"message","id":"native_msg","role":"assistant","status":"completed","content":[{"type":"output_text","text":"Original","annotations":[],"x_text":42}],"x_future":{"v":1}}""";
        var raw = JsonSerializer.Deserialize<JsonElement>(native);
        var response = new AgentResponse([
            new ChatMessage(ChatRole.Assistant, [new TextContent("Original") { RawRepresentation = new { responses_item = raw } }]),
            new ChatMessage(ChatRole.Assistant, [new DataContent(new byte[] { 1, 2, 3 }, "image/png") { Name = "image.png" }]) { MessageId = "file_msg" }
        ]);
        var result = new ResponsesNativeMapper().Map(new ResponseRequest(), "Agent", "test", response);
        Assert.Equal(native, Assert.IsType<JsonElement>(result.Output.First()).GetRawText());
        var fileMessage = Assert.IsType<ResponseOutputMessage>(result.Output.Last());
        var file = Assert.IsType<ResponseUnknownOutputContent>(Assert.Single(fileMessage.Content));
        Assert.Equal("input_image", file.Type);
        Assert.Equal("data:image/png;base64,AQID", file.AdditionalProperties!["image_url"].GetString());
        Assert.Equal("image/png", file.AdditionalProperties["media_type"].GetString());
    }

    private static async IAsyncEnumerable<AgentResponseUpdate> Updates(params AgentResponseUpdate[] updates)
    {
        foreach (var update in updates)
            yield return update;
        await Task.CompletedTask;
    }
}
