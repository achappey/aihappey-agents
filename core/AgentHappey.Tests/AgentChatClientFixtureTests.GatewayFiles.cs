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
    [Theory]
    [InlineData("openai", false)]
    [InlineData("openai", true)]
    [InlineData("anthropic", false)]
    [InlineData("anthropic", true)]
    public async Task Gateway_files_survive_agent_chat_responses_and_stateless_replay(string provider, bool streaming)
    {
        var item = GatewayFileItem(provider);
        var requests = new List<string>();
        using var http = CreateHttpClient(request =>
        {
            requests.Add(request.Content!.ReadAsStringAsync().Result);
            return streaming ? CreateStreamingResponse(GatewayFileStream(item))
                : CreateJsonResponse(JsonSerializer.Serialize(new { id = "resp_files", output = new[] { item } }));
        });
        using var client = CreateClient(http, CreateAgent(modelId: provider + "/fixture"));
        List<ChatMessage> messages;
        List<AgentResponseUpdate> updates;
        if (streaming)
        {
            var chunks = await CollectAsync(client.GetStreamingResponseAsync(CreateUserMessages("Create a document")));
            messages = chunks.ToChatResponse().Messages.ToList();
            updates = chunks.Select(chunk => new AgentResponseUpdate(chunk.Role, chunk.Contents)
            {
                MessageId = chunk.MessageId, AuthorName = "StructuredAgent"
            }).ToList();
        }
        else
        {
            messages = (await client.GetResponseAsync(CreateUserMessages("Create a document"))).Messages.ToList();
            updates = messages.Select(message => new AgentResponseUpdate(message.Role, message.Contents)
            {
                MessageId = message.MessageId, AuthorName = "StructuredAgent"
            }).ToList();
        }

        var ui = await CollectAsync(new StreamingContentMapper().MapAsync(ToAsync(updates)));
        var call = Assert.Single(ui.OfType<ToolCallPart>());
        Assert.Equal("download_file", call.ToolName);
        Assert.True(call.ProviderExecuted);
        var result = Assert.Single(ui.OfType<ToolOutputAvailablePart>());
        Assert.True(result.ProviderExecuted);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(item.GetProperty("output").GetString()!),
            JsonSerializer.SerializeToElement(result.Output)));
        var file = Assert.Single(ui.OfType<FileUIPart>());
        Assert.Equal("data:application/vnd.openxmlformats-officedocument.wordprocessingml.document;base64,AAEC/w==", file.Url);
        Assert.Equal("report.docx", file.Filename);

        var mapped = new ResponsesNativeMapper().Map(new ResponseRequest(), "StructuredAgent", "transport", new AgentResponse(messages));
        Assert.True(JsonElement.DeepEquals(item, JsonSerializer.SerializeToElement(Assert.Single(mapped.Output), ResponseJson.Default)));
        var events = await CollectAsync(new ResponsesNativeMapper().MapStreamingAsync(new ResponseRequest(),
            "StructuredAgent", "transport", ToAsync(updates)));
        var done = Assert.Single(events.OfType<ResponseOutputItemDone>());
        Assert.True(JsonElement.DeepEquals(item, JsonSerializer.SerializeToElement(done.Item, ResponseJson.Default)));
        Assert.Single(events.OfType<ResponseOutputItemAdded>());
        Assert.True(JsonElement.DeepEquals(item, JsonSerializer.SerializeToElement(
            Assert.Single(Assert.Single(events.OfType<ResponseCompleted>()).Response.Output), ResponseJson.Default)));
        Assert.Equal(Enumerable.Range(1, events.Count), events.Select(part =>
            JsonSerializer.SerializeToElement(part, ResponseJson.Default).GetProperty("sequence_number").GetInt32()));

        // Neither live framework history nor serialized chat/Responses history may
        // turn these output-only artifacts into model inputs or executable tools.
        await client.GetResponseAsync(messages.Concat(CreateUserMessages("Continue")));
        var history = new[] { new UIMessage { Id = "ui_files", Role = Role.assistant, Parts = ui } };
        var restored = JsonSerializer.Deserialize<UIMessage[]>(JsonSerializer.Serialize(history, JsonSerializerOptions.Web), JsonSerializerOptions.Web)!;
        await client.GetResponseAsync(restored.ToMessages(["StructuredAgent"]).Concat(CreateUserMessages("Continue")));
        var replay = JsonSerializer.Deserialize<ResponseRequest>(JsonSerializer.Serialize(new { input = mapped.Output }, ResponseJson.Default), ResponseJson.Default)!;
        await client.GetResponseAsync(replay.ToChatRuntimeRequest().Messages.Concat(CreateUserMessages("Continue")));
        Assert.All(requests.Skip(1), body =>
        {
            Assert.DoesNotContain("download_file", body);
            Assert.DoesNotContain("AAEC/w==", body);
            Assert.DoesNotContain("call_gateway", body);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Gateway_terminal_snapshot_recovers_missing_done_events_without_duplicate_files(bool terminalOnly)
    {
        var item = GatewayFileItem("openai");
        using var http = CreateHttpClient(_ => CreateStreamingResponse(GatewayFileStream(item, terminalOnly)));
        using var client = CreateClient(http, CreateAgent());
        var chunks = await CollectAsync(client.GetStreamingResponseAsync(CreateUserMessages("Create a file")));
        var updates = chunks.Select(chunk => new AgentResponseUpdate(chunk.Role, chunk.Contents) { AuthorName = "StructuredAgent" });
        var ui = await CollectAsync(new StreamingContentMapper().MapAsync(ToAsync(updates)));
        Assert.Single(ui.OfType<ToolCallPart>());
        Assert.Single(ui.OfType<ToolOutputAvailablePart>());
        Assert.Single(ui.OfType<FileUIPart>());
    }

    [Theory]
    [InlineData("image/png", "image.png", false)]
    [InlineData("text/plain", "notes.txt", false)]
    [InlineData("application/json", "result.json", false)]
    [InlineData("application/octet-stream", "output.bin", false)]
    [InlineData("application/octet-stream", "failed.bin", true)]
    public async Task Gateway_download_types_and_errors_preserve_payloads_through_the_agent_framework(string mediaType, string filename, bool error)
    {
        var item = GatewayFileItem("openai", mediaType, filename, error);
        using var http = CreateHttpClient(_ => CreateStreamingResponse(GatewayFileStream(item)));
        using var client = CreateClient(http, CreateAgent());
        var agent = new ChatClientAgent(client, instructions: "Fixture test instructions", name: "StructuredAgent");
        var ui = await CollectAsync(new StreamingContentMapper().MapAsync(agent.RunStreamingAsync(CreateUserMessages("Create a file"))));
        Assert.Single(ui.OfType<ToolCallPart>());
        var output = Assert.Single(ui.OfType<ToolOutputAvailablePart>());
        var payload = JsonSerializer.SerializeToElement(output.Output);
        Assert.Equal(error, payload.GetProperty("isError").GetBoolean());
        Assert.Equal(42, payload.GetProperty("future_result").GetInt32());
        Assert.True(payload.GetProperty("_meta").GetProperty("future/meta").GetProperty("retained").GetBoolean());
        if (error)
            Assert.Empty(ui.OfType<FileUIPart>());
        else
        {
            var file = Assert.Single(ui.OfType<FileUIPart>());
            Assert.Equal(mediaType, file.MediaType);
            Assert.Equal(filename, file.Filename);
            Assert.Equal(new byte[] { 0, 1, 2, 255 }, Convert.FromBase64String(file.Url.Split(',')[1]));
        }
    }

    [Fact]
    public async Task Gateway_multiple_files_keep_distinct_ids_indexes_and_terminal_items()
    {
        var items = new[] { GatewayFileItem("openai"), GatewayFileItem("anthropic", "text/plain", "notes.txt", id: "call_second") };
        using var http = CreateHttpClient(_ => CreateJsonResponse(JsonSerializer.Serialize(new { output = items })));
        using var client = CreateClient(http, CreateAgent());
        var response = await client.GetResponseAsync(CreateUserMessages("Create files"));
        var updates = response.Messages.Select(message => new AgentResponseUpdate(message.Role, message.Contents) { AuthorName = "StructuredAgent" });
        var ui = await CollectAsync(new StreamingContentMapper().MapAsync(ToAsync(updates)));
        Assert.Equal(2, ui.OfType<FileUIPart>().Count());
        var events = await CollectAsync(new ResponsesNativeMapper().MapStreamingAsync(new ResponseRequest(), "StructuredAgent", "transport", ToAsync(updates)));
        Assert.Equal(new[] { 0, 1 }, events.OfType<ResponseOutputItemDone>().Select(part => part.OutputIndex));
        Assert.Equal(items.Select(item => item.GetProperty("id").GetString()), events.OfType<ResponseOutputItemDone>().Select(part => part.Item.Id));
        Assert.Equal(2, Assert.Single(events.OfType<ResponseCompleted>()).Response.Output.Count());
    }

    [Fact]
    public async Task Gateway_transport_identity_does_not_capture_ordinary_mcp_or_same_name_client_tools()
    {
        var ordinary = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(GatewayFileItem("openai"))!;
        ordinary["server_label"] = JsonSerializer.SerializeToElement("ordinary-server");
        using var http = CreateHttpClient(_ => CreateStreamingResponse(GatewayFileStream(JsonSerializer.SerializeToElement(ordinary))));
        using var client = CreateClient(http, CreateAgent());
        var chunks = await CollectAsync(client.GetStreamingResponseAsync(CreateUserMessages("Run MCP")));
        Assert.Empty(chunks.SelectMany(chunk => chunk.Contents).OfType<FunctionCallContent>());

        string requestBody = "";
        using var replayHttp = CreateHttpClient(request =>
        {
            requestBody = request.Content!.ReadAsStringAsync().Result;
            return CreateJsonResponse("{\"output\":[]}");
        });
        using var replayClient = CreateClient(replayHttp, CreateAgent());
        await replayClient.GetResponseAsync([
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("client_download", "download_file", new Dictionary<string, object?>())]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("client_download", "ordinary client result")]),
            new ChatMessage(ChatRole.User, [new DataContent(new byte[] { 0, 1, 2, 255 }, "application/octet-stream") { Name = "user.bin" }])
        ]);
        Assert.Contains("download_file", requestBody);
        Assert.Contains("ordinary client result", requestBody);
        Assert.Contains("AAEC/w==", requestBody);
        Assert.Contains("user.bin", requestBody);
    }

    private static JsonElement GatewayFileItem(string provider, string mediaType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        string filename = "report.docx", bool error = false, string id = "call_gateway")
    {
        var payload = new Dictionary<string, object?>
        {
            ["content"] = new object[] { new { type = "resource", resource = new { uri = "https://provider.example/files/file_1", mimeType = mediaType, blob = "AAEC/w==" } } },
            ["structuredContent"] = new { file_id = "file_1", filename, media_type = mediaType,
                url = "https://provider.example/files/file_1", data_url = $"data:{mediaType};base64,AAEC/w==" },
            ["isError"] = error,
            ["_meta"] = new Dictionary<string, object?>
            {
                ["aihappey/gateway"] = new { provider, title = "Downloaded file", providerMetadata = new Dictionary<string, object?>
                {
                    [provider] = new { download_tool = true, file_id = "file_1", filename }
                } },
                ["future/meta"] = new { retained = true }
            },
            ["future_result"] = 42
        };
        return JsonSerializer.SerializeToElement(new { type = "mcp_call", id, name = "download_file", server_label = "aihappey",
            arguments = "{\"file_id\":\"file_1\"}", output = JsonSerializer.Serialize(payload), status = error ? "failed" : "completed" });
    }

    private static string GatewayFileStream(JsonElement item, bool terminalOnly = false)
    {
        var start = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(item)!;
        start["status"] = JsonSerializer.SerializeToElement("in_progress");
        start["output"] = JsonSerializer.SerializeToElement<object?>(null);
        var events = new List<object>();
        if (!terminalOnly)
        {
            events.Add(new { type = "response.output_item.added", output_index = 0, item = start });
            events.Add(new { type = "response.mcp_call_arguments.done", output_index = 0, item_id = item.GetProperty("id").GetString(), arguments = "{}" });
            // Repeated done events must not duplicate tool/file output.
            events.Add(new { type = "response.output_item.done", output_index = 0, item });
            events.Add(new { type = "response.output_item.done", output_index = 0, item });
        }
        events.Add(new { type = "response.completed", response = new { id = "resp_files", status = "completed", output = new[] { item } } });
        return string.Concat(events.Select((value, index) =>
        {
            var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value))!;
            fields["sequence_number"] = JsonSerializer.SerializeToElement(index);
            return "data: " + JsonSerializer.Serialize(fields) + "\n\n";
        })) + "data: [DONE]\n\n";
    }
}
