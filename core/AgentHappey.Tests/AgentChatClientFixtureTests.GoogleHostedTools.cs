using System.Text.Json;
using AgentHappey.Core;
using AIHappey.Vercel.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentHappey.Tests;

public sealed partial class AgentChatClientFixtureTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task Google_hosted_code_execution_never_runs_or_requests_client_approval(bool terminalOnly, bool requestApproval, bool registerClientFunction)
    {
        const string code = "print('hosted-result-42')";
        var payload = new Dictionary<string, object?>
        {
            ["content"] = new[] { new { type = "text", text = "hosted-result-42\n" } },
            ["_meta"] = new Dictionary<string, object?>
            {
                ["aihappey/gateway"] = new
                {
                    provider = "google", title = "code_execution",
                    providerMetadata = new { google = new { signature = "opaque-code-signature" } }
                }
            }
        };
        var item = JsonSerializer.SerializeToElement(new
        {
            type = "mcp_call", id = "hosted-code", name = "code_execution", server_label = "aihappey",
            arguments = JsonSerializer.Serialize(new { code, language = "python" }),
            output = JsonSerializer.Serialize(payload), status = "completed"
        });
        var requests = new List<string>();
        using var http = CreateHttpClient(request =>
        {
            requests.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            // A provider-hosted artifact must not cause a tool-loop retry, whether
            // the runtime has a same-name function or no executable function.
            Assert.Single(requests);
            return CreateStreamingResponse(GoogleHostedCodeStream(item, terminalOnly));
        });
        var definition = CreateAgent(modelId: "google/gemini-4-carbon", name: "GoogleAgent",
            providerMetadata: JsonSerializer.Deserialize<Dictionary<string, object>>("""
                {"tools":[{"type":"google_maps"},{"type":"url_context"},
                  {"type":"google_search","search_types":["web_search"]},{"type":"code_execution"}]}
                """)!);
        using var client = CreateClient(http, definition);
        var executions = 0;
        var collidingTool = AIFunctionFactory.Create((string code) =>
        {
            executions++;
            return "This client function must not run for hosted code execution.";
        }, "code_execution");
        var agent = new ChatClientAgent(client, name: "GoogleAgent", tools: registerClientFunction ? [collidingTool] : []);

        var parts = await CollectAsync(new StreamingContentMapper().MapAsync(
            agent.RunStreamingAsync(CreateUserMessages("Run some Python code")), requestToolApproval: requestApproval));

        Assert.Equal(0, executions);
        var call = Assert.Single(parts.OfType<ToolCallPart>());
        Assert.Equal("code_execution", call.ToolName);
        Assert.Equal("hosted-code", call.ToolCallId);
        Assert.True(call.ProviderExecuted);
        Assert.Equal(code, JsonSerializer.SerializeToElement(call.Input).GetProperty("code").GetString());
        var output = Assert.Single(parts.OfType<ToolOutputAvailablePart>());
        Assert.True(output.ProviderExecuted);
        Assert.Equal(call.ToolCallId, output.ToolCallId);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(payload), JsonSerializer.SerializeToElement(output.Output)));
        Assert.Empty(parts.OfType<ToolApprovalRequestUIPart>());
        Assert.DoesNotContain("function not found", JsonSerializer.Serialize(parts), StringComparison.OrdinalIgnoreCase);
        using var body = JsonDocument.Parse(Assert.Single(requests));
        Assert.Contains(body.RootElement.GetProperty("tools").EnumerateArray(),
            tool => tool.GetProperty("type").GetString() == "code_execution");
    }

    private static string GoogleHostedCodeStream(JsonElement item, bool terminalOnly)
    {
        var events = new List<object>();
        if (!terminalOnly)
        {
            var start = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(item)!;
            start["status"] = JsonSerializer.SerializeToElement("in_progress");
            start["output"] = JsonSerializer.SerializeToElement<object?>(null);
            events.Add(new { type = "response.output_item.added", output_index = 0, item = start });
            events.Add(new { type = "response.mcp_call_arguments.done", output_index = 0,
                item_id = "hosted-code", arguments = item.GetProperty("arguments").GetString() });
            events.Add(new { type = "response.output_item.done", output_index = 0, item });
        }
        events.Add(new { type = "response.completed", response = new { id = "google-response", status = "completed", output = new[] { item } } });
        return string.Concat(events.Select((value, index) =>
        {
            var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value))!;
            fields["sequence_number"] = JsonSerializer.SerializeToElement(index + 1);
            return "data: " + JsonSerializer.Serialize(fields) + "\n\n";
        })) + "data: [DONE]\n\n";
    }
}
