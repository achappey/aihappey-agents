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
    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task Mixed_client_batch_executes_multiple_servers_once_before_suspending(bool streaming, int clientIndex)
    {
        var firstExecutions = 0;
        var secondExecutions = 0;
        List<AITool> tools =
        [
            AIFunctionFactory.Create(() => ++firstExecutions, "server_first"),
            AIFunctionFactory.Create(() => ++secondExecutions, "server_second")
        ];
        AddClientTools(tools, [CreateBrowserTool()]);
        var names = new List<string> { "server_first", "server_second" };
        names.Insert(clientIndex, "browser_lookup");
        var requests = 0;
        using var http = CreateHttpClient(_ =>
        {
            Assert.Equal(1, ++requests);
            var response = CreateFunctionBatchResponse(names);
            return streaming ? CreateStreamingResponse(CreateToolBatchStream(response)) : CreateJsonResponse(response);
        });
        using var client = CreateClient(http, CreateAgent(name: "MixedAgent"));
        var agent = CreateClientToolAgent(client, tools);
        var options = new ChatClientAgentRunOptions(new ChatOptions());
        var contents = streaming
            ? (await CollectAsync(agent.RunStreamingAsync(CreateUserMessages("Use all tools"), options: options)))
                .SelectMany(update => update.Contents).ToList()
            : (await agent.RunAsync(CreateUserMessages("Use all tools"), options: options))
                .Messages.SelectMany(message => message.Contents).ToList();

        Assert.Equal(1, firstExecutions);
        Assert.Equal(1, secondExecutions);
        Assert.Equal(1, requests);
        Assert.Equal(3, contents.OfType<FunctionCallContent>().Count());
        var results = contents.OfType<FunctionResultContent>().ToList();
        Assert.Equal(2, results.Count);
        Assert.Single(results, result => result.CallId == "server_first-call");
        Assert.Single(results, result => result.CallId == "server_second-call");
        Assert.DoesNotContain(results, result => result.CallId == "browser_lookup-call");
        Assert.False(Assert.Single(contents.OfType<FunctionCallContent>(), call => call.Name == "browser_lookup").InformationalOnly);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Server_only_batch_does_not_suspend_for_an_older_pending_client_call(bool streaming)
    {
        var executions = 0;
        List<AITool> tools = [AIFunctionFactory.Create(() => ++executions, "server_lookup")];
        AddClientTools(tools, [CreateBrowserTool()]);
        var pending = new FunctionCallContent("older-client", "browser_lookup", new Dictionary<string, object?>());
        InvokeClientToolHelper("MarkCalls", new AIContent[] { pending }, new ChatOptions { Tools = tools });
        List<ChatMessage> history =
        [
            new(ChatRole.Assistant, [pending]),
            new(ChatRole.User, "Continue using only the server tool")
        ];
        var requests = 0;
        using var http = CreateHttpClient(_ =>
        {
            Assert.InRange(++requests, 1, 2);
            if (requests == 1)
            {
                var response = CreateFunctionBatchResponse(["server_lookup"]);
                return streaming ? CreateStreamingResponse(CreateToolBatchStream(response)) : CreateJsonResponse(response);
            }
            return streaming ? CreateStreamingResponse(CreateTextResponseStream("Done.")) : CreateJsonResponse(LoadFixture(StructuredFixturePath));
        });
        using var client = CreateClient(http, CreateAgent(name: "MixedAgent"));
        var agent = CreateClientToolAgent(client, tools);
        var options = new ChatClientAgentRunOptions(new ChatOptions());

        if (streaming)
            _ = await CollectAsync(agent.RunStreamingAsync(history, options: options));
        else
            _ = await agent.RunAsync(history, options: options);

        Assert.Equal(1, executions);
        Assert.Equal(2, requests);
    }

    private static string CreateFunctionBatchResponse(IEnumerable<string> names)
        => JsonSerializer.Serialize(new
        {
            id = "batch-response", @object = "response", created_at = 1735689600,
            status = "completed", model = "openai/gpt-fixture",
            output = names.Select(name => new
            {
                id = name + "-item", type = "function_call", call_id = name + "-call",
                name, arguments = name == "browser_lookup" ? "{\"query\":\"test\"}" : "{}", status = "completed"
            }).ToArray(),
            tools = Array.Empty<object>()
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Streaming_mapper_preserves_preliminary_outputs_until_final_without_repeating_input(bool requestApproval)
    {
        static FunctionResultContent Result(string text, bool preliminary) => new("shell-call",
            new Dictionary<string, object?>
            {
                ["output"] = new { text }, ["preliminary"] = preliminary,
                ["provider_executed"] = true,
                ["provider_metadata"] = new Dictionary<string, object?>()
            });
        var updates = new[]
        {
            new AgentResponseUpdate(ChatRole.Assistant,
                [new FunctionCallContent("shell-call", "shell_call", new Dictionary<string, object?>()) { InformationalOnly = true }]),
            new AgentResponseUpdate(ChatRole.Tool, [Result("first", true)]),
            new AgentResponseUpdate(ChatRole.Tool, [Result("second", true)]),
            new AgentResponseUpdate(ChatRole.Tool, [Result("complete", false)])
        };

        var parts = await CollectAsync(new StreamingContentMapper().MapAsync(
            updates.ToAsyncEnumerable(), requestToolApproval: requestApproval));

        Assert.Single(parts.OfType<ToolCallPart>());
        var outputs = parts.OfType<ToolOutputAvailablePart>().ToList();
        Assert.Equal(3, outputs.Count);
        Assert.Equal(new bool?[] { true, true, false }, outputs.Select(output => output.Preliminary));
        Assert.All(outputs, output => Assert.True(output.ProviderExecuted));
        Assert.Equal(new[] { "first", "second", "complete" }, outputs.Select(output =>
            JsonSerializer.SerializeToElement(output.Output).GetProperty("text").GetString()));
        Assert.Empty(parts.OfType<ToolApprovalRequestUIPart>());
    }

    [Fact]
    public async Task Streaming_mapper_emits_program_descriptor_without_waiting_for_a_program_result()
    {
        var program = new FunctionCallContent("program-call", "program", new Dictionary<string, object?> { ["code"] = "return 1;" })
        {
            InformationalOnly = true,
            RawRepresentation = new Dictionary<string, object?>
            {
                ["responses_item"] = new { type = "program", id = "program-item", call_id = "program-call", code = "return 1;" }
            }
        };
        var updates = new[] { new AgentResponseUpdate(ChatRole.Assistant, [program]) { AuthorName = "ProgrammaticAgent" } };

        var parts = await CollectAsync(new StreamingContentMapper().MapAsync(updates.ToAsyncEnumerable()));

        var call = Assert.Single(parts.OfType<ToolCallPart>());
        Assert.Equal("program-call", call.ToolCallId);
        Assert.True(call.ProviderExecuted);
        Assert.Contains("ProgrammaticAgent", call.ProviderMetadata ?? []);
        Assert.Empty(parts.OfType<ToolOutputAvailablePart>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Client_replay_retains_same_owner_results_and_filters_foreign_owner_results(bool streaming)
    {
        foreach (var owner in new[] { "StructuredAgent", "OtherAgent" })
        {
            var history = new[]
            {
                new UIMessage
                {
                    Id = "owned-history", Role = Role.assistant,
                    Parts =
                    [
                        new ToolCallPart { ToolCallId = "client-call", ToolName = "browser_lookup", Input = new { query = "test" }, ProviderExecuted = false },
                        new ToolOutputAvailablePart { ToolCallId = "client-call", Output = new { value = "browser-result" }, ProviderExecuted = false }
                    ]
                }
            }.ToMessages([owner]).ToList();

            var input = await CaptureClientReplayInputAsync(history, streaming);
            if (owner == "StructuredAgent")
            {
                Assert.Equal(2, input.GetArrayLength());
                Assert.Single(input.EnumerateArray(), item => item.GetProperty("type").GetString() == "function_call");
                var output = Assert.Single(input.EnumerateArray(), item => item.GetProperty("type").GetString() == "function_call_output");
                Assert.Contains("browser-result", output.GetProperty("output").GetString());
            }
            else
                Assert.Empty(input.EnumerateArray());
        }
    }

    [Fact]
    public async Task Responses_client_schema_preserves_unmodeled_keywords_through_outbound_tool_definitions()
    {
        var request = JsonSerializer.Deserialize<ResponseRequest>("""
            {
              "model":"openai/gpt-fixture", "input":"Use lookup",
              "tools":[{
                "type":"function", "name":"browser_lookup",
                "parameters":{
                  "type":"object", "properties":{"query":{"$ref":"#/$defs/query"}}, "required":["query"],
                  "additionalProperties":false,
                  "$defs":{"query":{"type":"string","minLength":1}},
                  "allOf":[{"maxProperties":1}], "x-schema-version":"fixture-v1"
                }
              }]
            }
            """, ResponseJson.Default)!;
        var mapped = request.ToChatRuntimeRequest();
        List<AITool> tools = [];
        AddClientTools(tools, mapped.Tools);
        var declaration = Assert.IsAssignableFrom<AIFunctionDeclaration>(Assert.Single(tools));
        AssertSchema(declaration.JsonSchema);

        string? body = null;
        using var http = CreateHttpClient(message =>
        {
            body = message.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return CreateJsonResponse(LoadFixture(StructuredFixturePath));
        });
        using var client = CreateClient(http, CreateAgent());
        await client.GetResponseAsync(CreateUserMessages("Use lookup"), new ChatOptions { Tools = tools });

        using var document = JsonDocument.Parse(body!);
        AssertSchema(Assert.Single(document.RootElement.GetProperty("tools").EnumerateArray()).GetProperty("parameters"));

        static void AssertSchema(JsonElement schema)
        {
            Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
            Assert.Equal("#/$defs/query", schema.GetProperty("properties").GetProperty("query").GetProperty("$ref").GetString());
            Assert.Equal(1, schema.GetProperty("$defs").GetProperty("query").GetProperty("minLength").GetInt32());
            Assert.Equal(1, schema.GetProperty("allOf")[0].GetProperty("maxProperties").GetInt32());
            Assert.Equal("fixture-v1", schema.GetProperty("x-schema-version").GetString());
        }
    }
}
