using System.Reflection;
using System.Text.Json;
using AgentHappey.Common.Extensions;
using AgentHappey.Common.Models;
using AgentHappey.Core;
using AgentHappey.Core.ChatClient;
using AgentHappey.Core.ChatRuntime;
using AgentHappey.Core.Responses;
using AIHappey.Responses;
using AIHappey.Vercel.Models;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Tool = AIHappey.Vercel.Models.Tool;

namespace AgentHappey.Tests;

public sealed partial class AgentChatClientFixtureTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Mixed_server_and_client_tools_execute_only_server_and_resume_statelessly(bool streaming, bool clientFirst)
    {
        var executions = 0;
        var server = AIFunctionFactory.Create(() =>
        {
            executions++;
            return "server-result";
        }, "server_lookup");
        List<AITool> tools = [server];
        AddClientTools(tools, [CreateBrowserTool()]);
        var requests = new List<string>();
        using var http = CreateHttpClient(request =>
        {
            requests.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Single(requests);
            return streaming
                ? CreateStreamingResponse(CreateMixedToolStream(clientFirst))
                : CreateJsonResponse(CreateMixedToolResponse(clientFirst));
        });
        using var client = CreateClient(http, CreateAgent());
        var agent = CreateClientToolAgent(client, tools);
        var options = new ChatClientAgentRunOptions(new ChatOptions());

        List<AIContent> contents;
        if (streaming)
        {
            var updates = await CollectAsync(agent.RunStreamingAsync(CreateUserMessages("Use both tools"), options: options));
            contents = updates.SelectMany(update => update.Contents).ToList();
        }
        else
        {
            var response = await agent.RunAsync(CreateUserMessages("Use both tools"), options: options);
            contents = response.Messages.SelectMany(message => message.Contents).ToList();
        }

        Assert.Equal(1, executions);
        Assert.Single(requests);
        var calls = contents.OfType<FunctionCallContent>().ToList();
        Assert.Equal(2, calls.Count);
        var clientCall = Assert.Single(calls, call => call.Name == "browser_lookup");
        Assert.True(Assert.IsType<bool>(clientCall.AdditionalProperties!["agenthappey_client_tool"]));
        Assert.False(clientCall.InformationalOnly);
        var serverResult = Assert.Single(contents.OfType<FunctionResultContent>());
        Assert.Equal("server-call", serverResult.CallId);
        Assert.DoesNotContain(contents.OfType<FunctionResultContent>(), result => result.CallId == "client-call");

        using (var request = JsonDocument.Parse(requests[0]))
        {
            var definitions = request.RootElement.GetProperty("tools").EnumerateArray().ToList();
            Assert.Equal(2, definitions.Count);
            var definition = Assert.Single(definitions, tool => tool.GetProperty("name").GetString() == "browser_lookup");
            Assert.Equal("function", definition.GetProperty("type").GetString());
            Assert.Equal("Browser-owned lookup", definition.GetProperty("description").GetString());
            Assert.Equal("string", definition.GetProperty("parameters").GetProperty("properties")
                .GetProperty("query").GetProperty("type").GetString());
        }

        var history = new[]
        {
            new UIMessage
            {
                Id = "mixed-assistant",
                Role = Role.assistant,
                Parts =
                [
                    new ToolInvocationPart
                    {
                        ToolCallId = "server-call",
                        Type = "tool-server_lookup",
                        Input = calls.Single(call => call.Name == "server_lookup").Arguments,
                        Output = serverResult.Result,
                        State = "output-available"
                    },
                    new ToolInvocationPart
                    {
                        ToolCallId = clientCall.CallId,
                        Type = "tool-browser_lookup",
                        Input = clientCall.Arguments,
                        Output = new { value = "browser-result" },
                        State = "output-available"
                    }
                ]
            }
        }.ToMessages(["MixedAgent"]).ToList();
        string? continuationBody = null;
        using var continuationHttp = CreateHttpClient(request =>
        {
            Assert.Null(continuationBody);
            continuationBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return streaming
                ? CreateStreamingResponse(CreateTextResponseStream("Both results received."))
                : CreateJsonResponse(LoadFixture(StructuredFixturePath));
        });
        using var freshClient = CreateClient(continuationHttp, CreateAgent());
        List<AITool> continuationTools = [server];
        AddClientTools(continuationTools, [CreateBrowserTool()]);
        var freshAgent = CreateClientToolAgent(freshClient, continuationTools);
        var continuationOptions = new ChatClientAgentRunOptions(new ChatOptions());
        if (streaming)
            _ = await CollectAsync(freshAgent.RunStreamingAsync(history, options: continuationOptions));
        else
            _ = await freshAgent.RunAsync(history, options: continuationOptions);

        Assert.Equal(1, executions);
        Assert.NotNull(continuationBody);
        using var continuation = JsonDocument.Parse(continuationBody);
        var input = continuation.RootElement.GetProperty("input").EnumerateArray().ToList();
        foreach (var callId in new[] { "server-call", "client-call" })
        {
            Assert.Single(input, item => item.GetProperty("type").GetString() == "function_call"
                && item.GetProperty("call_id").GetString() == callId);
            var output = Assert.Single(input, item => item.GetProperty("type").GetString() == "function_call_output"
                && item.GetProperty("call_id").GetString() == callId);
            Assert.Contains(callId == "server-call" ? "server-result" : "browser-result",
                output.GetProperty("output").GetString());
        }
    }

    [Fact]
    public void Client_tools_preserve_schema_in_sdk_pending_adapter()
    {
        List<AITool> tools = [];
        AddClientTools(tools, [CreateBrowserTool()]);

        var declaration = Assert.IsAssignableFrom<AIFunctionDeclaration>(Assert.Single(tools));
        Assert.IsAssignableFrom<AIFunction>(declaration);
        Assert.Equal("browser_lookup", declaration.Name);
        Assert.Equal("Browser-owned lookup", declaration.Description);
        Assert.Equal("object", declaration.JsonSchema.GetProperty("type").GetString());
        Assert.Equal("query", Assert.Single(declaration.JsonSchema.GetProperty("required").EnumerateArray()).GetString());
        Assert.Equal("object", declaration.ReturnJsonSchema!.Value.GetProperty("type").GetString());
    }

    [Fact]
    public void Omitted_client_tools_leave_existing_server_tools_unchanged()
    {
        var server = AIFunctionFactory.Create(() => "server", "server_lookup");
        List<AITool> tools = [server];

        AddClientTools(tools, null);
        AddClientTools(tools, []);

        Assert.Same(server, Assert.Single(tools));
    }

    [Fact]
    public void Client_tool_without_schema_uses_empty_object_schema()
    {
        List<AITool> tools = [];
        AddClientTools(tools, [new Tool { Name = "browser_lookup" }]);

        var declaration = Assert.IsAssignableFrom<AIFunctionDeclaration>(Assert.Single(tools));
        Assert.Equal("object", declaration.JsonSchema.GetProperty("type").GetString());
        Assert.Empty(declaration.JsonSchema.GetProperty("properties").EnumerateObject());
        Assert.Null(declaration.ReturnJsonSchema);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("ai_input_required")]
    [InlineData("approval-request")]
    [InlineData("CONNECT_MCP")]
    [InlineData("client_tool_search")]
    [InlineData("resource_search")]
    [InlineData("program")]
    [InlineData("web_search")]
    [InlineData("code_interpreter")]
    [InlineData("image_generation")]
    [InlineData("shell_call")]
    [InlineData("google_antigravity_state")]
    [InlineData("GOOGLE_CUSTOM_AGENT_STATE")]
    public void Invalid_or_reserved_client_names_are_rejected_atomically(string name)
    {
        var server = AIFunctionFactory.Create(() => "server", "server_lookup");
        List<AITool> tools = [server];

        var exception = Assert.Throws<TargetInvocationException>(() =>
            AddClientTools(tools, [CreateBrowserTool(), new Tool { Name = name }]));

        Assert.IsType<ArgumentException>(exception.InnerException);
        Assert.Same(server, Assert.Single(tools));
    }

    [Theory]
    [InlineData("browser_lookup", "BROWSER_LOOKUP")]
    [InlineData("server_lookup", "SERVER_LOOKUP")]
    public void Client_names_cannot_collide_with_other_clients_or_server_tools(string first, string second)
    {
        var server = AIFunctionFactory.Create(() => "server", "server_lookup");
        List<AITool> tools = [server];
        var declarations = first == "server_lookup"
            ? new[] { new Tool { Name = second } }
            : new[] { new Tool { Name = first }, new Tool { Name = second } };

        var exception = Assert.Throws<TargetInvocationException>(() => AddClientTools(tools, declarations));

        Assert.IsType<ArgumentException>(exception.InnerException);
        Assert.Same(server, Assert.Single(tools));
    }

    [Fact]
    public void Ownership_marking_is_limited_to_current_request_client_declarations()
    {
        List<AITool> tools = [AIFunctionFactory.Create(() => "server", "server_lookup")];
        AddClientTools(tools, [CreateBrowserTool()]);
        var clientCall = new FunctionCallContent("client", "browser_lookup", new Dictionary<string, object?>())
        {
            AdditionalProperties = new() { ["existing"] = "preserved" }
        };
        var serverCall = new FunctionCallContent("server", "server_lookup", new Dictionary<string, object?>());
        var unknownCall = new FunctionCallContent("unknown", "other_tool", new Dictionary<string, object?>());
        AIContent[] contents = [clientCall, serverCall, unknownCall, new TextContent("not a tool")];

        InvokeClientToolHelper("MarkCalls", contents, new ChatOptions { Tools = tools });

        Assert.Equal("preserved", clientCall.AdditionalProperties["existing"]);
        Assert.Equal(true, clientCall.AdditionalProperties["agenthappey_client_tool"]);
        Assert.False(serverCall.AdditionalProperties?.ContainsKey("agenthappey_client_tool") == true);
        Assert.False(unknownCall.AdditionalProperties?.ContainsKey("agenthappey_client_tool") == true);

        var nextRequestCall = new FunctionCallContent("next", "browser_lookup", new Dictionary<string, object?>());
        InvokeClientToolHelper("MarkCalls", new AIContent[] { nextRequestCall }, new ChatOptions());
        Assert.False(nextRequestCall.AdditionalProperties?.ContainsKey("agenthappey_client_tool") == true);
    }

    [Fact]
    public async Task Client_call_is_emitted_immediately_while_only_server_result_requests_approval()
    {
        List<AITool> tools = [];
        AddClientTools(tools, [CreateBrowserTool()]);
        var clientCall = new FunctionCallContent("client-call", "browser_lookup", new Dictionary<string, object?> { ["query"] = "test" });
        InvokeClientToolHelper("MarkCalls", new AIContent[] { clientCall }, new ChatOptions { Tools = tools });
        var serverResultProduced = false;
        async IAsyncEnumerable<AgentResponseUpdate> Updates()
        {
            yield return new AgentResponseUpdate(ChatRole.Assistant,
            [
                new FunctionCallContent("server-call", "server_lookup", new Dictionary<string, object?>()),
                clientCall
            ]) { MessageId = "mixed", AuthorName = "MixedAgent" };
            await Task.Yield();
            serverResultProduced = true;
            yield return new AgentResponseUpdate(ChatRole.Tool,
            [
                new FunctionResultContent("server-call", "server-result")
            ]) { MessageId = "mixed", AuthorName = "MixedAgent" };
        }

        await using var parts = new StreamingContentMapper()
            .MapAsync(Updates(), requestToolApproval: true).GetAsyncEnumerator();
        Assert.True(await parts.MoveNextAsync());
        var immediate = Assert.IsType<ToolCallPart>(parts.Current);
        Assert.Equal("client-call", immediate.ToolCallId);
        Assert.False(immediate.ProviderExecuted);
        Assert.False(serverResultProduced);

        var remaining = new List<UIMessagePart>();
        while (await parts.MoveNextAsync())
            remaining.Add(parts.Current);
        var approval = Assert.Single(remaining.OfType<ToolApprovalRequestUIPart>());
        Assert.Equal("server-call", approval.ToolCallId);
        Assert.Equal("server-call", approval.ApprovalId);
        Assert.True(Assert.Single(remaining.OfType<ToolCallPart>()).ProviderExecuted);
        Assert.Equal("server-call", Assert.Single(remaining.OfType<ToolOutputAvailablePart>()).ToolCallId);
        Assert.DoesNotContain(remaining.OfType<ToolCallPart>(), part => part.ToolCallId == "client-call");
    }

    [Theory]
    [InlineData("output-available", false, false)]
    [InlineData("output-available", true, false)]
    [InlineData("output-error", false, false)]
    [InlineData("output-available", false, true)]
    public async Task Completed_client_result_requests_approval_before_inference(string state, bool nullOutput, bool separateParts)
    {
        var request = new AgentRequest
        {
            Tools = [CreateBrowserTool()],
            Messages =
            [
                new UIMessage
                {
                    Id = "client-result",
                    Role = Role.assistant,
                    Parts =
                    [
                        new ToolInvocationPart
                        {
                            ToolCallId = "client-call",
                            Type = "tool-browser_lookup",
                            Input = new { query = "test" },
                            Output = nullOutput ? null : new { value = "browser-result" },
                            State = state,
                            ProviderExecuted = false
                        }
                    ]
                }
            ]
        };
        if (separateParts)
            request.Messages[0].Parts =
            [
                new ToolCallPart
                {
                    ToolCallId = "client-call",
                    ToolName = "browser_lookup",
                    Input = new { query = "test" },
                    ProviderExecuted = false
                },
                new ToolOutputAvailablePart
                {
                    ToolCallId = "client-call",
                    Output = new { value = "browser-result" },
                    ProviderExecuted = false
                }
            ];
        var messages = request.Messages.ToMessages(["MixedAgent"]).ToList();
        var context = new ChatRuntimeContext(messages, [], null, [CreateAgent()])
        {
            RequestToolApproval = true
        };
        var response = new DefaultHttpContext().Response;
        response.Body = new MemoryStream();
        var orchestrator = new ChatRuntimeOrchestrator(new StreamingContentMapper(), null!);

        // No runtime agent is supplied: requesting approval must not start inference.
        await orchestrator.ExecuteAsync(response, request, context);

        response.Body.Position = 0;
        var stream = await new StreamReader(response.Body).ReadToEndAsync();
        var parts = stream.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line["data: ".Length..]).RootElement.Clone()).ToList();
        var call = Assert.Single(parts, part => part.GetProperty("type").GetString() == "tool-input-available");
        Assert.True(call.GetProperty("providerExecuted").GetBoolean());
        var output = Assert.Single(parts, part => part.GetProperty("type").GetString() == "tool-output-available");
        Assert.False(output.GetProperty("providerExecuted").GetBoolean());
        Assert.Equal("client-call", output.GetProperty("toolCallId").GetString());
        var approval = Assert.Single(parts, part => part.GetProperty("type").GetString() == "tool-approval-request");
        Assert.Equal("client-call", approval.GetProperty("approvalId").GetString());
        Assert.True(parts.IndexOf(output) < parts.IndexOf(approval));
        if (!nullOutput)
            Assert.Contains("browser-result", output.GetRawText());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Answered_client_approval_continues_with_existing_result_replay(bool approved)
    {
        var tool = new ToolInvocationPart
        {
            ToolCallId = "client-call",
            Type = "tool-browser_lookup",
            Input = new { query = "test" },
            Output = new { value = "browser-result" },
            ProviderExecuted = false,
            State = "approval-responded",
            Approval = new ToolApproval { Id = "client-call", Approved = approved }
        };
        var request = new AgentRequest
        {
            Tools = [CreateBrowserTool()],
            Messages = [new UIMessage { Id = "approved-result", Role = Role.assistant, Parts = [tool] }]
        };
        var messages = request.Messages.ToMessages(["MixedAgent"]).ToList();
        string? inferenceBody = null;
        using var http = CreateHttpClient(message =>
        {
            Assert.Null(inferenceBody);
            inferenceBody = message.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return CreateStreamingResponse(CreateTextResponseStream("Continued."));
        });
        using var client = CreateClient(http, CreateAgent());
        List<AITool> tools = [];
        AddClientTools(tools, request.Tools);
        var context = new ChatRuntimeContext(messages, [CreateClientToolAgent(client, tools)],
            new ChatClientAgentRunOptions(new ChatOptions()), [CreateAgent()])
        {
            RequestToolApproval = true
        };
        var response = new DefaultHttpContext().Response;
        response.Body = new MemoryStream();

        await new ChatRuntimeOrchestrator(new StreamingContentMapper(), null!)
            .ExecuteAsync(response, request, context);

        Assert.NotNull(inferenceBody);
        using var body = JsonDocument.Parse(inferenceBody);
        var output = Assert.Single(body.RootElement.GetProperty("input").EnumerateArray(),
            item => item.GetProperty("type").GetString() == "function_call_output");
        Assert.Equal("client-call", output.GetProperty("call_id").GetString());
        Assert.Equal(approved, output.GetProperty("output").GetString()!.Contains("browser-result"));
        if (!approved)
            Assert.Contains("false", output.GetProperty("output").GetString());
    }

    [Fact]
    public async Task Streamed_client_result_uses_existing_result_then_approval_order()
    {
        var call = new FunctionCallContent("client-call", "browser_lookup", new Dictionary<string, object?>())
        {
            AdditionalProperties = new() { ["agenthappey_client_tool"] = true }
        };
        var updates = new[]
        {
            new AgentResponseUpdate(ChatRole.Assistant, [call]),
            new AgentResponseUpdate(ChatRole.Tool, [new FunctionResultContent("client-call", "browser-result")])
        };
        var parts = new List<UIMessagePart>();
        await foreach (var part in new StreamingContentMapper()
            .MapAsync(updates.ToAsyncEnumerable(), requestToolApproval: true))
            parts.Add(part);

        Assert.False(Assert.IsType<ToolCallPart>(parts[0]).ProviderExecuted);
        Assert.True(Assert.IsType<ToolCallPart>(parts[1]).ProviderExecuted);
        Assert.Equal("browser-result", Assert.IsType<ToolOutputAvailablePart>(parts[2]).Output);
        Assert.Equal("client-call", Assert.IsType<ToolApprovalRequestUIPart>(parts[3]).ApprovalId);
    }

    [Fact]
    public void Responses_mapper_preserves_client_function_definitions_and_ignores_native_tools()
    {
        var request = JsonSerializer.Deserialize<ResponseRequest>("""
            {
              "model": "openai/gpt-fixture",
              "input": "Use browser lookup",
              "tools": [
                {
                  "type": "function",
                  "name": "browser_lookup",
                  "description": "Browser-owned lookup",
                  "parameters": {
                    "type": "object",
                    "properties": { "query": { "type": "string" } },
                    "required": ["query"],
                    "additionalProperties": false
                  }
                },
                { "type": "web_search" }
              ]
            }
            """, ResponseJson.Default)!;

        var mapped = request.ToChatRuntimeRequest();

        Assert.Equal("openai/gpt-fixture", mapped.Model);
        var tool = Assert.Single(mapped.Tools!);
        Assert.Equal("browser_lookup", tool.Name);
        Assert.Equal("Browser-owned lookup", tool.Description);
        var schema = JsonSerializer.SerializeToElement(tool.InputSchema, JsonSerializerOptions.Web);
        Assert.Equal("string", schema.GetProperty("properties").GetProperty("query").GetProperty("type").GetString());
        Assert.Equal("query", Assert.Single(schema.GetProperty("required").EnumerateArray()).GetString());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public void Requests_without_tools_remain_backwards_compatible()
    {
        var request = JsonSerializer.Deserialize<ResponseRequest>(
            """{"model":"openai/gpt-fixture","input":"Hello"}""", ResponseJson.Default)!;

        Assert.Null(request.ToChatRuntimeRequest().Tools);
        Assert.Null(new ChatRuntimeRequest([], "model", null, null).Tools);
        Assert.Null(new AgentRequest().Tools);
    }

    [Fact]
    public void Ui_request_mapping_preserves_request_scoped_tools()
    {
        var request = new AgentRequest { Messages = [], Tools = [CreateBrowserTool()] };
        var method = typeof(ChatRuntimeOrchestrator).GetMethod("CreateRuntimeRequest",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        var mapped = Assert.IsType<ChatRuntimeRequest>(method.Invoke(null, [request, null]));

        Assert.Same(request.Tools, mapped.Tools);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unanswered_client_calls_are_omitted_until_resolved_on_fresh_inference_replay(bool streaming)
    {
        List<ChatMessage> history =
        [
            new(ChatRole.User, "Original user text"),
            new(ChatRole.Assistant,
            [
                new TextContent("Preserved assistant text"),
                new FunctionCallContent("unanswered-client", "browser_lookup", new Dictionary<string, object?> { ["query"] = "pending" }),
                new FunctionCallContent("answered-client", "browser_lookup", new Dictionary<string, object?> { ["query"] = "answered" }),
                new FunctionCallContent("answered-server", "server_lookup", new Dictionary<string, object?>()),
                new FunctionCallContent("unanswered-server", "server_lookup", new Dictionary<string, object?>())
            ]),
            new(ChatRole.Tool,
            [
                new FunctionResultContent("answered-server", new { value = "server-result" }),
                new FunctionResultContent("answered-client", new { value = "other-client-result" })
            ]),
            new(ChatRole.User, "Continue without the pending browser result")
        ];

        var first = await CaptureClientReplayInputAsync(history, streaming);
        var firstItems = first.EnumerateArray().ToList();
        Assert.DoesNotContain(firstItems, item => item.GetProperty("type").GetString() == "function_call"
            && item.GetProperty("call_id").GetString() == "unanswered-client");
        foreach (var preservedCall in new[] { "answered-client", "answered-server", "unanswered-server" })
            Assert.Single(firstItems, item => item.GetProperty("type").GetString() == "function_call"
                && item.GetProperty("call_id").GetString() == preservedCall);
        Assert.Equal(2, firstItems.Count(item => item.GetProperty("type").GetString() == "function_call_output"));
        Assert.Contains("Original user text", first.GetRawText());
        Assert.Contains("Preserved assistant text", first.GetRawText());
        Assert.Contains("Continue without the pending browser result", first.GetRawText());
        Assert.Contains("server-result", first.GetRawText());
        Assert.Single(history.SelectMany(message => message.Contents).OfType<FunctionCallContent>(),
            call => call.CallId == "unanswered-client");

        history.Add(new ChatMessage(ChatRole.Tool,
        [
            new FunctionResultContent("unanswered-client", new { value = "late-browser-result" })
        ]));
        var resolved = await CaptureClientReplayInputAsync(history, streaming);
        var resolvedItems = resolved.EnumerateArray().ToList();
        Assert.Single(resolvedItems, item => item.GetProperty("type").GetString() == "function_call"
            && item.GetProperty("call_id").GetString() == "unanswered-client");
        var output = Assert.Single(resolvedItems, item => item.GetProperty("type").GetString() == "function_call_output"
            && item.GetProperty("call_id").GetString() == "unanswered-client");
        using var result = JsonDocument.Parse(output.GetProperty("output").GetString()!);
        Assert.Equal("late-browser-result", result.RootElement.GetProperty("value").GetString());
    }

    [Fact]
    public async Task Interleaved_ui_tool_calls_pair_results_by_call_id_not_position()
    {
        var messages = new[]
        {
            new UIMessage
            {
                Id = "interleaved-assistant",
                Role = Role.assistant,
                Parts =
                [
                    new ToolCallPart
                    {
                        ToolCallId = "server-call",
                        ToolName = "server_lookup",
                        Input = new { },
                        ProviderExecuted = false
                    },
                    new ToolCallPart
                    {
                        ToolCallId = "client-call",
                        ToolName = "browser_lookup",
                        Input = new { query = "test" },
                        ProviderExecuted = false
                    },
                    new ToolOutputAvailablePart
                    {
                        ToolCallId = "client-call",
                        Output = new { value = "browser-result" },
                        ProviderExecuted = false
                    },
                    new ToolOutputAvailablePart
                    {
                        ToolCallId = "server-call",
                        Output = new { value = "server-result" },
                        ProviderExecuted = false
                    }
                ]
            }
        }.ToMessages(["MixedAgent"]).ToList();
        Assert.Equal(2, messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>().Count());
        var results = messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().ToList();
        Assert.Equal(2, results.Count);
        foreach (var (callId, expected) in new[] { ("server-call", "server-result"), ("client-call", "browser-result") })
        {
            var correlated = Assert.Single(results, result => result.CallId == callId);
            var envelope = JsonSerializer.SerializeToElement(correlated.Result, JsonSerializerOptions.Web);
            Assert.Equal(expected, envelope.GetProperty("output").GetProperty("value").GetString());
        }

        var input = await CaptureClientReplayInputAsync(messages);
        var items = input.EnumerateArray().ToList();
        foreach (var (callId, expected) in new[] { ("server-call", "server-result"), ("client-call", "browser-result") })
        {
            Assert.Single(items, item => item.GetProperty("type").GetString() == "function_call"
                && item.GetProperty("call_id").GetString() == callId);
            var output = Assert.Single(items, item => item.GetProperty("type").GetString() == "function_call_output"
                && item.GetProperty("call_id").GetString() == callId);
            using var result = JsonDocument.Parse(output.GetProperty("output").GetString()!);
            Assert.Equal(expected, result.RootElement.GetProperty("value").GetString());
        }
    }

    private static async Task<JsonElement> CaptureClientReplayInputAsync(IEnumerable<ChatMessage> messages, bool streaming = false)
    {
        string? body = null;
        using var http = CreateHttpClient(request =>
        {
            Assert.Null(body);
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return streaming
                ? CreateStreamingResponse(CreateTextResponseStream("Replay accepted."))
                : CreateJsonResponse(LoadFixture(StructuredFixturePath));
        });
        using var client = CreateClient(http, CreateAgent());
        List<AITool> tools = [AIFunctionFactory.Create(() => "unused", "server_lookup")];
        AddClientTools(tools, [CreateBrowserTool()]);
        var options = new ChatOptions { Tools = tools };
        if (streaming)
            _ = await CollectAsync(client.GetStreamingResponseAsync(messages, options));
        else
            _ = await client.GetResponseAsync(messages, options);
        Assert.NotNull(body);
        using var request = JsonDocument.Parse(body);
        return request.RootElement.GetProperty("input").Clone();
    }

    private static Tool CreateBrowserTool() => JsonSerializer.Deserialize<Tool>("""
        {
          "name": "browser_lookup",
          "description": "Browser-owned lookup",
          "inputSchema": {
            "type": "object",
            "properties": { "query": { "type": "string" } },
            "required": ["query"]
          },
          "outputSchema": { "type": "object", "properties": { "value": { "type": "string" } } }
        }
        """, JsonSerializerOptions.Web)!;

    private static void AddClientTools(List<AITool> tools, IReadOnlyList<Tool>? clients)
        => InvokeClientToolHelper("AddTo", tools, clients);

    private static AIAgent CreateClientToolAgent(AgentChatClient client, List<AITool> tools)
    {
        var pendingResult = typeof(AgentChatClient).Assembly.GetType(
            "AgentHappey.Core.ChatRuntime.ClientToolDeclaration+PendingResult", throwOnError: true)!;
        var runtimeAgent = new ChatClientAgent(client, name: "MixedAgent", tools: tools)
            .AsBuilder().Use(async (_, invocation, next, ct) =>
            {
                var result = await next(invocation, ct);
                if (pendingResult.IsInstanceOfType(result))
                    invocation.Terminate = true;
                return result;
            }).Build();
        return Assert.IsAssignableFrom<AIAgent>(InvokeClientToolHelper("WrapAgent", runtimeAgent));
    }

    private static object? InvokeClientToolHelper(string method, params object?[] arguments)
        => typeof(AgentChatClient).Assembly.GetType("AgentHappey.Core.ChatRuntime.ClientToolDeclaration", throwOnError: true)!
            .GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, arguments);

    private static string CreateMixedToolResponse(bool clientFirst)
    {
        var calls = new[]
        {
            new { id = "server-item", type = "function_call", call_id = "server-call", name = "server_lookup", arguments = "{}", status = "completed" },
            new { id = "client-item", type = "function_call", call_id = "client-call", name = "browser_lookup", arguments = "{\"query\":\"test\"}", status = "completed" }
        };
        return JsonSerializer.Serialize(new
        {
            id = "mixed-response",
            @object = "response",
            created_at = 1735689600,
            status = "completed",
            model = "openai/gpt-fixture",
            output = clientFirst ? calls.Reverse().ToArray() : calls,
            tools = Array.Empty<object>()
        });
    }

    private static string CreateMixedToolStream(bool clientFirst)
    {
        using var document = JsonDocument.Parse(CreateMixedToolResponse(clientFirst));
        var response = document.RootElement;
        var events = new List<string>
        {
            Sse("response.created", new { type = "response.created", sequence_number = 1, response })
        };
        var sequence = 2;
        var index = 0;
        foreach (var item in response.GetProperty("output").EnumerateArray())
        {
            events.Add(Sse("response.output_item.added", new
            {
                type = "response.output_item.added", sequence_number = sequence++, output_index = index,
                item = new
                {
                    id = item.GetProperty("id").GetString(), type = "function_call",
                    call_id = item.GetProperty("call_id").GetString(), name = item.GetProperty("name").GetString(),
                    arguments = "", status = "in_progress"
                }
            }));
            events.Add(Sse("response.function_call_arguments.done", new
            {
                type = "response.function_call_arguments.done", sequence_number = sequence++,
                output_index = index, item_id = item.GetProperty("id").GetString(),
                arguments = item.GetProperty("arguments").GetString()
            }));
            events.Add(Sse("response.output_item.done", new
            {
                type = "response.output_item.done", sequence_number = sequence++, output_index = index++, item
            }));
        }
        events.Add(Sse("response.completed", new { type = "response.completed", sequence_number = sequence, response }));
        events.Add("data: [DONE]");
        return string.Join("\n\n", events) + "\n\n";
    }
}
