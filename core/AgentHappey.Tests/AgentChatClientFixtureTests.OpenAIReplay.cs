using System.Text.Json;
using AgentHappey.Common.Extensions;
using AgentHappey.Core;
using AgentHappey.Core.Responses;
using AIHappey.Responses;
using AIHappey.Vercel.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentHappey.Tests;

public sealed partial class AgentChatClientFixtureTests
{
    private const string ReplayOutput = """
        [
          {"type":"reasoning","id":"rs_replay","status":"completed","encrypted_content":"encrypted-original","summary":[{"type":"summary_text","text":"First summary"},{"type":"summary_text","text":"Second summary"}],"content":[{"type":"reasoning_text","text":"Original reasoning"}]},
          {"type":"message","id":"msg_commentary","role":"assistant","phase":"commentary","status":"completed","content":[{"type":"output_text","text":"Checking.","annotations":[]}]},
          {"type":"message","id":"msg_final","role":"assistant","phase":"final_answer","status":"completed","content":[{"type":"output_text","text":"Done.","annotations":[]}]}
        ]
        """;

    [Theory]
    [InlineData("openai/gpt-fixture")]
    [InlineData("google/gemini-fixture")]
    public async Task OpenAI_stateless_non_streaming_replay_preserves_items_across_three_turns(string modelId)
    {
        var requests = new List<JsonElement>();
        using var http = CreateHttpClient(request =>
        {
            requests.Add(JsonDocument.Parse(request.Content!.ReadAsStringAsync().Result).RootElement.Clone());
            return CreateJsonResponse($$"""{"id":"resp_replay","model":"openai/gpt-fixture","output":{{ReplayOutput}}}""");
        });
        using var client = CreateClient(http, CreateAgent(modelId: modelId));
        var history = CreateUserMessages("First turn").ToList();
        for (var turn = 0; turn < 3; turn++)
        {
            var response = await client.GetResponseAsync(history);
            var native = new ResponsesNativeMapper().Map(new ResponseRequest(), modelId, "transport", new AgentResponse(response.Messages));
            AssertReplayItems(JsonSerializer.SerializeToElement(new { input = native.Output }, ResponseJson.Default));
            history.AddRange(response.Messages);
            history.Add(new ChatMessage(ChatRole.User, "Next turn"));
        }

        AssertReplayItems(requests[1]);
        var repeated = requests[2].GetProperty("input").EnumerateArray()
            .Where(item => item.TryGetProperty("id", out var id) && id.GetString() == "rs_replay").ToList();
        Assert.Equal(2, repeated.Count);
        Assert.All(repeated, AssertOriginalReasoning);
        Assert.All(requests, request =>
        {
            // Agents keep their existing unset store option; the AI gateway's
            // Responses mapping supplies store:false at the upstream boundary.
            Assert.False(request.TryGetProperty("store", out var store) && store.GetBoolean());
            Assert.False(request.TryGetProperty("previous_response_id", out _));
        });
    }

    [Theory]
    [InlineData(false, "openai/gpt-fixture")]
    [InlineData(true, "openai/gpt-fixture")]
    [InlineData(true, "google/gemini-fixture")]
    public async Task OpenAI_stateless_streaming_replay_uses_complete_done_items_and_survives_chat_history(bool includeDeltas, string modelId)
    {
        using var output = JsonDocument.Parse(ReplayOutput);
        var events = new List<string>();
        for (var index = 0; index < output.RootElement.GetArrayLength(); index++)
        {
            var item = output.RootElement[index];
            if (includeDeltas && index == 0)
                events.Add("data: {\"type\":\"response.reasoning_summary_text.delta\",\"item_id\":\"rs_replay\",\"summary_index\":0,\"delta\":\"Display summary delta\"}\n\n");
            if (includeDeltas && index > 0)
                events.Add($$"""data: {"type":"response.output_text.delta","item_id":"{{item.GetProperty("id").GetString()}}","output_index":{{index}},"content_index":0,"delta":"{{(index == 1 ? "Checking." : "Done.")}}"}""" + "\n\n");
            events.Add($$"""data: {"type":"response.output_item.done","output_index":{{index}},"item":{{item.GetRawText()}}}""" + "\n\n");
        }
        // Deliberately omit response.completed: replay must use output_item.done.
        using var http = CreateHttpClient(_ => CreateStreamingResponse(string.Concat(events)));
        using var client = CreateClient(http, CreateAgent(modelId: modelId));
        var updates = await CollectAsync(client.GetStreamingResponseAsync(CreateUserMessages("First turn")));
        string aggregatedRequest = "";
        using (var replayHttp = CreateHttpClient(request =>
        {
            aggregatedRequest = request.Content!.ReadAsStringAsync().Result;
            return CreateJsonResponse("{\"output\":[]}");
        }))
        using (var replayClient = CreateClient(replayHttp, CreateAgent(modelId: modelId)))
            await replayClient.GetResponseAsync(updates.ToChatResponse().Messages);
        AssertReplayItems(JsonDocument.Parse(aggregatedRequest).RootElement);
        var nativeStream = await CollectAsync(new ResponsesNativeMapper().MapStreamingAsync(
            new ResponseRequest(), modelId, "transport", ToAsync(updates.Select(update => new AgentResponseUpdate(update.Role, update.Contents)
            {
                MessageId = update.MessageId,
                AuthorName = "StructuredAgent"
            }))));
        var nativeOutput = Assert.Single(nativeStream.OfType<AIHappey.Responses.Streaming.ResponseCompleted>()).Response.Output;
        AssertReplayItems(JsonSerializer.SerializeToElement(new { input = nativeOutput }, ResponseJson.Default));
        Assert.Equal(new[] { "rs_replay", "msg_commentary", "msg_final" },
            nativeStream.OfType<AIHappey.Responses.Streaming.ResponseOutputItemDone>().Select(part => part.Item.Id));
        Assert.All(nativeStream.OfType<AIHappey.Responses.Streaming.ResponseOutputItemAdded>(),
            added => Assert.Contains(nativeStream.OfType<AIHappey.Responses.Streaming.ResponseOutputItemDone>(), done => done.Item.Id == added.Item.Id));

        var agentUpdates = updates.Select(update => new AgentResponseUpdate(update.Role, update.Contents)
        {
            MessageId = update.MessageId,
            AuthorName = "StructuredAgent"
        });
        var ui = await CollectAsync(new StreamingContentMapper().MapAsync(ToAsync(agentUpdates)));
        Assert.Equal("Checking.Done.", string.Concat(ui.OfType<TextDeltaUIMessageStreamPart>().Select(part => part.Delta)));

        var reasoningEnd = Assert.Single(ui.OfType<ReasoningEndUIPart>());
        var reasoningMetadata = JsonSerializer.SerializeToElement(reasoningEnd.ProviderMetadata, JsonSerializerOptions.Web)
            .GetProperty("StructuredAgent");
        Assert.Equal("rs_replay", reasoningMetadata.GetProperty("id").GetString());
        Assert.Equal(2, reasoningMetadata.GetProperty("summary").GetArrayLength());

        var parts = new List<UIMessagePart>
        {
            new ReasoningUIPart
            {
                Id = reasoningEnd.Id,
                Text = "UI display text must not replace the original summaries",
                ProviderMetadata = new() { ["StructuredAgent"] = reasoningMetadata }
            }
        };
        foreach (var end in ui.OfType<TextEndUIMessageStreamPart>())
            parts.Add(new TextUIPart
            {
                Text = string.Concat(ui.OfType<TextDeltaUIMessageStreamPart>().Where(delta => delta.Id == end.Id).Select(delta => delta.Delta)),
                ProviderMetadata = end.ProviderMetadata
            });
        var history = new[] { new UIMessage { Id = "ui-transport-id", Role = Role.assistant, Parts = parts } };
        var serialized = JsonSerializer.Serialize(history, JsonSerializerOptions.Web);
        var restored = JsonSerializer.Deserialize<UIMessage[]>(serialized, JsonSerializerOptions.Web)!;
        var request = JsonDocument.Parse(await CaptureRequestBodyAsync(restored, ["StructuredAgent"])).RootElement;
        AssertReplayItems(request);
        // A second serialized chat turn must preserve the same native items again.
        AssertReplayItems(JsonDocument.Parse(await CaptureRequestBodyAsync(restored, ["StructuredAgent"])).RootElement);

        // Even when both agents are active, only the producing agent can replay state.
        var shared = restored.ToMessages(["StructuredAgent", "OtherAgent"]).ToList();
        var other = CreateAgent();
        other.Name = "OtherAgent";
        string otherRequest = "";
        using var otherHttp = CreateHttpClient(requestMessage =>
        {
            otherRequest = requestMessage.Content!.ReadAsStringAsync().Result;
            return CreateJsonResponse("{\"output\":[]}");
        });
        using var otherClient = CreateClient(otherHttp, other);
        await otherClient.GetResponseAsync(shared);
        Assert.DoesNotContain("encrypted-original", otherRequest);
        Assert.DoesNotContain("final_answer", otherRequest);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Responses_replay_keeps_interleaved_output_order_with_tool_calls(bool streaming)
    {
        using var original = JsonDocument.Parse(ReplayOutput);
        var output = new[]
        {
            original.RootElement[1].Clone(),
            original.RootElement[0].Clone(),
            JsonSerializer.SerializeToElement(new { type = "function_call", id = "fc_order", call_id = "call_order", name = "lookup", arguments = "{}", status = "completed" }),
            original.RootElement[2].Clone()
        };
        var body = streaming
            ? string.Concat(output.Select((item, index) => $$"""data: {"type":"response.output_item.done","output_index":{{index}},"item":{{item.GetRawText()}}}""" + "\n\n"))
            : JsonSerializer.Serialize(new { output }, ResponseJson.Default);
        string replay = "";
        var turn = 0;
        using var http = CreateHttpClient(request =>
        {
            if (turn++ == 0)
                return streaming ? CreateStreamingResponse(body) : CreateJsonResponse(body);
            replay = request.Content!.ReadAsStringAsync().Result;
            return CreateJsonResponse("{\"output\":[]}");
        });
        using var client = CreateClient(http, CreateAgent());
        var history = streaming
            ? (await CollectAsync(client.GetStreamingResponseAsync(CreateUserMessages("First")))).ToChatResponse().Messages.ToList()
            : (await client.GetResponseAsync(CreateUserMessages("First"))).Messages.ToList();
        history.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call_order", "result")]));
        history.Add(new ChatMessage(ChatRole.User, "Next"));
        await client.GetResponseAsync(history);
        var items = JsonDocument.Parse(replay).RootElement.GetProperty("input").EnumerateArray().ToList();
        Assert.Equal(new[] { "msg_commentary", "rs_replay", "fc_order", "msg_final" },
            items.Take(4).Select(item => item.GetProperty("id").GetString()));
        Assert.Equal("function_call_output", items[4].GetProperty("type").GetString());
        AssertOriginalReasoning(items[1]);
    }

    [Fact]
    public async Task Responses_legacy_agent_keyed_reasoning_metadata_retains_original_id_and_summary_array()
    {
        var history = new[]
        {
            new UIMessage
            {
                Id = "ui-legacy",
                Role = Role.assistant,
                Parts = [new ReasoningUIPart
                {
                    Text = "Different display summary",
                    ProviderMetadata = new() { ["StructuredAgent"] = JsonSerializer.SerializeToElement(new
                    {
                        id = "rs_original",
                        encrypted_content = "original-encrypted",
                        summary = new[] { new { type = "summary_text", text = "Original summary" } }
                    }) }
                }]
            }
        };
        var request = JsonDocument.Parse(await CaptureRequestBodyAsync(history, ["StructuredAgent"])).RootElement;
        var reasoning = Assert.Single(request.GetProperty("input").EnumerateArray());
        Assert.Equal("rs_original", reasoning.GetProperty("id").GetString());
        Assert.Equal("Original summary", reasoning.GetProperty("summary")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task OpenAI_legacy_encrypted_reasoning_without_summary_does_not_invent_empty_summary_text()
    {
        string captured = "";
        using var captureHttp = CreateHttpClient(request =>
        {
            captured = request.Content!.ReadAsStringAsync().Result;
            return CreateJsonResponse("{\"output\":[]}");
        });
        using var capture = CreateClient(captureHttp, CreateAgent());
        await capture.GetResponseAsync([new ChatMessage(ChatRole.Assistant,
            [new TextReasoningContent("") { ProtectedData = "legacy-encrypted" }])]);
        var reasoning = JsonDocument.Parse(captured).RootElement.GetProperty("input")[0];
        Assert.Empty(reasoning.GetProperty("summary").EnumerateArray());
    }

    private static void AssertReplayItems(JsonElement request)
    {
        var items = request.GetProperty("input").EnumerateArray()
            .Where(item => item.GetProperty("type").GetString() != "message"
                || item.GetProperty("role").GetString() == "assistant").ToList();
        Assert.Equal(3, items.Count);
        AssertOriginalReasoning(items[0]);
        Assert.Equal("msg_commentary", items[1].GetProperty("id").GetString());
        Assert.Equal("commentary", items[1].GetProperty("phase").GetString());
        Assert.Equal("Checking.", items[1].GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("msg_final", items[2].GetProperty("id").GetString());
        Assert.Equal("final_answer", items[2].GetProperty("phase").GetString());
        Assert.Equal("Done.", items[2].GetProperty("content")[0].GetProperty("text").GetString());
    }

    private static void AssertOriginalReasoning(JsonElement item)
    {
        Assert.Equal("rs_replay", item.GetProperty("id").GetString());
        Assert.Equal("encrypted-original", item.GetProperty("encrypted_content").GetString());
        Assert.Equal("completed", item.GetProperty("status").GetString());
        Assert.Equal("Original reasoning", item.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal(new[] { "First summary", "Second summary" },
            item.GetProperty("summary").EnumerateArray().Select(part => part.GetProperty("text").GetString()));
    }
}
