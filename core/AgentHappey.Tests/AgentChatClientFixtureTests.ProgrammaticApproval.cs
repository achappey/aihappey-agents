using System.Text.Json;
using AgentHappey.Common.Extensions;
using AgentHappey.Common.Models;
using AgentHappey.Core;
using AgentHappey.Core.ChatRuntime;
using AgentHappey.Core.Responses;
using AIHappey.Responses;
using Microsoft.Agents.AI;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;

namespace AgentHappey.Tests;

public sealed partial class AgentChatClientFixtureTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Programmatic_chat_approval_executes_whole_batch_and_replays_normalized_history(bool approved)
    {
        var executions = new int[5];
        var tools = Enumerable.Range(0, 5).Select(index => (AITool)AIFunctionFactory.Create(() =>
        {
            executions[index]++;
            return new { value = index };
        }, $"usage_{index}")).ToList();
        var bodies = new List<JsonElement>();
        using var http = CreateHttpClient(request =>
        {
            bodies.Add(JsonSerializer.Deserialize<JsonElement>(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()));
            Assert.True(bodies.Count <= 2, "The completed batch must not start another program.");
            return CreateStreamingResponse(bodies.Count == 1
                ? ProgrammaticApprovalStream()
                : CreateTextResponseStream("Last week: 401 requests, 760365 tokens."));
        });
        var orchestrator = new ChatRuntimeOrchestrator(new StreamingContentMapper(), new ModelCatalog([]));
        var request = new AgentRequest
        {
            Agents = [CreateAgent(name: "UsageAgent")],
            Messages = [new UIMessage { Id = "question", Role = Role.user, Parts = [new TextUIPart { Text = "Last week?" }] }]
        };
        List<UIMessagePart> parts;
        using (var client = CreateClient(http, request.Agents.Single()))
        {
            var response = new DefaultHttpContext().Response;
            response.Body = new MemoryStream();
            var context = await orchestrator.PrepareAsync(response, request, _ => client);
            context.SingleAgentRunOptions!.ChatOptions!.Tools = tools;
            parts = await CollectAsync(new StreamingContentMapper().MapAsync(
                orchestrator.StreamAgentAsync(context), requestToolApproval: context.RequestToolApproval));
        }

        Assert.All(executions, count => Assert.Equal(1, count));
        Assert.Single(bodies);
        Assert.Equal(5, parts.OfType<ToolOutputAvailablePart>().Count());
        Assert.Equal(5, parts.OfType<ToolApprovalRequestUIPart>().Count());
        var program = parts.OfType<ToolCallPart>().Last(part => part.ToolName == "program");
        Assert.Equal("final-fingerprint", NativeProgramItem(program.ProviderMetadata!).GetProperty("fingerprint").GetString());

        // The browser SDK stores a tool invocation, not ToolCallPart + ToolOutputAvailablePart.
        // JSON round-trip it too: live server references must not repair missing replay state.
        var history = NormalizeProgrammaticUiParts(parts, approved);
        request.Messages.Add(JsonSerializer.Deserialize<UIMessage>(JsonSerializer.Serialize(history, JsonSerializerOptions.Web), JsonSerializerOptions.Web)!);
        using (var freshClient = CreateClient(http, request.Agents.Single()))
        {
            var response = new DefaultHttpContext().Response;
            response.Body = new MemoryStream();
            var context = await orchestrator.PrepareAsync(response, request, _ => freshClient);
            context.SingleAgentRunOptions!.ChatOptions!.Tools = tools;
            var answer = await CollectAsync(orchestrator.StreamAgentAsync(context));
            Assert.Contains(answer.SelectMany(update => update.Contents).OfType<TextContent>(), text => text.Text.Contains("401 requests"));
        }

        Assert.Equal(2, bodies.Count);
        Assert.All(executions, count => Assert.Equal(1, count));
        var input = bodies[1].GetProperty("input").EnumerateArray().ToList();
        var parent = Assert.Single(input, item => item.GetProperty("type").GetString() == "program");
        Assert.Equal("final-fingerprint", parent.GetProperty("fingerprint").GetString());
        Assert.DoesNotContain(input, item => item.GetProperty("type").GetString() == "program_output");
        for (var index = 0; index < 5; index++)
        {
            var call = Assert.Single(input, item => item.GetProperty("type").GetString() == "function_call"
                && item.GetProperty("call_id").GetString() == $"usage-call-{index}");
            Assert.Equal("completed", call.GetProperty("status").GetString());
            AssertProgramCaller(call, "usage-program-call");
            Assert.True(input.IndexOf(parent) < input.IndexOf(call));
            var output = Assert.Single(input, item => item.GetProperty("type").GetString() == "function_call_output"
                && item.GetProperty("call_id").GetString() == $"usage-call-{index}");
            AssertProgramCaller(output, "usage-program-call");
            Assert.Equal(approved, output.GetProperty("output").GetString()!.Contains("value"));
        }
    }

    [Fact]
    public async Task Programmatic_normalized_descriptor_without_result_survives_fresh_client_replay()
    {
        var history = NormalizeProgrammaticUiParts([
            new ToolCallPart
            {
                ToolCallId = "usage-program-call", ToolName = "program", ProviderExecuted = true,
                Input = new { code = "await tools.usage_0({})" },
                ProviderMetadata = ProgrammaticMetadata(new
                {
                    type = "program", id = "usage-program-item", call_id = "usage-program-call",
                    code = "await tools.usage_0({})", fingerprint = "final-fingerprint"
                })
            }
        ], approved: true);
        var messages = new[] { history }.ToMessages(["UsageAgent"]).ToList();
        var descriptor = Assert.Single(messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>());
        Assert.True(descriptor.InformationalOnly);
        Assert.DoesNotContain(messages.SelectMany(message => message.Contents), content => content is FunctionResultContent);
        string? body = null;
        using var http = CreateHttpClient(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return CreateJsonResponse(LoadFixture(StructuredFixturePath));
        });
        using var client = CreateClient(http, CreateAgent(name: "UsageAgent"));
        await client.GetResponseAsync(messages);
        var input = JsonSerializer.Deserialize<JsonElement>(body!).GetProperty("input");
        Assert.Equal("program", Assert.Single(input.EnumerateArray()).GetProperty("type").GetString());
    }

    [Fact]
    public async Task Programmatic_orphan_caller_is_rejected_before_inference()
    {
        using var http = CreateHttpClient(_ => throw new InvalidOperationException("Orphan graph reached inference"));
        using var client = CreateClient(http, CreateAgent(name: "UsageAgent"));
        var messages = new ResponseRequest
        {
            Input = new ResponseInput([
                new ResponseFunctionCallItem
                {
                    Id = "orphan-item", CallId = "orphan-call", Name = "usage_0", Arguments = "{}",
                    Caller = new ResponseCaller { Type = "program", CallerId = "missing-program" }
                },
                new ResponseFunctionCallOutputItem { CallId = "orphan-call", Output = "{}" }
            ])
        }.ToChatRuntimeRequest().Messages;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(messages));
        Assert.Contains("missing-program", error.Message);
        Assert.Contains("program", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Programmatic_final_snapshot_updates_replay_without_duplicate_execution()
    {
        var bodies = new List<JsonElement>();
        using var http = CreateHttpClient(request =>
        {
            bodies.Add(JsonSerializer.Deserialize<JsonElement>(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()));
            return CreateStreamingResponse(ProgrammaticApprovalStream());
        });
        using var client = CreateClient(http, CreateAgent(name: "UsageAgent"));
        var updates = await CollectAsync(client.GetStreamingResponseAsync(CreateUserMessages("Last week?")));
        Assert.Equal(5, updates.SelectMany(update => update.Contents).OfType<FunctionCallContent>().Count(call => !call.InformationalOnly));
        var response = updates.ToChatResponse();
        await CollectAsync(client.GetStreamingResponseAsync(response.Messages));
        var input = bodies[1].GetProperty("input").EnumerateArray().ToList();
        Assert.Equal("final-fingerprint", Assert.Single(input, item => item.GetProperty("type").GetString() == "program")
            .GetProperty("fingerprint").GetString());
        Assert.All(input.Where(item => item.GetProperty("type").GetString() == "function_call"),
            item => Assert.Equal("completed", item.GetProperty("status").GetString()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Programmatic_native_responses_final_output_keeps_latest_program(bool streaming)
    {
        using var http = CreateHttpClient(_ => CreateStreamingResponse(ProgrammaticApprovalStream()));
        using var client = CreateClient(http, CreateAgent(name: "UsageAgent"));
        var updates = await CollectAsync(client.GetStreamingResponseAsync(CreateUserMessages("Last week?")));
        var mapper = new ResponsesNativeMapper();
        ResponseResult response;
        if (streaming)
        {
            var nativeUpdates = updates.Select(update => new AgentResponseUpdate(update.Role, update.Contents)
            {
                MessageId = update.MessageId, AuthorName = update.AuthorName
            });
            var parts = await CollectAsync(mapper.MapStreamingAsync(new ResponseRequest(), "UsageAgent", "openai", nativeUpdates.ToAsyncEnumerable()));
            response = Assert.IsType<AIHappey.Responses.Streaming.ResponseCompleted>(parts.Last()).Response;
        }
        else
            response = mapper.Map(new ResponseRequest(), "UsageAgent", "openai", new AgentResponse(updates.ToChatResponse().Messages));
        var output = JsonSerializer.SerializeToElement(response, ResponseJson.Default).GetProperty("output").EnumerateArray().ToList();
        Assert.Equal("final-fingerprint", Assert.Single(output, item => item.GetProperty("type").GetString() == "program")
            .GetProperty("fingerprint").GetString());
        Assert.Equal(5, output.Count(item => item.GetProperty("type").GetString() == "function_call"));
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("wrong-call-id")]
    [InlineData("missing-fingerprint")]
    [InlineData("input-streaming")]
    [InlineData("denied")]
    public void Programmatic_invalid_or_foreign_normalized_descriptors_never_become_executable(string scenario)
    {
        var metadata = ProgrammaticMetadata(new
        {
            type = "program", id = "usage-program-item", call_id = scenario == "wrong-call-id" ? "other" : "usage-program-call",
            code = "await tools.usage_0({})", fingerprint = scenario == "missing-fingerprint" ? "" : "fingerprint"
        });
        if (scenario == "foreign") metadata["openai"]!["agent_name"] = "OtherAgent";
        var history = new UIMessage
        {
            Id = "invalid-program", Role = Role.assistant,
            Parts = [new ToolInvocationPart
            {
                Type = "tool-program", ToolCallId = "usage-program-call", Input = new { },
                ProviderExecuted = true, CallProviderMetadata = metadata,
                State = scenario == "input-streaming" ? scenario : "input-available",
                Approval = scenario == "denied" ? new ToolApproval { Id = "denied", Approved = false } : null
            }]
        };
        Assert.Empty(new[] { history }.ToMessages(["UsageAgent"]));
    }

    [Fact]
    public async Task Programmatic_normalized_real_program_output_replays_as_native_output()
    {
        var callMetadata = ProgrammaticMetadata(new
        {
            type = "program", id = "usage-program-item", call_id = "usage-program-call", code = "text(401)", fingerprint = "fingerprint"
        });
        var outputMetadata = ProgrammaticMetadata(new
        {
            type = "program_output", call_id = "usage-program-call", result = "401", status = "completed"
        });
        var history = new UIMessage
        {
            Id = "program-output", Role = Role.assistant,
            Parts = [new ToolInvocationPart
            {
                Type = "tool-program", ToolCallId = "usage-program-call", Input = new { code = "text(401)" },
                Output = new { result = "401" }, State = "output-available", ProviderExecuted = true,
                CallProviderMetadata = callMetadata, ResultProviderMetadata = outputMetadata
            }]
        };
        string? body = null;
        using var http = CreateHttpClient(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return CreateJsonResponse(LoadFixture(StructuredFixturePath));
        });
        using var client = CreateClient(http, CreateAgent(name: "UsageAgent"));
        await client.GetResponseAsync(new[] { history }.ToMessages(["UsageAgent"]));
        var input = JsonSerializer.Deserialize<JsonElement>(body!).GetProperty("input").EnumerateArray().ToList();
        Assert.Equal(new[] { "program", "program_output" }, input.Select(item => item.GetProperty("type").GetString()));
        Assert.Equal("401", input[1].GetProperty("result").GetString());
    }

    [Fact]
    public async Task Programmatic_failed_stream_does_not_execute_buffered_children()
    {
        var stream = ProgrammaticApprovalStream().Replace("\"type\":\"response.completed\"", "\"type\":\"response.failed\"");
        using var http = CreateHttpClient(_ => CreateStreamingResponse(stream));
        using var client = CreateClient(http, CreateAgent(name: "UsageAgent"));
        var updates = await CollectAsync(client.GetStreamingResponseAsync(CreateUserMessages("Last week?")));
        Assert.DoesNotContain(updates.SelectMany(update => update.Contents).OfType<FunctionCallContent>(), call => !call.InformationalOnly);
        Assert.Contains(updates.SelectMany(update => update.Contents), content => content is ErrorContent);
    }

    [Fact]
    public async Task Programmatic_duplicate_stream_events_execute_each_child_once()
    {
        var executions = new int[5];
        var tools = Enumerable.Range(0, 5).Select(index => (AITool)AIFunctionFactory.Create(() => ++executions[index], $"usage_{index}")).ToList();
        var requests = 0;
        using var http = CreateHttpClient(_ =>
        {
            requests++;
            Assert.True(requests <= 2);
            var stream = ProgrammaticApprovalStream();
            stream = string.Join("\n\n", stream.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).SelectMany(part => new[] { part, part })) + "\n\n";
            return CreateStreamingResponse(requests == 1 ? stream : CreateTextResponseStream("Done."));
        });
        using var client = CreateClient(http, CreateAgent(name: "UsageAgent"));
        var agent = new ChatClientAgent(client, name: "UsageAgent", tools: tools);
        await CollectAsync(agent.RunStreamingAsync(CreateUserMessages("Last week?")));
        Assert.Equal(2, requests);
        Assert.All(executions, count => Assert.Equal(1, count));
    }

    [Fact]
    public async Task Programmatic_cancellation_does_not_release_buffered_children()
    {
        using var cancellation = new CancellationTokenSource();
        using var http = CreateHttpClient(_ => CreateStreamingResponse(ProgrammaticApprovalStream()));
        using var client = CreateClient(http, CreateAgent(name: "UsageAgent"));
        var calls = new List<FunctionCallContent>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var update in client.GetStreamingResponseAsync(CreateUserMessages("Last week?"), cancellationToken: cancellation.Token))
            {
                calls.AddRange(update.Contents.OfType<FunctionCallContent>().Where(call => !call.InformationalOnly));
                if (update.Contents.OfType<FunctionCallContent>().Any(call => call.Name == "program"))
                    cancellation.Cancel();
            }
        });
        Assert.Empty(calls);
    }

    private static string ProgrammaticApprovalStream()
    {
        object Program(string fingerprint) => new
        {
            type = "program", id = "usage-program-item", call_id = "usage-program-call",
            code = "const results = await Promise.all([0,1,2,3,4].map(i => tools['usage_' + i]({}))); text(results);",
            fingerprint
        };
        object Call(int index, string status) => new
        {
            type = "function_call", id = $"usage-item-{index}", call_id = $"usage-call-{index}",
            name = $"usage_{index}", arguments = "{}", status,
            caller = new { type = "program", caller_id = "usage-program-call" }
        };
        var events = new List<string>();
        events.Add(Sse("response.output_item.added", new { type = "response.output_item.added", output_index = 0, item = Program("start-fingerprint") }));
        events.Add(Sse("response.output_item.done", new { type = "response.output_item.done", output_index = 0, item = Program("done-fingerprint") }));
        for (var index = 0; index < 5; index++)
        {
            events.Add(Sse("response.output_item.done", new { type = "response.output_item.done", output_index = index + 1, item = Call(index, "in_progress") }));
        }
        events.Add(Sse("response.completed", new
        {
            type = "response.completed",
            response = new
            {
                id = "usage-response", model = "openai/gpt-fixture", status = "completed",
                output = new[] { Program("final-fingerprint") }.Concat(Enumerable.Range(0, 5).Select(index => Call(index, "completed"))).ToArray()
            }
        }));
        return string.Join("\n\n", events) + "\n\ndata: [DONE]\n\n";
    }

    private static UIMessage NormalizeProgrammaticUiParts(List<UIMessagePart> parts, bool approved)
    {
        var normalized = parts.OfType<ToolCallPart>().GroupBy(call => call.ToolCallId).Select(group =>
        {
            var call = group.Last();
            var output = parts.OfType<ToolOutputAvailablePart>().LastOrDefault(part => part.ToolCallId == call.ToolCallId);
            var approval = parts.OfType<ToolApprovalRequestUIPart>().LastOrDefault(part => part.ToolCallId == call.ToolCallId);
            return new ToolInvocationPart
            {
                Type = "tool-" + call.ToolName, ToolCallId = call.ToolCallId, Input = call.Input,
                ProviderExecuted = output?.ProviderExecuted ?? call.ProviderExecuted,
                CallProviderMetadata = call.ProviderMetadata, ResultProviderMetadata = output?.ProviderMetadata,
                Output = output?.Output,
                State = approval is not null ? "approval-responded" : output is not null ? "output-available" : "input-available",
                Approval = approval is null ? null : new ToolApproval { Id = approval.ApprovalId, Approved = approved }
            };
        });
        return new UIMessage
        {
            Id = "usage-history", Role = Role.assistant,
            Metadata = new Dictionary<string, object> { ["model"] = "UsageAgent" },
            Parts = normalized.Cast<UIMessagePart>().ToList()
        };
    }

    private static Dictionary<string, Dictionary<string, object>?> ProgrammaticMetadata(object item) => new()
    {
        ["openai"] = new() { ["type"] = "program", ["responses_item"] = JsonSerializer.SerializeToElement(item) }
    };

    private static JsonElement NativeProgramItem(Dictionary<string, Dictionary<string, object>?> metadata) =>
        JsonSerializer.SerializeToElement(metadata["openai"]!["responses_item"]);
}
