using System.Collections.Concurrent;
using System.Text.Json;
using AgentHappey.Common.Extensions;
using AgentHappey.Core.ChatClient;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;
using AIHappey.Responses;

namespace AgentHappey.Tests;

public sealed partial class AgentChatClientFixtureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Tool_search_extracts_valid_selected_definitions_from_supported_output_envelopes(bool wrapped)
    {
        var result = SearchResult();
        object value = wrapped ? new { output = result, provider_executed = false } : result;
        var tools = InvokePrivateStatic<List<AIHappey.Responses.ResponseToolDefinition>>("ExtractSelectedToolDefinitions", value);
        Assert.Single(tools);
        Assert.Equal("read_resource", tools[0].Extra!["name"].GetString());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Tool_search_fresh_client_replays_approved_dynamic_history(bool wrapped, bool streaming)
    {
        string body = "";
        using var http = CreateHttpClient(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return streaming ? CreateStreamingResponse(LoadFixture(StreamingFixturePath))
                : CreateJsonResponse(LoadFixture(StructuredFixturePath));
        });
        using var client = CreateClient(http, CreateAgent());
        var read = AIFunctionFactory.Create((string uri) => "mail count: 7", "read_resource");
        GetPrivateField<ConcurrentDictionary<string, AITool>>(client, "Tools")[read.Name] = read;
        var messages = new[] { SearchHistory(SearchResult(), wrapped) }.ToMessages(["StructuredAgent"]).ToList();
        if (streaming) await CollectAsync(client.GetStreamingResponseAsync(messages));
        else await client.GetResponseAsync(messages);

        using var json = JsonDocument.Parse(body);
        var input = json.RootElement.GetProperty("input").EnumerateArray().ToArray();
        Assert.Equal(new[] { "tool_search_call", "tool_search_output" }, input.Select(i => i.GetProperty("type").GetString()));
        Assert.Equal("call_search_1", input[0].GetProperty("call_id").GetString());
        Assert.Equal("tsc_search_1", input[0].GetProperty("id").GetString());
        Assert.Equal("call_search_1", input[1].GetProperty("call_id").GetString());
        Assert.Equal("client", input[1].GetProperty("execution").GetString());
        Assert.Equal("completed", input[1].GetProperty("status").GetString());
        Assert.False(input[1].TryGetProperty("id", out _));
        var tool = Assert.Single(input[1].GetProperty("tools").EnumerateArray());
        Assert.Equal("read_resource", tool.GetProperty("name").GetString());
        Assert.Equal(read.JsonSchema.GetRawText(), tool.GetProperty("parameters").GetRawText());
    }

    [Fact]
    public async Task Tool_search_non_streaming_dispatches_registered_client_function()
    {
        using var http = CreateHttpClient(_ => CreateJsonResponse("""
            {"id":"resp_search","model":"openai/gpt-fixture","output":[
              {"type":"tool_search_call","id":"tsc_search_1","call_id":"call_search_1","execution":"client","status":"completed","arguments":{"goal":"read mail"}}
            ]}
            """));
        using var client = CreateClient(http, CreateAgent());
        var response = await client.GetResponseAsync(CreateUserMessages("read mail"));
        Assert.Equal("client_tool_search", Assert.Single(response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>()).Name);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("denied")]
    [InlineData("missing-selection")]
    [InlineData("malformed-selection")]
    [InlineData("unauthorized")]
    [InlineData("missing-call-id")]
    [InlineData("mismatched-call-id")]
    [InlineData("malformed-metadata")]
    [InlineData("conflicting-duplicate")]
    [InlineData("orphan-result")]
    public async Task Tool_search_rejects_invalid_or_failed_history_before_inference(string scenario)
    {
        var sent = 0;
        using var http = CreateHttpClient(_ => { sent++; return CreateJsonResponse(LoadFixture(StructuredFixturePath)); });
        using var client = CreateClient(http, CreateAgent());
        var read = AIFunctionFactory.Create((string uri) => "7", "read_resource");
        GetPrivateField<ConcurrentDictionary<string, AITool>>(client, "Tools")[read.Name] = read;
        var history = SearchHistory(SearchResult(),
            callId: scenario == "missing-call-id" ? "" : scenario == "mismatched-call-id" ? "call_wrong" : "call_search_1",
            nativeCallId: "call_search_1", approved: scenario != "denied");
        var part = Assert.IsType<ToolInvocationPart>(Assert.Single(history.Parts));
        switch (scenario)
        {
            case "failed": part.Output = new { isError = true, content = new[] { new { type = "text", text = "selection failed" } } }; break;
            case "missing-selection": part.Output = new { content = new[] { new { type = "text", text = "unexpected" } } }; break;
            case "malformed-selection": part.Output = new { selectedTools = new object[] { new { notAName = "read_resource" } } }; break;
            case "unauthorized": part.Output = SearchResult("delete_everything"); break;
            case "malformed-metadata": part.CallProviderMetadata!["openai"]!["responses_item"] = new { type = "tool_search_call", execution = "nonsense" }; break;
        }
        var messages = new[] { history }.ToMessages(["StructuredAgent"]).ToList();
        if (scenario == "conflicting-duplicate")
        {
            var duplicate = SearchHistory(SearchResult(), callId: "call_search_1");
            var duplicateMessages = new[] { duplicate }.ToMessages(["StructuredAgent"]).ToList();
            var call = duplicateMessages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Single();
            call.RawRepresentation = new Dictionary<string, object?>
            {
                ["responses_item"] = JsonSerializer.SerializeToElement(new { type = "tool_search_call", execution = "client", call_id = "call_search_1", arguments = new { goal = "different" } })
            };
            messages.AddRange(duplicateMessages);
        }
        if (scenario == "orphan-result")
            messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call_orphan", SearchResult())]));

        await Assert.ThrowsAnyAsync<Exception>(() => client.GetResponseAsync(messages));
        Assert.Equal(0, sent);
    }

    [Theory]
    [InlineData("OtherAgent", "openai", "openai/gpt-fixture")]
    [InlineData("StructuredAgent", "anthropic", "openai/gpt-fixture")]
    [InlineData("StructuredAgent", "openai", "anthropic/test")]
    public async Task Tool_search_omits_foreign_agent_or_provider_history(string owner, string provider, string model)
    {
        string body = "";
        using var http = CreateHttpClient(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return CreateJsonResponse(LoadFixture(StructuredFixturePath));
        });
        using var client = CreateClient(http, CreateAgent(modelId: model));
        var messages = new[] { SearchHistory(SearchResult(), owner: owner, provider: provider) }
            .ToMessages(["StructuredAgent", "OtherAgent"]).ToList();
        messages.AddRange(CreateUserMessages("Hello"));
        await client.GetResponseAsync(messages);
        Assert.DoesNotContain("call_search_1", body);
        Assert.DoesNotContain("selectedTools", body);
    }

    [Fact]
    public async Task Tool_search_preserves_empty_selection_and_deduplicates_identical_replay()
    {
        string body = "";
        using var http = CreateHttpClient(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return CreateJsonResponse(LoadFixture(StructuredFixturePath));
        });
        using var client = CreateClient(http, CreateAgent());
        var empty = new { structuredContent = new { selectedTools = Array.Empty<object>() }, isError = false };
        var messages = new[] { SearchHistory(empty), SearchHistory(empty) }.ToMessages(["StructuredAgent"]).ToList();
        await client.GetResponseAsync(messages);
        using var json = JsonDocument.Parse(body);
        var input = json.RootElement.GetProperty("input").EnumerateArray().ToArray();
        Assert.Equal(2, input.Length);
        Assert.Empty(input[1].GetProperty("tools").EnumerateArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tool_search_backend_loop_loads_executes_and_finishes_without_rediscovery(bool streaming)
    {
        var bodies = new List<JsonElement>();
        var executions = 0;
        using var http = CreateHttpClient(request =>
        {
            bodies.Add(JsonSerializer.Deserialize<JsonElement>(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()));
            return bodies.Count switch
            {
                1 => SearchModelResponse(streaming, new { type = "tool_search_call", id = "tsc_search_1", call_id = "call_search_1", execution = "client", status = "completed", arguments = new { goal = "read mail" } }),
                2 => SearchModelResponse(false, new { type = "message", id = "msg_selection", role = "assistant", content = new[] { new { type = "output_text", text = "{\"selectedToolNames\":[\"read_resource\"]}" } } }),
                3 => SearchModelResponse(streaming, new { type = "function_call", id = "fc_read", call_id = "call_read", name = "read_resource", arguments = "{\"uri\":\"mail://inbox\"}", status = "completed" }),
                4 => SearchModelResponse(streaming, new { type = "message", id = "msg_final", role = "assistant", content = new[] { new { type = "output_text", text = "You have 7 unread emails." } } }),
                _ => throw new InvalidOperationException("Unexpected redundant inference/search")
            };
        });
        using var client = CreateClient(http, CreateAgent());
        var tools = await client.ConnectMcp(default);
        var read = AIFunctionFactory.Create((string uri) => { executions++; return "7 unread emails"; }, "read_resource");
        tools.Add(read);
        GetPrivateField<ConcurrentDictionary<string, AITool>>(client, "Tools")[read.Name] = read;
        var agent = new ChatClientAgent(client, name: "StructuredAgent", tools: tools);
        if (streaming)
        {
            var updates = await CollectAsync(agent.RunStreamingAsync(CreateUserMessages("Count unread mail")));
            Assert.Contains(updates.SelectMany(u => u.Contents).OfType<TextContent>(), t => t.Text.Contains("7 unread"));
        }
        else
        {
            var response = await agent.RunAsync(CreateUserMessages("Count unread mail"));
            Assert.Contains("7 unread", response.Text);
        }
        Assert.Equal(1, executions);
        Assert.Equal(4, bodies.Count);
        Assert.Equal("none", bodies[1].GetProperty("tool_choice").GetString());
        Assert.Empty(bodies[1].GetProperty("tools").EnumerateArray());
        var loaded = Assert.Single(bodies[2].GetProperty("input").EnumerateArray(), i => i.GetProperty("type").GetString() == "tool_search_output");
        Assert.Equal("read_resource", Assert.Single(loaded.GetProperty("tools").EnumerateArray()).GetProperty("name").GetString());
        Assert.Contains(bodies[3].GetProperty("input").EnumerateArray(), i => i.GetProperty("type").GetString() == "function_call_output" && i.GetProperty("call_id").GetString() == "call_read");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Tool_search_ui_approval_continuation_executes_selected_tool_once(bool streaming, bool automaticApproval)
    {
        var bodies = new List<JsonElement>();
        var executions = 0;
        object? lastResult = null;
        using var http = CreateHttpClient(request =>
        {
            bodies.Add(JsonSerializer.Deserialize<JsonElement>(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()));
            return bodies.Count switch
            {
                1 => SearchModelResponse(streaming, new { type = "tool_search_call", id = "tsc_search_1", call_id = "call_search_1", execution = "client", status = "completed", arguments = new { goal = "read mail" } }),
                2 => SearchModelResponse(false, new { type = "message", id = "msg_selection", role = "assistant", content = new[] { new { type = "output_text", text = "{\"selectedToolNames\":[\"read_resource\"]}" } } }),
                3 => SearchModelResponse(streaming, new { type = "function_call", id = "fc_read", call_id = "call_read", name = "read_resource", arguments = "{\"uri\":\"mail://inbox\"}", status = "completed" }),
                4 => SearchModelResponse(streaming, new { type = "message", id = "msg_final", role = "assistant", content = new[] { new { type = "output_text", text = "You have 7 unread emails." } } }),
                _ => throw new InvalidOperationException("Unexpected rediscovery or duplicate execution")
            };
        });

        async Task<string> RunFreshStage(List<ChatMessage> history)
        {
            using var client = CreateClient(http, CreateAgent());
            var tools = await client.ConnectMcp(default);
            var read = AIFunctionFactory.Create((string uri) => { executions++; return "7 unread emails"; }, "read_resource");
            tools.Add(read);
            GetPrivateField<ConcurrentDictionary<string, AITool>>(client, "Tools")[read.Name] = read;
            var backend = new ChatClientAgent(client, name: "StructuredAgent", tools: tools);
            // Same batch suspension as the single-agent UI runtime; approval is
            // represented by a separate request with an existing result, never reinvocation.
            var runtime = backend.AsBuilder().Use(async (_, invocation, next, ct) =>
            {
                var result = await next(invocation, ct);
                lastResult = result;
                invocation.Terminate = true;
                return result;
            }).Build();
            if (!streaming) return (await runtime.RunAsync(history)).Text;
            var updates = await CollectAsync(runtime.RunStreamingAsync(history));
            return string.Concat(updates.SelectMany(u => u.Contents).OfType<TextContent>().Select(t => t.Text));
        }

        await RunFreshStage(CreateUserMessages("Count unread mail").ToList());
        Assert.Equal(2, bodies.Count);
        Assert.Equal(0, executions);
        Assert.NotNull(lastResult);
        var search = SearchHistory(lastResult!);
        if (!automaticApproval)
        {
            // While waiting for a human, neither model continuation nor the
            // selected Outlook tool has run. Approval then posts the same result.
            Assert.Equal(2, bodies.Count);
            Assert.Equal(0, executions);
        }
        await RunFreshStage(new[] { search }.ToMessages(["StructuredAgent"]).ToList());
        Assert.Equal(3, bodies.Count);
        Assert.Equal(1, executions);
        var readHistory = new UIMessage
        {
            Id = "read-history", Role = Role.assistant,
            Metadata = new Dictionary<string, object> { ["model"] = "StructuredAgent" },
            Parts = [new ToolInvocationPart
            {
                Type = "tool-read_resource", ToolCallId = "call_read", State = "approval-responded",
                Input = new { uri = "mail://inbox" }, Output = lastResult!, ProviderExecuted = false,
                Approval = new ToolApproval { Id = "call_read", Approved = true }
            }]
        };
        var answer = await RunFreshStage(new[] { search, readHistory }.ToMessages(["StructuredAgent"]).ToList());
        Assert.Contains("7 unread", answer);
        Assert.Equal(1, executions);
        Assert.Equal(4, bodies.Count);
        var loaded = Assert.Single(bodies[2].GetProperty("input").EnumerateArray(), i => i.GetProperty("type").GetString() == "tool_search_output");
        Assert.Equal("call_search_1", loaded.GetProperty("call_id").GetString());
        Assert.Single(loaded.GetProperty("tools").EnumerateArray());
    }

    [Fact]
    public async Task Tool_search_consecutive_searches_preserve_order_and_multiple_authorized_tools()
    {
        string body = "";
        using var http = CreateHttpClient(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return CreateJsonResponse(LoadFixture(StructuredFixturePath));
        });
        using var client = CreateClient(http, CreateAgent());
        var catalog = GetPrivateField<ConcurrentDictionary<string, AITool>>(client, "Tools");
        catalog["read_resource"] = AIFunctionFactory.Create((string uri) => "7", "read_resource");
        catalog["search_resources"] = AIFunctionFactory.Create((string query) => "mail://inbox", "search_resources");
        var both = new { selectedTools = new[] { new { name = "read_resource" }, new { name = "search_resources" }, new { name = "read_resource" } } };
        var messages = new[] { SearchHistory(both), SearchHistory(both, callId: "call_search_2") }.ToMessages(["StructuredAgent"]).ToList();
        await client.GetResponseAsync(messages);
        using var json = JsonDocument.Parse(body);
        var input = json.RootElement.GetProperty("input").EnumerateArray().ToArray();
        Assert.Equal(new[] { "tool_search_call", "tool_search_output", "tool_search_call", "tool_search_output" }, input.Select(i => i.GetProperty("type").GetString()));
        Assert.Equal("call_search_1", input[1].GetProperty("call_id").GetString());
        Assert.Equal("call_search_2", input[3].GetProperty("call_id").GetString());
        Assert.All(new[] { input[1], input[3] }, item => Assert.Equal(2, item.GetProperty("tools").GetArrayLength()));
    }

    [Fact]
    public async Task Tool_search_selection_cancellation_propagates_without_creating_empty_result()
    {
        using var http = CreateHttpClient(_ => throw new InvalidOperationException("No request expected"));
        using var client = CreateClient(http, CreateAgent());
        var tools = await client.ConnectMcp(default);
        var search = Assert.Single(tools.OfType<AIFunction>(), t => t.Name == "client_tool_search");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await search.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["goal"] = "read mail" }), cancellation.Token));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Tool_search_native_output_preserves_identity_but_cannot_inject_tools(bool hosted, bool unauthorized)
    {
        string body = "";
        using var http = CreateHttpClient(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return CreateJsonResponse(LoadFixture(StructuredFixturePath));
        });
        using var client = CreateClient(http, CreateAgent());
        var read = AIFunctionFactory.Create((string uri) => "7", "read_resource");
        GetPrivateField<ConcurrentDictionary<string, AITool>>(client, "Tools")[read.Name] = read;
        var lifecycle = hosted ? "tsc_hosted" : "call_search_1";
        var call = new ResponseToolSearchCallItem
        {
            Id = "tsc_native", CallId = hosted ? null : lifecycle,
            Execution = hosted ? "server" : "client", Status = "completed",
            Arguments = JsonSerializer.SerializeToElement(new { goal = "read mail" })
        };
        var output = new ResponseToolSearchOutputItem
        {
            Id = "tso_native", CallId = call.CallId, Execution = call.Execution, Status = "completed",
            Tools = [new ResponseToolDefinition
            {
                Type = "function", Extra = new()
                {
                    ["name"] = JsonSerializer.SerializeToElement(unauthorized ? "delete_everything" : "read_resource"),
                    ["parameters"] = JsonSerializer.SerializeToElement(new { type = "object", properties = new { forged = new { type = "string" } } })
                }
            }]
        };
        List<ChatMessage> messages =
        [
            new(ChatRole.Assistant, [new FunctionCallContent(lifecycle, hosted ? "tool_search" : "client_tool_search")
            { RawRepresentation = new { responses_item = call } }]),
            new(ChatRole.Tool, [new FunctionResultContent(lifecycle, new { responses_item = output })])
        ];
        if (unauthorized)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(messages));
            Assert.Empty(body);
            return;
        }
        await client.GetResponseAsync(messages);
        using var json = JsonDocument.Parse(body);
        var native = Assert.Single(json.RootElement.GetProperty("input").EnumerateArray(), i => i.GetProperty("type").GetString() == "tool_search_output");
        Assert.Equal("tso_native", native.GetProperty("id").GetString());
        Assert.Equal(call.CallId, native.TryGetProperty("call_id", out var nativeCallId) && nativeCallId.ValueKind == JsonValueKind.String ? nativeCallId.GetString() : null);
        Assert.Equal(call.Execution, native.GetProperty("execution").GetString());
        Assert.Equal(read.JsonSchema.GetRawText(), Assert.Single(native.GetProperty("tools").EnumerateArray()).GetProperty("parameters").GetRawText());
    }

    [Theory]
    [InlineData("legacy")]
    [InlineData("scoped")]
    [InlineData("nested-scoped")]
    public async Task Tool_search_normalizes_supported_native_metadata_formats(string format)
    {
        string body = "";
        using var http = CreateHttpClient(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return CreateJsonResponse(LoadFixture(StructuredFixturePath));
        });
        using var client = CreateClient(http, CreateAgent());
        var read = AIFunctionFactory.Create((string uri) => "7", "read_resource");
        GetPrivateField<ConcurrentDictionary<string, AITool>>(client, "Tools")[read.Name] = read;
        var messages = new[] { SearchHistory(SearchResult()) }.ToMessages(["StructuredAgent"]).ToList();
        var call = Assert.Single(messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>());
        var native = new { type = "tool_search_call", id = "tsc_search_1", call_id = "call_search_1", execution = "client", status = "completed", arguments = new { goal = "read mail" } };
        call.RawRepresentation = format switch
        {
            "legacy" => new { responses_type = "tool_search_call", item_id = "tsc_search_1", call_id = "call_search_1", execution = "client", status = "completed" },
            "scoped" => new { provider_metadata = new { openai = native } },
            _ => new { provider_metadata = new { openai = new { responses_item = native } } }
        };
        await client.GetResponseAsync(messages);
        using var json = JsonDocument.Parse(body);
        Assert.Single(json.RootElement.GetProperty("input").EnumerateArray(), i => i.GetProperty("type").GetString() == "tool_search_output");
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("{\"selectedToolNames\":[\"unauthorized\"]}")]
    public async Task Tool_search_malformed_selection_inference_returns_error_not_empty_success(string text)
    {
        using var http = CreateHttpClient(_ => SearchModelResponse(false,
            new { type = "message", id = "selection", role = "assistant", content = new[] { new { type = "output_text", text } } }));
        using var client = CreateClient(http, CreateAgent());
        var tools = await client.ConnectMcp(default);
        GetPrivateField<ConcurrentDictionary<string, AITool>>(client, "Tools")["read_resource"] = AIFunctionFactory.Create((string uri) => "7", "read_resource");
        var search = Assert.Single(tools.OfType<AIFunction>(), t => t.Name == "client_tool_search");
        var result = await search.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["goal"] = "read mail" }));
        var json = JsonSerializer.SerializeToElement(result);
        Assert.True(json.GetProperty("isError").GetBoolean());
        Assert.False(json.TryGetProperty("structuredContent", out var structured) && structured.ValueKind == JsonValueKind.Object);
    }

    [Fact]
    public async Task Tool_search_preserves_trusted_namespace_and_caller_restrictions()
    {
        string body = "";
        using var http = CreateHttpClient(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return CreateJsonResponse(LoadFixture(StructuredFixturePath));
        });
        using var client = CreateClient(http, CreateAgent());
        var read = AIFunctionFactory.Create((string uri) => "7", "read_resource");
        GetPrivateField<ConcurrentDictionary<string, AITool>>(client, "Tools")[read.Name] = read;
        var sourceType = typeof(AgentChatClient).GetNestedType("McpToolSource", System.Reflection.BindingFlags.NonPublic)!;
        var source = Activator.CreateInstance(sourceType, "outlook", new AgentHappey.Common.Models.McpServer
        {
            Url = TestMcpUrl, Namespace = true, DeferLoading = true, AllowedCallers = ["programmatic"]
        }, new ModelContextProtocol.Protocol.Implementation { Name = "Outlook", Version = "1" });
        var sources = (System.Collections.IDictionary)GetPrivateField<object>(client, "McpToolSources");
        sources[read.Name] = source;
        var messages = new[] { SearchHistory(SearchResult()) }.ToMessages(["StructuredAgent"]).ToList();
        await client.GetResponseAsync(messages);
        using var json = JsonDocument.Parse(body);
        var output = Assert.Single(json.RootElement.GetProperty("input").EnumerateArray(), i => i.GetProperty("type").GetString() == "tool_search_output");
        var ns = Assert.Single(output.GetProperty("tools").EnumerateArray());
        Assert.Equal("namespace", ns.GetProperty("type").GetString());
        Assert.Equal("outlook", ns.GetProperty("name").GetString());
        var member = Assert.Single(ns.GetProperty("tools").EnumerateArray());
        Assert.Equal("programmatic", Assert.Single(member.GetProperty("allowed_callers").EnumerateArray()).GetString());
        Assert.False(member.TryGetProperty("defer_loading", out _));
        Assert.Equal(read.JsonSchema.GetRawText(), member.GetProperty("parameters").GetRawText());
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("mismatched-output")]
    [InlineData("duplicate-native-id")]
    [InlineData("conflicting-results")]
    [InlineData("result-before-call")]
    public async Task Tool_search_native_identity_anomalies_fail_closed(string scenario)
    {
        var sent = 0;
        using var http = CreateHttpClient(_ => { sent++; return CreateJsonResponse(LoadFixture(StructuredFixturePath)); });
        using var client = CreateClient(http, CreateAgent());
        GetPrivateField<ConcurrentDictionary<string, AITool>>(client, "Tools")["read_resource"] = AIFunctionFactory.Create((string uri) => "7", "read_resource");
        var messages = new[] { SearchHistory(SearchResult()) }.ToMessages(["StructuredAgent"]).ToList();
        if (scenario == "duplicate-native-id")
        {
            var second = SearchHistory(SearchResult(), callId: "call_search_2");
            Assert.IsType<ToolInvocationPart>(Assert.Single(second.Parts)).CallProviderMetadata!["openai"]!["responses_item"] = new
            {
                type = "tool_search_call", id = "tsc_search_1", call_id = "call_search_2", execution = "client", status = "completed", arguments = new { goal = "read mail" }
            };
            messages.AddRange(new[] { second }.ToMessages(["StructuredAgent"]));
        }
        else if (scenario == "conflicting-results")
            messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call_search_1", new { selectedTools = Array.Empty<object>() })]));
        else if (scenario == "result-before-call") messages.Reverse();
        else
        {
            messages[1] = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call_search_1", new
            {
                responses_item = new ResponseToolSearchOutputItem
                {
                    Id = "tso_native", Execution = "client", CallId = scenario == "mismatched-output" ? "call_wrong" : "call_search_1",
                    Status = scenario == "incomplete" ? "incomplete" : "completed", Tools = []
                }
            })]);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(messages));
        Assert.Equal(0, sent);
    }

    [Fact]
    public async Task Tool_search_inflight_selection_cancellation_is_not_swallowed()
    {
        using var http = CreateHttpClient(_ => throw new OperationCanceledException("Selection cancelled"));
        using var client = CreateClient(http, CreateAgent());
        var tools = await client.ConnectMcp(default);
        GetPrivateField<ConcurrentDictionary<string, AITool>>(client, "Tools")["read_resource"] = AIFunctionFactory.Create((string uri) => "7", "read_resource");
        var search = Assert.Single(tools.OfType<AIFunction>(), t => t.Name == "client_tool_search");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await search.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["goal"] = "read mail" })));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task Tool_search_invalid_native_dispatch_is_rejected(bool streaming, bool missingCallId)
    {
        using var http = CreateHttpClient(_ => SearchModelResponse(streaming, new
        {
            type = "tool_search_call", id = "tsc_invalid", call_id = missingCallId ? null : "call_search",
            execution = "client", status = missingCallId ? "completed" : "incomplete", arguments = new { goal = "read mail" }
        }));
        using var client = CreateClient(http, CreateAgent());
        if (streaming)
            await Assert.ThrowsAsync<InvalidOperationException>(() => CollectAsync(client.GetStreamingResponseAsync(CreateUserMessages("read mail"))));
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(CreateUserMessages("read mail")));
    }

    private static System.Net.Http.HttpResponseMessage SearchModelResponse(bool streaming, params object[] output)
    {
        var response = new { id = "resp_search_test", model = "openai/gpt-fixture", status = "completed", output };
        if (!streaming) return CreateJsonResponse(JsonSerializer.Serialize(response));
        var events = new List<object>();
        for (var i = 0; i < output.Length; i++)
        {
            events.Add(new { type = "response.output_item.added", output_index = i, item = output[i] });
            events.Add(new { type = "response.output_item.done", output_index = i, item = output[i] });
        }
        events.Add(new { type = "response.completed", response });
        return CreateStreamingResponse(string.Join("", events.Select(e => $"data: {JsonSerializer.Serialize(e)}\n\n")) + "data: [DONE]\n\n");
    }

    private static object SearchResult(string name = "read_resource") => new
    {
        content = new[] { new { type = "text", text = "Selected tool" } },
        structuredContent = new { selectedTools = new[] { new { name, inputSchema = new { type = "object", properties = new { attacker = new { type = "string" } } } } } },
        isError = false
    };

    private static UIMessage SearchHistory(object output, bool wrapped = false, string owner = "StructuredAgent",
        string provider = "openai", string callId = "call_search_1", string? nativeCallId = null, bool approved = true) => new()
    {
        Id = "search-history",
        Role = Role.assistant,
        Metadata = new Dictionary<string, object> { ["model"] = owner },
        Parts =
        [
            new ToolInvocationPart
            {
                Type = "tool-client_tool_search", ToolCallId = callId,
                State = "approval-responded", ProviderExecuted = false,
                Input = new { goal = "read mail" },
                Output = wrapped ? new { output, provider_executed = false } : output,
                Approval = new ToolApproval { Id = callId, Approved = approved },
                CallProviderMetadata = new()
                {
                    [provider] = new()
                    {
                        ["type"] = "tool_search_call",
                        ["responses_item"] = JsonSerializer.SerializeToElement(new
                        {
                            type = "tool_search_call", id = callId == "call_search_1" ? "tsc_search_1" : $"tsc_{callId}", call_id = nativeCallId ?? callId,
                            execution = "client", status = "completed", arguments = new { goal = "read mail" }
                        })
                    }
                }
            }
        ]
    };
}
