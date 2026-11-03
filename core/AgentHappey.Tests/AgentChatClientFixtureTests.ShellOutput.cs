using System.Text.Json;
using AgentHappey.Core;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using AIHappey.Vercel.Models;
using Microsoft.Agents.AI;

namespace AgentHappey.Tests;

public sealed partial class AgentChatClientFixtureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Typed_shell_output_chunks_emit_preliminary_updates_before_the_authoritative_final_output(bool completedChunk)
    {
        const string call = """{"type":"shell_call","id":"sh_1","call_id":"call_1","status":"completed","action":{"commands":["echo hello"]}}""";
        const string output = """{"type":"shell_call_output","id":"sho_1","call_id":"call_1","status":"completed","output":[{"stdout":"complete\n","stderr":"","outcome":{"type":"exit","exit_code":0}}]}""";
        var chunk = completedChunk
            ? """{"type":"response.shell_call_output_content.done","sequence_number":3,"output_index":1,"item_id":"sho_1","command_index":0,"output":[{"stdout":"progress\n","stderr":"","outcome":{"type":"exit","exit_code":0}}]}"""
            : """{"type":"response.shell_call_output_content.delta","sequence_number":3,"output_index":1,"item_id":"sho_1","command_index":0,"delta":{"stdout":"progress\n"}}""";
        var parsed = JsonSerializer.Deserialize<ResponseStreamPart>(chunk, ResponseJson.Default);
        if (completedChunk)
            Assert.IsType<ResponseShellCallOutputContentDone>(parsed);
        else
            Assert.IsType<ResponseShellCallOutputContentDelta>(parsed);

        var stream = "data: {\"type\":\"response.output_item.done\",\"sequence_number\":1,\"output_index\":0,\"item\":" + call + "}\n\n"
            + "data: {\"type\":\"response.output_item.added\",\"sequence_number\":2,\"output_index\":1,\"item\":" + output + "}\n\n"
            + "data: " + chunk + "\n\n"
            + "data: {\"type\":\"response.output_item.done\",\"sequence_number\":4,\"output_index\":1,\"item\":" + output + "}\n\n"
            + "data: {\"type\":\"response.completed\",\"sequence_number\":5,\"response\":{\"id\":\"resp_shell\",\"model\":\"openai/gpt-fixture\",\"status\":\"completed\",\"output\":[]}}\n\n"
            + "data: [DONE]\n\n";
        using var http = CreateHttpClient(_ => CreateStreamingResponse(stream));
        using var client = CreateClient(http, CreateAgent(modelId: "openai/gpt-fixture"));
        var agent = new ChatClientAgent(client, instructions: "Fixture test instructions", name: "FixtureAgent");
        var parts = await CollectAsync(new StreamingContentMapper().MapAsync(agent.RunStreamingAsync(CreateUserMessages("Run shell"))));
        var input = Assert.Single(parts.OfType<ToolCallPart>(), part => part.ToolName == "shell_call");
        var results = parts.OfType<ToolOutputAvailablePart>().Where(part => part.ToolCallId == input.ToolCallId).ToList();
        var preliminary = Assert.Single(results, part => part.Preliminary == true);
        var final = Assert.Single(results, part => part.Preliminary is false or null);
        Assert.True(preliminary.ProviderExecuted);
        Assert.True(final.ProviderExecuted);
        Assert.True(parts.IndexOf(preliminary) < parts.IndexOf(final));
        Assert.Equal("progress\n", JsonSerializer.SerializeToElement(preliminary.Output).GetProperty("output")[0].GetProperty("stdout").GetString());
        Assert.Equal("complete\n", JsonSerializer.SerializeToElement(final.Output).GetProperty("output")[0].GetProperty("stdout").GetString());
    }
}
