using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.Abstractions.Http;
using AgentHappey.Common.Extensions;
using AgentHappey.Common.Models;
using AgentHappey.Core;
using AgentHappey.Core.ChatClient;
using AgentHappey.Core.Responses;
using AIHappey.Vercel.Models;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;
using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.AspNetCore.Http;

namespace AgentHappey.Tests;

public sealed class AgentChatClientFixtureTests
{
    private const string StreamingFixturePath = "Fixtures/responses/raw/basic-response-stream.jsonl";
    private const string StreamingGatewayFixturePath = "Fixtures/responses/raw/basic-response-stream-with-gateway.jsonl";
    private const string StructuredFixturePath = "Fixtures/responses/raw/structured-response-non-streaming.json";
    private const string GoogleEmptyReasoningFixturePath = "Fixtures/responses/raw/google-with-reasoning-responses-stream.jsonl";
    private const string OpenAiEmptyReasoningFixturePath = "Fixtures/responses/raw/openai-with-reasoning-responses-stream.jsonl";
    private const string OpenAiReasoningSummaryFixturePath = "Fixtures/responses/raw/openai-with-reasoning-summaries-responses-stream.jsonl";
    private const string OpenAiShellAndFileFixturePath = "Fixtures/responses/raw/openai-with-shell-calls-and-file-output-stream.jsonl";
    private const string OpenAiClientToolSearchFixturePath = "Fixtures/responses/raw/openai-client-tool-search-call-stream.jsonl";

    [Theory]
    [InlineData("https://backend.test/v1/", "https://backend.test/mcp", true)]
    [InlineData("https://backend.test:443/v1/", "https://backend.test/mcp", true)]
    [InlineData("http://localhost:5000/v1/", "http://localhost:5000/mcp", true)]
    [InlineData("http://localhost:5000/v1/", "http://localhost:5001/mcp", false)]
    [InlineData("https://backend.test/v1/", "https://other.test/mcp", false)]
    public void Mcp_server_is_same_inference_endpoint_when_host_and_port_match(string inferenceEndpoint, string mcpServerUrl, bool expected)
    {
        var result = InvokePrivateStatic<bool>(
            "IsSameEndpointAsInference",
            new Uri(inferenceEndpoint),
            mcpServerUrl);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Same_backend_mcp_server_reuses_inference_authorization_header()
    {
        using var inferenceClient = new HttpClient
        {
            BaseAddress = new Uri("https://backend.test/v1/")
        };
        inferenceClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "inference-token");

        using var mcpClient = new HttpClient();

        InvokePrivateStatic(
            "ApplyInferenceAuthorizationForSameEndpoint",
            inferenceClient,
            mcpClient,
            "https://backend.test/mcp");

        Assert.Equal("Bearer", mcpClient.DefaultRequestHeaders.Authorization?.Scheme);
        Assert.Equal("inference-token", mcpClient.DefaultRequestHeaders.Authorization?.Parameter);
    }

    [Fact]
    public void Different_backend_mcp_server_does_not_reuse_inference_authorization_header()
    {
        using var inferenceClient = new HttpClient
        {
            BaseAddress = new Uri("https://backend.test/v1/")
        };
        inferenceClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "inference-token");

        using var mcpClient = new HttpClient();

        InvokePrivateStatic(
            "ApplyInferenceAuthorizationForSameEndpoint",
            inferenceClient,
            mcpClient,
            "https://backend.test:8443/mcp");

        Assert.Null(mcpClient.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task Non_streaming_inference_request_sends_authoritative_agent_name_header()
    {
        var fixture = LoadFixture(StructuredFixturePath);
        string? agentNameHeader = null;

        using var httpClient = CreateHttpClient(request =>
        {
            agentNameHeader = request.Headers.TryGetValues("X-Agent-Name", out var values)
                ? Assert.Single(values)
                : null;

            return CreateJsonResponse(fixture);
        });

        using var client = new AgentChatClient(
            httpClient,
            new StaticHttpClientFactory(httpClient),
            CreateAgent(),
            new Dictionary<string, string?>
            {
                ["X-Agent-Name"] = "ClientSuppliedAgent"
            });

        await client.GetResponseAsync(CreateUserMessages("Say hello"));

        Assert.Equal("StructuredAgent", agentNameHeader);
    }

    [Fact]
    public async Task Streaming_inference_request_sends_agent_name_header()
    {
        var fixture = LoadFixture(StreamingFixturePath);
        string? agentNameHeader = null;

        using var httpClient = CreateHttpClient(request =>
        {
            agentNameHeader = request.Headers.TryGetValues("X-Agent-Name", out var values)
                ? Assert.Single(values)
                : null;

            return CreateStreamingResponse(fixture);
        });

        using var client = CreateClient(httpClient, CreateAgent());

        var updates = await CollectAsync(client.GetStreamingResponseAsync(CreateUserMessages("Say hello")));

        Assert.NotEmpty(updates);
        Assert.Equal("StructuredAgent", agentNameHeader);
    }

    [Fact]
    public async Task OpenAI_client_tool_search_call_is_emitted_for_agent_backend_execution()
    {
        var fixture = LoadFixture(OpenAiClientToolSearchFixturePath);

        using var httpClient = CreateHttpClient(_ => CreateStreamingResponse(fixture));
        using var client = CreateClient(httpClient, CreateAgent(modelId: "openai/gpt-fixture"));

        var updates = await CollectAsync(client.GetStreamingResponseAsync(CreateUserMessages("Tell me about Poland")));
        var functionCall = Assert.Single(updates
            .SelectMany(update => update.Contents)
            .OfType<FunctionCallContent>());

        Assert.Equal("call_tool_search_fixture_123", functionCall.CallId);
        Assert.Equal("client_tool_search", functionCall.Name);
        Assert.Equal("Get detailed information about Poland", functionCall.Arguments?["goal"]?.ToString());

        var raw = Assert.IsType<Dictionary<string, object?>>(functionCall.RawRepresentation);
        Assert.Equal("tool_search_call", raw["responses_type"]);
        Assert.Equal("tsc_fixture_123", raw["item_id"]);
        Assert.Equal("call_tool_search_fixture_123", raw["call_id"]);
        Assert.Equal("client", raw["execution"]);
    }

    [Fact]
    public async Task Non_streaming_inference_request_forwards_provider_headers_with_safe_precedence()
    {
        var fixture = LoadFixture(StructuredFixturePath);
        HttpRequestMessage? capturedRequest = null;

        using var httpClient = CreateHttpClient(request =>
        {
            capturedRequest = request;
            return CreateJsonResponse(fixture);
        });
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("X-Provider-Route", "client-default");

        using var client = CreateClient(httpClient, CreateAgent(providerHeaders: new(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Provider-Route"] = "agent-route",
            ["Authorization"] = "Bearer provider-token",
            ["Accept"] = "text/plain",
            ["Content-Type"] = "text/plain",
            ["X-Agent-Name"] = "ProviderSuppliedAgent"
        }));

        await client.GetResponseAsync(CreateUserMessages("Say hello"));

        Assert.NotNull(capturedRequest);
        Assert.Equal("agent-route", Assert.Single(capturedRequest.Headers.GetValues("X-Provider-Route")));
        Assert.Equal("Bearer provider-token", Assert.Single(capturedRequest.Headers.GetValues("Authorization")));
        Assert.Equal("application/json", Assert.Single(capturedRequest.Headers.Accept).MediaType);
        Assert.Equal("application/json", capturedRequest.Content?.Headers.ContentType?.MediaType);
        Assert.Equal("StructuredAgent", Assert.Single(capturedRequest.Headers.GetValues("X-Agent-Name")));
    }

    [Fact]
    public async Task Streaming_inference_request_forwards_provider_headers()
    {
        var fixture = LoadFixture(StreamingFixturePath);
        string? providerHeader = null;

        using var httpClient = CreateHttpClient(request =>
        {
            providerHeader = request.Headers.TryGetValues("X-Provider-Stream", out var values)
                ? Assert.Single(values)
                : null;

            return CreateStreamingResponse(fixture);
        });
        using var client = CreateClient(httpClient, CreateAgent(providerHeaders: new()
        {
            ["X-Provider-Stream"] = "enabled"
        }));

        var updates = await CollectAsync(client.GetStreamingResponseAsync(CreateUserMessages("Say hello")));

        Assert.NotEmpty(updates);
        Assert.Equal("enabled", providerHeader);
    }

    [Fact]
    public async Task Provider_headers_do_not_persist_on_shared_http_client()
    {
        var fixture = LoadFixture(StructuredFixturePath);
        var capturedValues = new List<string?>();

        using var httpClient = CreateHttpClient(request =>
        {
            capturedValues.Add(request.Headers.TryGetValues("X-Provider-Scoped", out var values)
                ? Assert.Single(values)
                : null);
            return CreateJsonResponse(fixture);
        });
        using var providerClient = CreateClient(httpClient, CreateAgent(providerHeaders: new()
        {
            ["X-Provider-Scoped"] = "first-agent-only"
        }));
        using var plainClient = CreateClient(httpClient, CreateAgent());

        await providerClient.GetResponseAsync(CreateUserMessages("First request"));
        await plainClient.GetResponseAsync(CreateUserMessages("Second request"));

        Assert.Equal(["first-agent-only", null], capturedValues);
        Assert.False(httpClient.DefaultRequestHeaders.Contains("X-Provider-Scoped"));
    }

    [Fact]
    public async Task Assistant_reasoning_is_sent_before_assistant_text_when_ui_part_order_has_reasoning_first()
    {
        var requestBody = await CaptureRequestBodyAsync(
            [new UIMessage
            {
                Id = "assistant-1",
                Role = AIHappey.Vercel.Models.Role.assistant,
                Parts =
                [
                    new ReasoningUIPart
                    {
                        Id = "reasoning-1",
                        Text = "Visible reasoning summary",
                        ProviderMetadata = new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            ["StructuredAgent"] = JsonSerializer.SerializeToElement(new
                            {
                                encrypted_content = "encrypted-payload"
                            }, JsonSerializerOptions.Web)
                        }
                    },
                    new TextUIPart { Text = "Final assistant response" }
                ]
            }],
            activeAgentNames: ["StructuredAgent"]);

        using var document = JsonDocument.Parse(requestBody);
        var input = document.RootElement.GetProperty("input").EnumerateArray().ToList();

        var reasoningIndex = input.FindIndex(item => item.GetProperty("type").GetString() == "reasoning");
        var assistantMessageIndex = input.FindIndex(item =>
            item.GetProperty("type").GetString() == "message"
            && item.GetProperty("role").GetString() == "assistant");

        Assert.InRange(reasoningIndex, 0, input.Count - 1);
        Assert.InRange(assistantMessageIndex, 0, input.Count - 1);
        Assert.True(reasoningIndex < assistantMessageIndex);
        Assert.Equal("encrypted-payload", input[reasoningIndex].GetProperty("encrypted_content").GetString());
        Assert.Contains("Final assistant response", input[assistantMessageIndex].GetRawText());
    }

    [Fact]
    public async Task Assistant_tool_invocations_are_sent_as_interleaved_function_calls_and_outputs()
    {
        var requestBody = await CaptureRequestBodyAsync(
            [new UIMessage
            {
                Id = "assistant-1",
                Role = AIHappey.Vercel.Models.Role.assistant,
                Parts =
                [
                    new ToolInvocationPart
                    {
                        ToolCallId = "call-1",
                        Type = "tool-get_weather",
                        Input = new { city = "Amsterdam" },
                        Output = new { temperature = 18 },
                        State = "output-available"
                    },
                    new ToolInvocationPart
                    {
                        ToolCallId = "call-2",
                        Type = "tool-get_time",
                        Input = new { timezone = "Europe/Amsterdam" },
                        Output = new { hour = 16 },
                        State = "output-available"
                    }
                ]
            }],
            activeAgentNames: ["StructuredAgent"]);

        using var document = JsonDocument.Parse(requestBody);
        var input = document.RootElement.GetProperty("input").EnumerateArray().ToList();

        var functionItems = input
            .Where(item => item.TryGetProperty("type", out var type)
                && (type.GetString() == "function_call" || type.GetString() == "function_call_output"))
            .ToList();

        Assert.Equal(4, functionItems.Count);
        AssertFunctionItem(functionItems[0], "function_call", "call-1");
        AssertFunctionItem(functionItems[1], "function_call_output", "call-1");
        AssertFunctionItem(functionItems[2], "function_call", "call-2");
        AssertFunctionItem(functionItems[3], "function_call_output", "call-2");
    }

    [Fact]
    public async Task Programmatic_function_result_replays_complete_program_caller_graph()
    {
        const string programItemId = "prog-item-1";
        const string programCallId = "prog-call-1";
        const string functionItemId = "function-item-1";
        const string functionCallId = "function-call-1";
        var responseJson = JsonSerializer.Serialize(new
        {
            id = "response-programmatic-1",
            @object = "response",
            created_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            status = "completed",
            model = "gpt-fixture",
            output = new object[]
            {
                new
                {
                    id = programItemId,
                    type = "program",
                    call_id = programCallId,
                    code = "const result = await tools.github_rest_countries_get_detail({ cca: 'PL' });",
                    fingerprint = "program-fingerprint-1"
                },
                new
                {
                    id = functionItemId,
                    type = "function_call",
                    call_id = functionCallId,
                    name = "github_rest_countries_get_detail",
                    arguments = "{\"cca\":\"PL\"}",
                    status = "completed",
                    caller = new { type = "program", caller_id = programItemId }
                },
            },
            tools = Array.Empty<object>()
        }, JsonSerializerOptions.Web);

        var requestBodies = new List<string>();
        using var httpClient = CreateHttpClient(request =>
        {
            requestBodies.Add(request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty);
            return CreateJsonResponse(responseJson);
        });
        using var client = CreateClient(httpClient, CreateAgent());

        var firstResponse = await client.GetResponseAsync(CreateUserMessages("Look up Poland"));
        var functionCall = Assert.Single(firstResponse.Messages.Single().Contents.OfType<FunctionCallContent>());

        var messages = CreateUserMessages("Look up Poland").ToList();
        messages.Add(firstResponse.Messages.Single());
        messages.Add(new ChatMessage(ChatRole.Tool,
        [
            new FunctionResultContent(functionCall.CallId, new { name = "Poland", cca = "PL" })
        ]));

        await client.GetResponseAsync(messages);

        using var document = JsonDocument.Parse(requestBodies[1]);
        var input = document.RootElement.GetProperty("input").EnumerateArray().ToList();
        var programIndex = input.FindIndex(item => item.GetProperty("type").GetString() == "program");
        var functionCallIndex = input.FindIndex(item => item.GetProperty("type").GetString() == "function_call");
        var functionOutputIndex = input.FindIndex(item => item.GetProperty("type").GetString() == "function_call_output");

        Assert.True(programIndex < functionCallIndex);
        Assert.True(functionCallIndex < functionOutputIndex);
        Assert.DoesNotContain(input, item => item.GetProperty("type").GetString() == "program_output");

        var program = input[programIndex];
        Assert.Equal(programItemId, program.GetProperty("id").GetString());
        Assert.Equal(programCallId, program.GetProperty("call_id").GetString());
        Assert.Equal("program-fingerprint-1", program.GetProperty("fingerprint").GetString());

        AssertProgramCaller(input[functionCallIndex], programItemId);
        AssertProgramCaller(input[functionOutputIndex], programItemId);
        Assert.Equal(functionItemId, input[functionCallIndex].GetProperty("id").GetString());
        Assert.Equal("completed", input[functionCallIndex].GetProperty("status").GetString());
        Assert.Equal(functionCallId, input[functionOutputIndex].GetProperty("call_id").GetString());
    }

    [Fact]
    public async Task Microsoft_agent_programmatic_tool_loop_replays_caller_on_executed_result()
    {
        const string programItemId = "prog-item-agent-loop";
        const string programCallId = "prog-call-agent-loop";
        const string functionCallId = "function-call-agent-loop";
        var requestBodies = new List<string>();
        var requestNumber = 0;

        using var httpClient = CreateHttpClient(request =>
        {
            requestBodies.Add(request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty);
            requestNumber++;

            if (requestNumber == 1)
            {
                return CreateStreamingResponse(CreateProgrammaticToolCallStream(
                    programItemId,
                    programCallId,
                    functionCallId));
            }

            return CreateStreamingResponse(CreateTextResponseStream("Poland uses PLN."));
        });
        using var client = CreateClient(httpClient, CreateAgent());
        var tool = AIFunctionFactory.Create(
            ([System.ComponentModel.Description("Country code")] string cca) => new { cca, currency = "PLN" },
            "github_rest_countries_get_detail");
        var agent = new ChatClientAgent(
            client,
            instructions: "Use the tool.",
            name: "ProgrammaticAgent",
            tools: [tool]);
        var options = new ChatClientAgentRunOptions(new ChatOptions { Tools = [tool] });

        _ = await CollectAsync(agent.RunStreamingAsync(
            CreateUserMessages("Look up Poland"),
            options: options));

        Assert.Equal(2, requestBodies.Count);
        using var document = JsonDocument.Parse(requestBodies[1]);
        var input = document.RootElement.GetProperty("input").EnumerateArray().ToList();
        var programIndex = input.FindIndex(item => item.GetProperty("type").GetString() == "program");
        var functionCallIndex = input.FindIndex(item => item.GetProperty("type").GetString() == "function_call");
        var functionOutputIndex = input.FindIndex(item => item.GetProperty("type").GetString() == "function_call_output");

        Assert.True(programIndex >= 0, requestBodies[1]);
        Assert.True(functionCallIndex > programIndex, requestBodies[1]);
        Assert.True(functionOutputIndex > functionCallIndex, requestBodies[1]);
        Assert.DoesNotContain(input, item => item.GetProperty("type").GetString() == "program_output");
        AssertProgramCaller(input[functionCallIndex], programItemId);
        AssertProgramCaller(input[functionOutputIndex], programItemId);
        Assert.Equal("function-item-agent-loop", input[functionCallIndex].GetProperty("id").GetString());
        Assert.Equal("completed", input[functionCallIndex].GetProperty("status").GetString());
        Assert.Equal(functionCallId, input[functionOutputIndex].GetProperty("call_id").GetString());
    }

    private static string CreateProgrammaticToolCallStream(
        string programItemId,
        string programCallId,
        string functionCallId)
    {
        var response = new
        {
            id = "response-programmatic-agent-loop",
            @object = "response",
            created_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            status = "completed",
            model = "gpt-fixture",
            output = Array.Empty<object>(),
            tools = Array.Empty<object>()
        };
        var program = new
        {
            id = programItemId,
            type = "program",
            call_id = programCallId,
            code = "const result = await tools.github_rest_countries_get_detail({ cca: 'PL' });",
            fingerprint = "program-fingerprint-agent-loop"
        };
        var addedCall = new
        {
            id = "function-item-agent-loop",
            type = "function_call",
            call_id = functionCallId,
            name = "github_rest_countries_get_detail",
            arguments = string.Empty,
            status = "in_progress"
        };
        var completedCall = new
        {
            id = "function-item-agent-loop",
            type = "function_call",
            call_id = functionCallId,
            name = "github_rest_countries_get_detail",
            arguments = "{\"cca\":\"PL\"}",
            status = "completed",
            caller = new { type = "program", caller_id = programItemId }
        };

        return string.Join("\n\n", new[]
        {
            Sse("response.created", new { type = "response.created", sequence_number = 1, response }),
            Sse("response.output_item.added", new { type = "response.output_item.added", sequence_number = 2, output_index = 0, item = program }),
            Sse("response.output_item.done", new { type = "response.output_item.done", sequence_number = 3, output_index = 0, item = program }),
            Sse("response.output_item.added", new { type = "response.output_item.added", sequence_number = 4, output_index = 1, item = addedCall }),
            Sse("response.function_call_arguments.done", new { type = "response.function_call_arguments.done", sequence_number = 5, output_index = 1, item_id = addedCall.id, arguments = completedCall.arguments }),
            Sse("response.output_item.done", new { type = "response.output_item.done", sequence_number = 6, output_index = 1, item = completedCall }),
            Sse("response.completed", new { type = "response.completed", sequence_number = 7, response }),
            "data: [DONE]"
        }) + "\n\n";
    }

    private static string CreateTextResponseStream(string text)
    {
        var response = new
        {
            id = "response-text-agent-loop",
            @object = "response",
            created_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            status = "completed",
            model = "gpt-fixture",
            output = Array.Empty<object>(),
            tools = Array.Empty<object>()
        };

        return string.Join("\n\n", new[]
        {
            Sse("response.created", new { type = "response.created", sequence_number = 1, response }),
            Sse("response.output_text.delta", new { type = "response.output_text.delta", sequence_number = 2, item_id = "message-agent-loop", content_index = 0, output_index = 0, delta = text }),
            Sse("response.output_text.done", new { type = "response.output_text.done", sequence_number = 3, item_id = "message-agent-loop", content_index = 0, output_index = 0, text }),
            Sse("response.completed", new { type = "response.completed", sequence_number = 4, response }),
            "data: [DONE]"
        }) + "\n\n";
    }

    private static string CreateChunkedTextResponseStream(string eventPrefix, IReadOnlyList<string> chunks)
    {
        var response = new
        {
            id = "response-chunked-text",
            @object = "response",
            created_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            status = "completed",
            model = "gpt-fixture",
            output = Array.Empty<object>(),
            tools = Array.Empty<object>()
        };
        var itemId = "chunked-text-item";
        var events = new List<string>
        {
            Sse("response.created", new { type = "response.created", sequence_number = 1, response })
        };

        for (var index = 0; index < chunks.Count; index++)
        {
            var delta = chunks[index];
            var type = $"response.{eventPrefix}.delta";
            events.Add(Sse(type, new
            {
                type,
                sequence_number = index + 2,
                item_id = itemId,
                content_index = 0,
                output_index = 0,
                summary_index = 0,
                delta
            }));
        }

        var fullText = string.Concat(chunks);
        var doneType = $"response.{eventPrefix}.done";
        events.Add(Sse(doneType, new
        {
            type = doneType,
            sequence_number = chunks.Count + 2,
            item_id = itemId,
            content_index = 0,
            output_index = 0,
            summary_index = 0,
            text = fullText
        }));
        events.Add(Sse("response.completed", new
        {
            type = "response.completed",
            sequence_number = chunks.Count + 3,
            response
        }));
        events.Add("data: [DONE]");

        return string.Join("\n\n", events) + "\n\n";
    }

    private static string Sse(string eventName, object data)
        => $"event: {eventName}\ndata: {JsonSerializer.Serialize(data, JsonSerializerOptions.Web)}";

    private static void AssertProgramCaller(JsonElement item, string callerId)
    {
        var caller = item.GetProperty("caller");
        Assert.Equal("program", caller.GetProperty("type").GetString());
        Assert.Equal(callerId, caller.GetProperty("caller_id").GetString());
    }

    [Fact]
    public void Composed_instructions_include_chat_shaped_mcp_server_blocks()
    {
        using var httpClient = CreateHttpClient(_ => CreateJsonResponse(LoadFixture(StructuredFixturePath)));
        using var client = CreateClient(httpClient, CreateAgent());

        SeedMcpMetadata(client);

        var instructions = client.GetComposedInstructions();
        var block = ExtractSingleMcpInstructionBlock(instructions);

        var server = block.GetProperty("modelContextProtocolServer");
        Assert.Equal("example-mcp", server.GetProperty("name").GetString());
        Assert.Equal("1.0.0", server.GetProperty("version").GetString());
        Assert.Equal(TestMcpUrl, server.GetProperty("mcpServerUrl").GetString());
        Assert.Equal("Example MCP", server.GetProperty("title").GetString());
        Assert.Equal("https://mcp.example.com", server.GetProperty("websiteUrl").GetString());

        Assert.Equal("Use the MCP resources before answering.", block.GetProperty("instructions").GetString());

        var resources = block.GetProperty("resources").EnumerateArray().ToList();
        Assert.Single(resources);
        Assert.Equal("Policy", resources[0].GetProperty("name").GetString());
        Assert.Equal("file://policy.md", resources[0].GetProperty("uri").GetString());
        Assert.Equal("text/markdown", resources[0].GetProperty("mimeType").GetString());
        Assert.Equal(42, resources[0].GetProperty("size").GetInt64());
        Assert.Equal("high", resources[0].GetProperty("annotations").GetProperty("priority").GetString());
        Assert.Equal("2026-04-28T00:00:00Z", resources[0].GetProperty("annotations").GetProperty("lastModified").GetString());

        var templates = block.GetProperty("resourceTemplates").EnumerateArray().ToList();
        Assert.Single(templates);
        Assert.Equal("Ticket", templates[0].GetProperty("name").GetString());
        Assert.Equal("ticket://{id}", templates[0].GetProperty("uriTemplate").GetString());
        Assert.Equal("application/json", templates[0].GetProperty("mimeType").GetString());
        Assert.Equal("high", templates[0].GetProperty("annotations").GetProperty("priority").GetString());
    }

    [Fact]
    public async Task Composed_mcp_instructions_are_sent_in_responses_request_instructions_field()
    {
        var fixture = LoadFixture(StructuredFixturePath);
        string requestBody = string.Empty;

        using var httpClient = CreateHttpClient(request =>
        {
            requestBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            return CreateJsonResponse(fixture);
        });

        using var client = CreateClient(httpClient, CreateAgent(modelId: "openai/gpt-fixture"));
        SeedMcpMetadata(client);

        await client.GetResponseAsync(
            CreateUserMessages("Use MCP"),
            new ChatOptions { Instructions = client.GetComposedInstructions() });

        using var document = JsonDocument.Parse(requestBody);
        var instructions = document.RootElement.GetProperty("instructions").GetString();

        Assert.False(string.IsNullOrWhiteSpace(instructions));

        var block = ExtractSingleMcpInstructionBlock(instructions!);
        Assert.Equal(TestMcpUrl, block.GetProperty("modelContextProtocolServer").GetProperty("mcpServerUrl").GetString());
        Assert.Equal("Use the MCP resources before answering.", block.GetProperty("instructions").GetString());
        Assert.Single(block.GetProperty("resources").EnumerateArray());
        Assert.Single(block.GetProperty("resourceTemplates").EnumerateArray());
    }

    [Fact]
    public async Task Streaming_responses_are_captured_when_configured_in_agent_provider_metadata()
    {
        var captureRoot = Path.Combine(Path.GetTempPath(), "agenthappey-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(captureRoot);

        ProviderBackendCapture.Configure(new ProviderBackendCaptureOptions
        {
            Enabled = true,
            DevelopmentOnly = false,
            RootDirectory = captureRoot
        });

        try
        {
            var fixture = LoadFixture(StreamingFixturePath);

            using var httpClient = CreateHttpClient(_ => CreateStreamingResponse(fixture));
            using var client = CreateClient(
                httpClient,
                CreateAgent(new Dictionary<string, object>
                {
                    ["capture"] = new Dictionary<string, object?>
                    {
                        ["enabled"] = true,
                        ["relativeDirectory"] = "agents/tests",
                        ["fileName"] = "basic-response-stream"
                    }
                }));

            var updates = await CollectAsync(client.GetStreamingResponseAsync(CreateUserMessages("Say hello")));

            Assert.NotEmpty(updates);

            var capturePath = Path.Combine(captureRoot, "agents", "tests", "basic-response-stream.jsonl");
            Assert.True(File.Exists(capturePath));

            var captured = await File.ReadAllTextAsync(capturePath);
            Assert.Contains("event: response.created", captured);
            Assert.Contains("data: [DONE]", captured);
        }
        finally
        {
            ProviderBackendCapture.Disable();

            if (Directory.Exists(captureRoot))
                Directory.Delete(captureRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Streaming_text_response_roundtrips_to_text_ui_part()
    {
        var fixture = LoadFixture(StreamingFixturePath);

        using var httpClient = CreateHttpClient(_ => CreateStreamingResponse(fixture));
        using var client = CreateClient(httpClient, CreateAgent());

        var updates = await CollectAsync(client.GetStreamingResponseAsync(CreateUserMessages("Say hello")));

        var textContent = updates
            .SelectMany(update => update.Contents)
            .OfType<TextContent>()
            .Single(content => content.Text == "Hello world");

        var uiPart = Assert.IsType<TextUIPart>(textContent.ToUiPart());

        Assert.Equal("Hello world", uiPart.Text);
        Assert.Contains(updates, update => update.FinishReason is not null);
    }

    [Fact]
    public async Task Streaming_output_preserves_every_non_empty_chunk_exactly()
    {
        string[] chunks =
        [
            "Intro", "\n", "# Heading", "\n", "> Quote", "\n", "- List item",
            "\n", "---", "\n", "\tindented", " ", string.Empty
        ];
        var fixture = CreateChunkedTextResponseStream("output_text", chunks);

        using var httpClient = CreateHttpClient(_ => CreateStreamingResponse(fixture));
        using var client = CreateClient(httpClient, CreateAgent());

        var updates = await CollectAsync(client.GetStreamingResponseAsync(CreateUserMessages("Preserve formatting")));
        var emitted = updates
            .SelectMany(update => update.Contents)
            .OfType<TextContent>()
            .Select(content => content.Text)
            .ToList();

        Assert.Equal(chunks.Where(chunk => chunk.Length > 0), emitted);
        Assert.Equal(string.Concat(chunks), string.Concat(emitted));
    }

    [Theory]
    [InlineData("reasoning_text")]
    [InlineData("reasoning_summary_text")]
    public async Task Streaming_reasoning_preserves_whitespace_only_chunks(string eventPrefix)
    {
        string[] chunks = ["Think", "\n", " ", "\t", "again", string.Empty];
        var fixture = CreateChunkedTextResponseStream(eventPrefix, chunks);

        using var httpClient = CreateHttpClient(_ => CreateStreamingResponse(fixture));
        using var client = CreateClient(httpClient, CreateAgent());

        var updates = await CollectAsync(client.GetStreamingResponseAsync(CreateUserMessages("Think")));
        var emitted = updates
            .SelectMany(update => update.Contents)
            .OfType<TextReasoningContent>()
            .Select(content => content.Text)
            .ToList();

        Assert.Equal(chunks.Where(chunk => chunk.Length > 0), emitted);
        Assert.Equal(string.Concat(chunks), string.Concat(emitted));
    }

    [Fact]
    public async Task Streaming_completed_response_gateway_metadata_roundtrips_to_finish_ui_part()
    {
        var uiParts = await CollectUiPartsAsync(StreamingGatewayFixturePath, "openai/gpt-fixture");

        var finishPart = Assert.IsType<FinishUIPart>(Assert.Single(uiParts.OfType<FinishUIPart>()));
        Assert.NotNull(finishPart.MessageMetadata?.Gateway);
        Assert.Equal(0.12345m, finishPart.MessageMetadata.Gateway.Cost);

        var gateway = finishPart.MessageMetadata.Gateway.ToDictionary();
        Assert.Equal("EUR", Assert.IsType<JsonElement>(gateway["currency"]).GetString());
        Assert.Equal("fixture", Assert.IsType<JsonElement>(gateway["provider"]).GetString());
    }

    [Fact]
    public async Task Streaming_completed_response_gateway_metadata_roundtrips_to_final_update()
    {
        var fixture = LoadFixture(StreamingGatewayFixturePath);

        using var httpClient = CreateHttpClient(_ => CreateStreamingResponse(fixture));
        using var client = CreateClient(httpClient, CreateAgent(modelId: "openai/gpt-fixture"));

        var updates = await CollectAsync(client.GetStreamingResponseAsync(CreateUserMessages("Say hello")));
        var finalUpdate = Assert.Single(updates, update => update.FinishReason is not null);
        var finishMetadataContent = Assert.Single(finalUpdate.Contents.OfType<DataContent>(), content => content.Name == "finish_metadata");

        var metadata = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            Encoding.UTF8.GetString(finishMetadataContent.Data!.Span),
            JsonSerializerOptions.Web);

        Assert.NotNull(metadata);
        Assert.Equal(0.12345m, metadata!["gateway"].GetProperty("cost").GetDecimal());
        Assert.Equal("EUR", metadata["gateway"].GetProperty("currency").GetString());
        Assert.Equal("fixture", metadata["gateway"].GetProperty("provider").GetString());
    }

    [Fact]
    public void Responses_mapper_uses_resolved_agent_name_as_response_model()
    {
        var mapper = new ResponsesNativeMapper();
        var result = mapper.Map(
            new ResponseRequest
            {
                Model = "openai/gpt-fixture",
                Input = new ResponseInput("Say hello")
            },
            "StructuredAgent",
            "openai",
            new AgentResponse([new ChatMessage(ChatRole.Assistant, "Hello world")]));

        Assert.Equal("StructuredAgent", result.Model);
    }

    [Fact]
    public void Responses_mapper_adds_provider_metadata_with_provider_key()
    {
        var mapper = new ResponsesNativeMapper();
        var result = mapper.Map(
            new ResponseRequest
            {
                Model = "openai/gpt-fixture",
                Input = new ResponseInput("Say hello")
            },
            "StructuredAgent",
            "openai",
            new AgentResponse([new ChatMessage(ChatRole.Assistant, "Hello world")]));

        Assert.NotNull(result.Metadata);
        var providerMetadata = Assert.IsType<Dictionary<string, object?>>(result.Metadata!["providerMetadata"]);
        Assert.Empty(Assert.IsType<Dictionary<string, object?>>(providerMetadata["openai"]));
    }

    [Fact]
    public void Responses_mapper_sums_gateway_cost_from_finish_metadata()
    {
        var mapper = new ResponsesNativeMapper();
        var result = mapper.Map(
            new ResponseRequest
            {
                Model = "openai/gpt-fixture",
                Input = new ResponseInput("Say hello"),
                Metadata = new Dictionary<string, object?>
                {
                    ["gateway"] = new Dictionary<string, object?>
                    {
                        ["cost"] = 0.10m,
                        ["currency"] = "EUR"
                    }
                }
            },
            "StructuredAgent",
            "openai",
            new AgentResponse([new ChatMessage(ChatRole.Assistant, [CreateFinishMetadataContent(0.23m, "EUR", "fixture")])]));

        Assert.NotNull(result.Metadata);
        var gateway = GetProviderMetadataGateway(result.Metadata!);
        Assert.Equal(0.33m, gateway["cost"]);
        Assert.Equal("EUR", Assert.IsType<JsonElement>(gateway["currency"]).GetString());
        Assert.Equal("fixture", Assert.IsType<JsonElement>(gateway["provider"]).GetString());
        Assert.False(result.Metadata!.ContainsKey("gateway"));
    }

    [Fact]
    public void Responses_mapper_prefers_provider_metadata_gateway_over_direct_gateway()
    {
        var mapper = new ResponsesNativeMapper();
        var result = mapper.Map(
            new ResponseRequest
            {
                Model = "openai/gpt-fixture",
                Input = new ResponseInput("Say hello")
            },
            "StructuredAgent",
            "openai",
            new AgentResponse([new ChatMessage(ChatRole.Assistant, [CreateProviderMetadataGatewayContent(
                providerGatewayCost: 0.45m,
                directGatewayCost: 0.01m,
                currency: "EUR",
                provider: "fixture")])]));

        Assert.NotNull(result.Metadata);
        var gateway = GetProviderMetadataGateway(result.Metadata!);
        Assert.Equal(0.45m, gateway["cost"]);
        Assert.Equal("EUR", Assert.IsType<JsonElement>(gateway["currency"]).GetString());
        Assert.Equal("fixture", Assert.IsType<JsonElement>(gateway["provider"]).GetString());
        Assert.False(result.Metadata!.ContainsKey("gateway"));
    }

    [Fact]
    public void Responses_mapper_sums_provider_metadata_gateway_for_background_compatible_non_streaming_result()
    {
        var mapper = new ResponsesNativeMapper();
        var result = mapper.Map(
            new ResponseRequest
            {
                Model = "openai/gpt-fixture",
                Input = new ResponseInput("Say hello"),
                Background = true,
                Stream = false,
                Metadata = new Dictionary<string, object?>
                {
                    ["gateway"] = new Dictionary<string, object?>
                    {
                        ["cost"] = 0.10m,
                        ["currency"] = "EUR"
                    }
                }
            },
            "StructuredAgent",
            "openai",
            new AgentResponse([new ChatMessage(ChatRole.Assistant, [CreateProviderMetadataGatewayContent(
                providerGatewayCost: 0.45m,
                directGatewayCost: 0.01m,
                currency: "EUR",
                provider: "fixture")])]));

        Assert.NotNull(result.Metadata);
        var gateway = GetProviderMetadataGateway(result.Metadata!);
        Assert.Equal(0.55m, gateway["cost"]);
        Assert.Equal("fixture", Assert.IsType<JsonElement>(gateway["provider"]).GetString());
        Assert.False(result.Metadata!.ContainsKey("gateway"));
    }

    [Fact]
    public async Task Non_streaming_inference_response_metadata_roundtrips_to_finish_metadata_content()
    {
        var responseJson = JsonSerializer.Serialize(new
        {
            id = "resp_fixture",
            @object = "response",
            created_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            status = "completed",
            model = "openai/gpt-fixture",
            output = Array.Empty<object>(),
            metadata = new
            {
                gateway = new
                {
                    cost = 0.016693800m
                }
            }
        }, ResponseJson.Default);

        using var httpClient = CreateHttpClient(_ => CreateJsonResponse(responseJson));
        using var client = CreateClient(httpClient, CreateAgent());

        var response = await client.GetResponseAsync(CreateUserMessages("Say hello"));
        var finishMetadataContent = Assert.Single(response.Messages.Single().Contents.OfType<DataContent>(), content => content.Name == "finish_metadata");
        var metadata = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            Encoding.UTF8.GetString(finishMetadataContent.Data!.Span),
            JsonSerializerOptions.Web);

        Assert.NotNull(metadata);
        Assert.Equal(0.016693800m, metadata!["gateway"].GetProperty("cost").GetDecimal());
    }

    [Fact]
    public async Task Streaming_responses_mapper_sums_gateway_cost_on_completed_response()
    {
        var mapper = new ResponsesNativeMapper();
        var parts = await CollectAsync(mapper.MapStreamingAsync(
            new ResponseRequest
            {
                Model = "openai/gpt-fixture",
                Input = new ResponseInput("Say hello"),
                Metadata = new Dictionary<string, object?>
                {
                    ["gateway"] = new Dictionary<string, object?>
                    {
                        ["cost"] = 0.10m,
                        ["currency"] = "EUR"
                    }
                }
            },
            "StructuredAgent",
            "openai",
            ToAsync([
                new AgentResponseUpdate(ChatRole.Assistant, [CreateFinishMetadataContent(0.23m, "EUR", "fixture")])
            ])));

        var completed = Assert.IsType<ResponseCompleted>(Assert.Single(parts.OfType<ResponseCompleted>()));
        Assert.Equal("StructuredAgent", completed.Response.Model);
        var gateway = GetProviderMetadataGateway(completed.Response.Metadata!);
        Assert.Equal(0.33m, gateway["cost"]);
        Assert.Equal("fixture", Assert.IsType<JsonElement>(gateway["provider"]).GetString());
        Assert.False(completed.Response.Metadata!.ContainsKey("gateway"));
    }


    [Fact]
    public async Task Streaming_writer_error_helper_writes_vercel_error_part()
    {
        var response = new DefaultHttpContext().Response;
        response.Body = new MemoryStream();

        await response.WriteErrorPartAsync("Original provider error");

        response.Body.Position = 0;
        using var reader = new StreamReader(response.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();

        Assert.Contains("data:", body);
        Assert.Contains("\"type\":\"error\"", body);
        Assert.Contains("Original provider error", body);
    }

    [Fact]
    public async Task Streaming_writer_abort_helper_writes_vercel_abort_part()
    {
        var response = new DefaultHttpContext().Response;
        response.Body = new MemoryStream();

        await response.WriteAbortPartAsync("Operation canceled");

        response.Body.Position = 0;
        using var reader = new StreamReader(response.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();

        Assert.Contains("data:", body);
        Assert.Contains("\"type\":\"abort\"", body);
        Assert.Contains("Operation canceled", body);
    }

    [Fact]
    public async Task Non_streaming_structured_response_roundtrips_to_data_ui_part()
    {
        var fixture = LoadFixture(StructuredFixturePath);

        using var httpClient = CreateHttpClient(_ => CreateJsonResponse(fixture));
        using var client = CreateClient(
            httpClient,
            CreateAgent(outputSchema: new OutputSchema
            {
                Properties = new Dictionary<string, Property>
                {
                    ["summary"] = new() { Type = "string", Required = true, Description = "Short summary" },
                    ["tone"] = new() { Type = "string", Description = "Detected tone" }
                }
            }));

        var response = await client.GetResponseAsync(CreateUserMessages("Return a structured response"));

        var dataContent = Assert.Single(response.Messages.SelectMany(message => message.Contents).OfType<DataContent>());

        Assert.Equal("structuredagent_output", dataContent.Name);
        Assert.Equal("application/json", dataContent.MediaType);

        var uiPart = dataContent.ToDataUIPart();
        var data = Assert.IsType<JsonElement>(uiPart.Data);

        Assert.Equal("data-structuredagent_output", uiPart.Type);
        Assert.Equal("hello", data.GetProperty("summary").GetString());
        Assert.Equal("friendly", data.GetProperty("tone").GetString());
    }

    [Fact]
    public async Task Streaming_google_empty_reasoning_roundtrips_to_reasoning_ui_parts_with_provider_metadata()
    {
        var uiParts = await CollectUiPartsAsync(GoogleEmptyReasoningFixturePath, "google/gemini-fixture");

        Assert.Single(uiParts.OfType<ReasoningStartUIPart>());
        Assert.Empty(uiParts.OfType<ReasoningDeltaUIPart>());
        Assert.Single(uiParts.OfType<ReasoningEndUIPart>());

        var reasoningStartPart = Assert.IsType<ReasoningStartUIPart>(uiParts.Single(part => part.Type == "reasoning-start"));
        Assert.Null(reasoningStartPart.ProviderMetadata);

        var reasoningEndPart = Assert.IsType<ReasoningEndUIPart>(uiParts.Single(part => part.Type == "reasoning-end"));
        Assert.Null(reasoningEndPart.ProviderMetadata);
    }

    [Fact]
    public async Task Streaming_openai_empty_reasoning_roundtrips_to_reasoning_ui_parts_without_reasoning_delta_text()
    {
        var uiParts = await CollectUiPartsAsync(OpenAiEmptyReasoningFixturePath, "openai/gpt-fixture");

        Assert.Single(uiParts.OfType<ReasoningStartUIPart>());
        Assert.Empty(uiParts.OfType<ReasoningDeltaUIPart>());
        Assert.Single(uiParts.OfType<ReasoningEndUIPart>());

        var reasoningEndPart = Assert.IsType<ReasoningEndUIPart>(uiParts.Single(part => part.Type == "reasoning-end"));
        var providerMetadata = Assert.Contains("StructuredAgent", reasoningEndPart.ProviderMetadata ?? []);

        Assert.True(providerMetadata.ContainsKey("encrypted_content"));
        Assert.Single(providerMetadata);
        Assert.False(string.IsNullOrWhiteSpace(Assert.IsType<string>(providerMetadata["encrypted_content"])));
    }

    [Fact]
    public async Task Streaming_openai_reasoning_summaries_continue_to_emit_reasoning_text_and_end_metadata()
    {
        var uiParts = await CollectUiPartsAsync(OpenAiReasoningSummaryFixturePath, "openai/gpt-fixture");

        Assert.NotEmpty(uiParts.OfType<ReasoningDeltaUIPart>());

        var reasoningText = string.Concat(uiParts.OfType<ReasoningDeltaUIPart>().Select(part => part.Delta));
        Assert.Contains("Responding to user casually", reasoningText);
    }

    [Fact]
    public async Task Streaming_openai_shell_calls_download_file_and_output_file_roundtrip_to_visible_ui_parts()
    {
        var uiParts = await CollectUiPartsAsync(OpenAiShellAndFileFixturePath, "openai/gpt-fixture");

        var shellInputs = uiParts
            .OfType<ToolCallPart>()
            .Where(part => string.Equals(part.ToolName, "shell_call", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(2, shellInputs.Count);
        Assert.All(shellInputs, part => Assert.True(part.ProviderExecuted));

        var shellOutputs = uiParts
            .OfType<ToolOutputAvailablePart>()
            .Where(part => shellInputs.Any(input => string.Equals(input.ToolCallId, part.ToolCallId, StringComparison.Ordinal)))
            .ToList();

        Assert.Contains(shellOutputs, part => part.Preliminary == true);
        Assert.Equal(2, shellOutputs.Count(part => part.Preliminary is false or null));

        var finalShellOutputJson = JsonSerializer.SerializeToElement(
            shellOutputs.Last(part => part.Preliminary is false or null).Output,
            JsonSerializerOptions.Web);

        Assert.Contains("zeer_simpel.docx", finalShellOutputJson.GetRawText());

        var downloadInput = Assert.Single(uiParts
            .OfType<ToolCallPart>()
                , part => string.Equals(part.ToolName, "download_file", StringComparison.Ordinal));

        Assert.True(downloadInput.ProviderExecuted);

        var downloadOutput = Assert.Single(uiParts
            .OfType<ToolOutputAvailablePart>()
            , part => string.Equals(part.ToolCallId, downloadInput.ToolCallId, StringComparison.Ordinal));

        Assert.True(downloadOutput.ProviderExecuted);
        Assert.Contains("openai", downloadOutput.ProviderMetadata ?? []);

    }


    [Fact]
    public async Task Reasoning_ui_parts_are_dropped_on_roundtrip_when_agent_name_does_not_match()
    {
        var requestBody = await CaptureRequestBodyAsync(
            [new UIMessage
            {
                Id = "assistant-1",
                Role = AIHappey.Vercel.Models.Role.assistant,
                Parts =
                [
                    new ReasoningStartUIPart { Id = "reasoning-1" },
                    new ReasoningDeltaUIPart { Id = "reasoning-1", Delta = "Visible reasoning summary" },
                    new ReasoningEndUIPart
                    {
                        Id = "reasoning-1",
                        ProviderMetadata = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal)
                        {
                            ["OtherAgent"] = new(StringComparer.Ordinal)
                            {
                                ["encrypted_content"] = "encrypted-payload"
                            }
                        }
                    }
                ]
            }],
            activeAgentNames: ["StructuredAgent"]);

        Assert.DoesNotContain("\"type\":\"reasoning\"", requestBody);
        Assert.DoesNotContain("\"encrypted_content\":\"encrypted-payload\"", requestBody);
    }

    private static IEnumerable<ChatMessage> CreateUserMessages(string text)
        =>
        [
            new ChatMessage(ChatRole.User, [new TextContent(text)])
        ];

    private static AgentChatClient CreateClient(HttpClient httpClient, Agent agent)
        => new(httpClient, new StaticHttpClientFactory(httpClient), agent, new Dictionary<string, string?>());

    private static Agent CreateAgent(
        Dictionary<string, object>? providerMetadata = null,
        OutputSchema? outputSchema = null,
        string modelId = "openai/gpt-fixture",
        Dictionary<string, string>? providerHeaders = null)
        => new()
        {
            Name = "StructuredAgent",
            Description = "Fixture test agent",
            Instructions = "Return concise answers.",
            OutputSchema = outputSchema,
            Model = new Common.Models.AIModel
            {
                Id = modelId,
                ProviderMetadata = providerMetadata,
                ProviderHeaders = providerHeaders,
                Options = new AIModelOptions
                {
                    Temperature = 0
                }
            }
        };

    private static HttpClient CreateHttpClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new StaticResponseHttpMessageHandler(responder))
        {
            BaseAddress = new Uri("https://example.test/")
        };

    private static HttpResponseMessage CreateStreamingResponse(string body)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
        };

        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        return response;
    }

    private static HttpResponseMessage CreateJsonResponse(string body, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return response;
    }

    private static string LoadFixture(string relativePath)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static ChatResponseUpdate CreateCompletionUpdate(
        long inputTokens,
        long outputTokens,
        long totalTokens,
        decimal gatewayCost,
        string currency,
        string provider)
        => new(ChatRole.Assistant,
        [
            new UsageContent
            {
                Details = new UsageDetails
                {
                    InputTokenCount = inputTokens,
                    OutputTokenCount = outputTokens,
                    TotalTokenCount = totalTokens
                }
            },
            new DataContent(
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                {
                    gateway = new
                    {
                        cost = gatewayCost,
                        currency,
                        provider
                    }
                }, JsonSerializerOptions.Web)),
                "application/json")
            {
                Name = "finish_metadata"
            }
        ])
        {
            MessageId = Guid.NewGuid().ToString("N"),
            FinishReason = new ChatFinishReason("stop"),
            AuthorName = "FixtureAgent",
            ModelId = "openai/gpt-fixture"
        };

    private static DataContent CreateFinishMetadataContent(decimal gatewayCost, string currency, string provider)
        => new(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                gateway = new
                {
                    cost = gatewayCost,
                    currency,
                    provider
                }
            }, JsonSerializerOptions.Web)),
            "application/json")
        {
            Name = "finish_metadata"
        };

    private static DataContent CreateProviderMetadataGatewayContent(
        decimal providerGatewayCost,
        decimal directGatewayCost,
        string currency,
        string provider)
        => new(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                providerMetadata = new
                {
                    openai = new
                    {
                        gateway = new
                        {
                            cost = providerGatewayCost,
                            currency,
                            provider
                        }
                    }
                },
                gateway = new
                {
                    cost = directGatewayCost,
                    currency = "USD",
                    provider = "direct"
                }
            }, JsonSerializerOptions.Web)),
            "application/json")
        {
            Name = "finish_metadata"
        };

    private static Dictionary<string, object?> GetProviderMetadataGateway(Dictionary<string, object?> metadata)
    {
        var providerMetadata = Assert.IsType<Dictionary<string, object?>>(metadata["providerMetadata"]);
        Assert.IsType<Dictionary<string, object?>>(providerMetadata["openai"]);
        return Assert.IsType<Dictionary<string, object?>>(providerMetadata["gateway"]);
    }

    private static async IAsyncEnumerable<T> ToAsync<T>(IEnumerable<T> source)
    {
        foreach (var item in source)
        {
            yield return item;
            await Task.Yield();
        }
    }

    private static void AssertFunctionItem(JsonElement item, string expectedType, string expectedCallId)
    {
        Assert.Equal(expectedType, item.GetProperty("type").GetString());
        Assert.Equal(expectedCallId, item.GetProperty("call_id").GetString());
    }

    private const string TestMcpUrl = "https://mcp.example.com/server";

    private static void SeedMcpMetadata(AgentChatClient client)
    {
        GetPrivateField<ConcurrentDictionary<string, Implementation>>(client, "McpServerImplementations")[TestMcpUrl] = new Implementation
        {
            Name = "example-mcp",
            Version = "1.0.0",
            Title = "Example MCP",
            WebsiteUrl = "https://mcp.example.com"
        };

        GetPrivateField<ConcurrentDictionary<string, string>>(client, "McpServerInstructions")[TestMcpUrl] = "Use the MCP resources before answering.";

        GetPrivateField<ConcurrentDictionary<string, IEnumerable<object>>>(client, "McpServerResources")[TestMcpUrl] =
        [
            new TestMcpResource
            {
                Name = "Policy",
                Uri = "file://policy.md",
                Description = "Policy reference",
                MimeType = "text/markdown",
                Size = 42,
                Annotations = new TestMcpAnnotations
                {
                    Audience = ["assistant"],
                    Priority = "high",
                    LastModified = "2026-04-28T00:00:00Z"
                }
            },
            new TestMcpResource
            {
                Name = "UserOnly",
                Uri = "file://user.txt",
                Description = "User visible resource",
                MimeType = "text/plain",
                Annotations = new TestMcpAnnotations
                {
                    Audience = ["user"]
                }
            }
        ];

        GetPrivateField<ConcurrentDictionary<string, IEnumerable<object>>>(client, "McpServerResourceTemplates")[TestMcpUrl] =
        [
            new TestMcpResourceTemplate
            {
                Name = "Ticket",
                UriTemplate = "ticket://{id}",
                Description = "Ticket lookup template",
                MimeType = "application/json",
                Annotations = new TestMcpAnnotations
                {
                    Audience = ["assistant"],
                    Priority = "high"
                }
            }
        ];
    }

    private static T GetPrivateField<T>(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(instance.GetType().FullName, fieldName);

        return (T)field.GetValue(instance)!;
    }

    private static T InvokePrivateStatic<T>(string methodName, params object?[] arguments)
        => (T)InvokePrivateStatic(methodName, arguments)!;

    private static object? InvokePrivateStatic(string methodName, params object?[] arguments)
    {
        var method = typeof(AgentChatClient).GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(AgentChatClient).FullName, methodName);

        return method.Invoke(null, arguments);
    }

    private static JsonElement ExtractSingleMcpInstructionBlock(string instructions)
    {
        var json = instructions
            .Split(["\r\n\r\n", "\n\n"], StringSplitOptions.RemoveEmptyEntries)
            .First(section => section.TrimStart().StartsWith("{\"modelContextProtocolServer\"", StringComparison.Ordinal));

        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> source, CancellationToken cancellationToken = default)
    {
        var items = new List<T>();

        await foreach (var item in source.WithCancellation(cancellationToken))
            items.Add(item);

        return items;
    }

    private static async Task<List<UIMessagePart>> CollectUiPartsAsync(string fixturePath, string modelId)
    {
        var fixture = LoadFixture(fixturePath);

        using var httpClient = CreateHttpClient(_ => CreateStreamingResponse(fixture));
        using var client = CreateClient(httpClient, CreateAgent(modelId: modelId));

        var agent = new ChatClientAgent(
            client,
            instructions: "Fixture test instructions",
            name: "FixtureAgent",
            description: "Fixture test agent");

        var mapper = new StreamingContentMapper();
        var updates = agent.RunStreamingAsync(CreateUserMessages("Say hello"));

        return await CollectAsync(mapper.MapAsync(updates));
    }

    private static async Task<string> CaptureRequestBodyAsync(
        IEnumerable<UIMessage> uiMessages,
        IEnumerable<string> activeAgentNames)
    {
        var fixture = LoadFixture(StructuredFixturePath);
        string requestBody = string.Empty;

        using var httpClient = CreateHttpClient(request =>
        {
            requestBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            return CreateJsonResponse(fixture);
        });

        using var client = CreateClient(httpClient, CreateAgent(modelId: "openai/gpt-fixture"));

        var messages = uiMessages.ToMessages(activeAgentNames).ToList();
        await client.GetResponseAsync(messages);

        return requestBody;
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = responder(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private sealed class TestMcpAnnotations
    {
        public string[]? Audience { get; init; }
        public string? Priority { get; init; }
        public string? LastModified { get; init; }
    }

    private sealed class TestMcpResource
    {
        public string Name { get; init; } = string.Empty;
        public string Uri { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string MimeType { get; init; } = string.Empty;
        public long? Size { get; init; }
        public TestMcpAnnotations? Annotations { get; init; }
    }

    private sealed class TestMcpResourceTemplate
    {
        public string Name { get; init; } = string.Empty;
        public string UriTemplate { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string MimeType { get; init; } = string.Empty;
        public TestMcpAnnotations? Annotations { get; init; }
    }
}
