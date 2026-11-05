using System.Text.Json;
using AgentHappey.Common.Extensions;
using AgentHappey.Core;
using AgentHappey.Core.Responses;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using AIHappey.Vercel.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentHappey.Tests;

public sealed partial class AgentChatClientFixtureTests
{
    public static IEnumerable<object[]> WebSearchCases()
    {
        foreach (var provider in new[] { "openai", "anthropic" })
        foreach (var streaming in new[] { false, true })
        foreach (var action in new[] { "search", "open_page", "find" })
            yield return [provider, streaming, action];
    }

    [Theory]
    [MemberData(nameof(WebSearchCases))]
    public async Task Web_search_survives_chat_native_responses_and_fresh_stateless_replay(string provider, bool streaming, string action)
    {
        var item = WebSearchItem(provider, action);
        using var http = CreateHttpClient(_ => streaming
            ? CreateStreamingResponse(WebSearchStream(item))
            : CreateJsonResponse(JsonSerializer.Serialize(new { output = new[] { item } })));
        using var client = CreateClient(http, CreateAgent(modelId: provider + "/fixture"));
        List<ChatMessage> messages;
        List<AgentResponseUpdate> updates;
        if (streaming)
        {
            var chunks = await CollectAsync(client.GetStreamingResponseAsync(CreateUserMessages("Search the web")));
            messages = chunks.ToChatResponse().Messages.ToList();
            updates = chunks.Select(chunk => new AgentResponseUpdate(chunk.Role, chunk.Contents)
            { MessageId = chunk.MessageId, AuthorName = "StructuredAgent" }).ToList();
        }
        else
        {
            messages = (await client.GetResponseAsync(CreateUserMessages("Search the web"))).Messages.ToList();
            updates = messages.Select(message => new AgentResponseUpdate(message.Role, message.Contents)
            { MessageId = message.MessageId, AuthorName = "StructuredAgent" }).ToList();
        }

        var frameworkCall = Assert.Single(messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>());
        Assert.True(frameworkCall.InformationalOnly);
        Assert.Equal("web_search", frameworkCall.Name);
        Assert.Equal(item.GetProperty("id").GetString(), frameworkCall.CallId);

        var ui = await CollectAsync(new StreamingContentMapper().MapAsync(ToAsync(updates), requestToolApproval: true));
        var call = Assert.Single(ui.OfType<ToolCallPart>());
        var result = Assert.Single(ui.OfType<ToolOutputAvailablePart>());
        Assert.Equal(frameworkCall.CallId, call.ToolCallId);
        Assert.Equal(call.ToolCallId, result.ToolCallId);
        Assert.True(call.ProviderExecuted);
        Assert.True(result.ProviderExecuted);
        Assert.False(result.Preliminary);
        Assert.Empty(ui.OfType<ToolApprovalRequestUIPart>());
        Assert.True(JsonElement.DeepEquals(item.GetProperty("action"), JsonSerializer.SerializeToElement(call.Input)));
        foreach (var metadata in new[] { call.ProviderMetadata, result.ProviderMetadata })
        {
            var json = JsonSerializer.SerializeToElement(metadata);
            Assert.True(JsonElement.DeepEquals(item, json.GetProperty("StructuredAgent").GetProperty("responses_item")));
            if (provider == "anthropic")
                Assert.True(JsonElement.DeepEquals(item.GetProperty("provider_metadata").GetProperty(provider), json.GetProperty(provider)));
            Assert.False(json.TryGetProperty("openai", out _));
        }

        var mapper = new ResponsesNativeMapper();
        var native = mapper.Map(new ResponseRequest(), "StructuredAgent", "transport", new AgentResponse(messages));
        Assert.True(JsonElement.DeepEquals(item, JsonSerializer.SerializeToElement(Assert.Single(native.Output), ResponseJson.Default)));
        var nativeStream = await CollectAsync(mapper.MapStreamingAsync(new ResponseRequest(), "StructuredAgent", "transport", ToAsync(updates)));
        Assert.Single(nativeStream.OfType<ResponseOutputItemAdded>());
        Assert.True(JsonElement.DeepEquals(item, JsonSerializer.SerializeToElement(Assert.Single(nativeStream.OfType<ResponseOutputItemDone>()).Item, ResponseJson.Default)));
        Assert.Empty(nativeStream.OfType<ResponseFunctionCallArgumentsDone>());
        Assert.True(JsonElement.DeepEquals(item, JsonSerializer.SerializeToElement(
            Assert.Single(Assert.Single(nativeStream.OfType<ResponseCompleted>()).Response.Output), ResponseJson.Default)));

        AssertWebSearchReplay(item, await CaptureWebSearchReplay(messages, provider));
        var nativeReplay = JsonSerializer.Deserialize<ResponseRequest>(JsonSerializer.Serialize(new { input = native.Output }, ResponseJson.Default), ResponseJson.Default)!;
        AssertWebSearchReplay(item, await CaptureWebSearchReplay(nativeReplay.ToChatRuntimeRequest().Messages, provider));

        // Exercise both separate stream parts and the consolidated shape stored
        // by the UI SDK, after a real serialize/deserialize boundary and with a
        // brand-new backend client for every turn.
        foreach (var consolidated in new[] { false, true })
        {
            List<UIMessagePart> parts = consolidated
                ? [new ToolInvocationPart
                {
                    Type = "tool-web_search", ToolCallId = call.ToolCallId, State = "output-available",
                    Input = call.Input, Output = result.Output, ProviderExecuted = true,
                    CallProviderMetadata = call.ProviderMetadata, ResultProviderMetadata = result.ProviderMetadata
                }]
                : [call, result];
            var history = new[] { new UIMessage { Id = "ui-search", Role = Role.assistant,
                Metadata = new() { ["model"] = "StructuredAgent" }, Parts = parts } };
            for (var turn = 0; turn < 2; turn++)
            {
                history = JsonSerializer.Deserialize<UIMessage[]>(JsonSerializer.Serialize(history, JsonSerializerOptions.Web), JsonSerializerOptions.Web)!;
                var restored = history.ToMessages(["StructuredAgent", "OtherAgent"]).ToList();
                Assert.Empty(restored.SelectMany(message => message.Contents).OfType<FunctionResultContent>());
                AssertWebSearchReplay(item, await CaptureWebSearchReplay(restored, provider));
                var other = await CaptureWebSearchReplay(restored, provider, "OtherAgent");
                Assert.Empty(other.GetProperty("input").EnumerateArray());
            }
        }
    }

    [Theory]
    [InlineData("openai", false)]
    [InlineData("openai", true)]
    [InlineData("anthropic", false)]
    [InlineData("anthropic", true)]
    public async Task Web_search_terminal_fallback_and_repeated_done_events_emit_one_lifecycle_through_agent_framework(string provider, bool terminalOnly)
    {
        var item = WebSearchItem(provider);
        var requestCount = 0;
        using var http = CreateHttpClient(_ =>
        {
            requestCount++;
            return CreateStreamingResponse(WebSearchStream(item, terminalOnly));
        });
        using var client = CreateClient(http, CreateAgent(modelId: provider + "/fixture"));
        var agent = new ChatClientAgent(client, instructions: "Fixture", name: "StructuredAgent");
        var ui = await CollectAsync(new StreamingContentMapper().MapAsync(agent.RunStreamingAsync(CreateUserMessages("Search"))));
        Assert.Single(ui.OfType<ToolCallPart>());
        Assert.Single(ui.OfType<ToolOutputAvailablePart>());
        Assert.Equal(1, requestCount); // Provider-managed search must not enter the local tool loop.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Web_search_replay_deduplicates_native_items_and_preserves_interleaved_order(bool streaming)
    {
        var first = WebSearchItem("openai");
        var secondFields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(WebSearchItem("openai", "find"))!;
        secondFields["id"] = JsonSerializer.SerializeToElement("ws_second");
        var second = JsonSerializer.SerializeToElement(secondFields);
        var message = JsonSerializer.SerializeToElement(new { type = "message", id = "msg_between", role = "assistant",
            status = "completed", content = new[] { new { type = "output_text", text = "Between searches", annotations = Array.Empty<object>() } } });
        var output = new[] { first, message, second };
        using var http = CreateHttpClient(_ => streaming
            ? CreateStreamingResponse(string.Concat(output.Select((item, index) => "data: " + JsonSerializer.Serialize(new
                { type = "response.output_item.done", output_index = index, item }) + "\n\n")))
            : CreateJsonResponse(JsonSerializer.Serialize(new { output })));
        using var client = CreateClient(http, CreateAgent());
        var messages = streaming
            ? (await CollectAsync(client.GetStreamingResponseAsync(CreateUserMessages("Search")))).ToChatResponse().Messages
            : (await client.GetResponseAsync(CreateUserMessages("Search"))).Messages;
        var replay = await CaptureWebSearchReplay(messages.Concat([
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("ws_search", "web_search")
            { InformationalOnly = true, RawRepresentation = new { responses_item = first } }])]), "openai");
        var items = replay.GetProperty("input").EnumerateArray().ToList();
        Assert.Equal(new[] { "ws_search", "msg_between", "ws_second" }, items.Select(item => item.GetProperty("id").GetString()));
        Assert.True(JsonElement.DeepEquals(first, items[0]));
        Assert.True(JsonElement.DeepEquals(second, items[2]));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Web_search_replay_uses_metadata_owner_when_message_owner_is_missing(bool consolidated, bool resultOnlyMetadata)
    {
        var item = WebSearchItem("anthropic");
        var metadata = new Dictionary<string, Dictionary<string, object>?>
        {
            ["StructuredAgent"] = new() { ["responses_item"] = item, ["agent_name"] = "StructuredAgent" }
        };
        List<UIMessagePart> parts = consolidated
            ? [new ToolInvocationPart
            {
                Type = "tool-web_search", ToolCallId = "srvtoolu_search", State = "output-available",
                Input = new { }, Output = new { }, ProviderExecuted = true,
                CallProviderMetadata = resultOnlyMetadata ? null : metadata, ResultProviderMetadata = metadata
            }]
            : [new ToolCallPart { ToolCallId = "srvtoolu_search", ToolName = "web_search", Input = new { },
                ProviderExecuted = true, ProviderMetadata = resultOnlyMetadata ? null : metadata },
                new ToolOutputAvailablePart { ToolCallId = "srvtoolu_search", Output = new { },
                    ProviderExecuted = true, ProviderMetadata = metadata }];
        var history = JsonSerializer.Deserialize<UIMessage[]>(JsonSerializer.Serialize(new[]
        {
            new UIMessage { Id = "without-owner", Role = Role.assistant, Parts = parts }
        }, JsonSerializerOptions.Web), JsonSerializerOptions.Web)!;
        AssertWebSearchReplay(item, await CaptureWebSearchReplay(history.ToMessages(["StructuredAgent"]), "anthropic"));
        AssertWebSearchReplay(item, await CaptureWebSearchReplay(history.ToMessages(["StructuredAgent", "OtherAgent"]), "anthropic"));
        Assert.Empty((await CaptureWebSearchReplay(history.ToMessages(["OtherAgent"]), "anthropic", "OtherAgent"))
            .GetProperty("input").EnumerateArray());
        Assert.Empty((await CaptureWebSearchReplay(history.ToMessages(["StructuredAgent", "OtherAgent"]), "anthropic", "OtherAgent"))
            .GetProperty("input").EnumerateArray());
    }

    [Theory]
    [InlineData("wrong-id")]
    [InlineData("missing-id")]
    [InlineData("missing-action")]
    [InlineData("in-progress")]
    public async Task Web_search_invalid_native_history_never_becomes_an_executable_function(string variant)
    {
        var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(WebSearchItem("openai"))!;
        switch (variant)
        {
            case "wrong-id": fields["id"] = JsonSerializer.SerializeToElement("another-search"); break;
            case "missing-id": fields.Remove("id"); break;
            case "missing-action": fields.Remove("action"); break;
            case "in-progress": fields["status"] = JsonSerializer.SerializeToElement("in_progress"); break;
        }
        var item = JsonSerializer.SerializeToElement(fields);
        var rawHistory = new[] { new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("ws_search", "web_search") { InformationalOnly = true, RawRepresentation = new { responses_item = item } }]) };
        Assert.Empty((await CaptureWebSearchReplay(rawHistory, "openai")).GetProperty("input").EnumerateArray());
        var uiHistory = new[] { new UIMessage { Id = "invalid", Role = Role.assistant, Parts = [new ToolInvocationPart
        {
            Type = "tool-web_search", ToolCallId = "ws_search", State = "output-available", ProviderExecuted = true,
            Input = new { }, Output = new { }, CallProviderMetadata = new()
            { ["StructuredAgent"] = new() { ["responses_item"] = item, ["agent_name"] = "StructuredAgent" } }
        }] } };
        Assert.Empty((await CaptureWebSearchReplay(uiHistory.ToMessages(["StructuredAgent"]), "openai")).GetProperty("input").EnumerateArray());
    }

    [Fact]
    public async Task Web_search_native_mapper_does_not_emit_a_synthetic_result_when_result_raw_metadata_is_absent()
    {
        var item = WebSearchItem("openai");
        var messages = new[]
        {
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("ws_search", "web_search")
            { InformationalOnly = true, RawRepresentation = new { responses_item = item } }]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("ws_search", new { displayOnly = true })])
        };
        var output = new ResponsesNativeMapper().Map(new ResponseRequest(), "StructuredAgent", "transport", new AgentResponse(messages)).Output;
        Assert.True(JsonElement.DeepEquals(item, JsonSerializer.SerializeToElement(Assert.Single(output), ResponseJson.Default)));
        AssertWebSearchReplay(item, await CaptureWebSearchReplay(messages, "openai"));
    }

    private static JsonElement WebSearchItem(string provider, string action = "search")
    {
        object details = action switch
        {
            "open_page" => new { type = action, url = "https://news.example/article", future = 42 },
            "find" => new { type = action, url = "https://news.example/article", pattern = "latest", future = 42 },
            _ => new { type = action, query = "latest news", queries = new[] { "latest news", "more news" },
                sources = new[] { new { type = "url", url = "https://news.example/article", title = "News" } }, future = 42 }
        };
        var fields = new Dictionary<string, object?>
        {
            ["type"] = "web_search_call", ["id"] = provider == "anthropic" ? "srvtoolu_search" : "ws_search",
            ["status"] = "completed", ["action"] = details, ["future_item"] = new { retained = true }
        };
        if (provider == "anthropic")
            fields["provider_metadata"] = new { anthropic = new { type = "web_search_tool_result", id = "srvtoolu_search",
                name = "web_search", tool_use_id = "srvtoolu_search", caller = new { type = "direct" }, provider_executed = true,
                input = new { query = "latest news" }, output = new { structuredContent = new { results = new[] {
                    new { type = "web_search_result", url = "https://news.example/article", title = "News", encrypted_content = "opaque-result" } } } } } };
        return JsonSerializer.SerializeToElement(fields);
    }

    private static string WebSearchStream(JsonElement item, bool terminalOnly = false)
    {
        var events = new List<object>();
        if (!terminalOnly)
        {
            events.Add(new { type = "response.output_item.added", output_index = 0, item });
            events.Add(new { type = "response.output_item.done", output_index = 0, item });
            events.Add(new { type = "response.output_item.done", output_index = 0, item });
        }
        events.Add(new { type = "response.completed", response = new { id = "resp_search", status = "completed", output = new[] { item } } });
        return string.Concat(events.Select(value => "data: " + JsonSerializer.Serialize(value) + "\n\n")) + "data: [DONE]\n\n";
    }

    private static async Task<JsonElement> CaptureWebSearchReplay(IEnumerable<ChatMessage> messages, string provider, string owner = "StructuredAgent")
    {
        string body = "";
        using var http = CreateHttpClient(request =>
        {
            body = request.Content!.ReadAsStringAsync().Result;
            return CreateJsonResponse("{\"output\":[]}");
        });
        var agent = CreateAgent(modelId: provider + "/fixture");
        agent.Name = owner;
        using var client = CreateClient(http, agent);
        await client.GetResponseAsync(messages);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static void AssertWebSearchReplay(JsonElement expected, JsonElement request)
    {
        var item = Assert.Single(request.GetProperty("input").EnumerateArray());
        Assert.True(JsonElement.DeepEquals(expected, item), item.GetRawText());
        Assert.Equal("web_search_call", item.GetProperty("type").GetString());
        Assert.False(request.TryGetProperty("previous_response_id", out _));
    }
}
