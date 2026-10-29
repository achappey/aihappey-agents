using System.Text.Json;
using AgentHappey.Common.Models;
using AgentHappey.Core;
using AgentHappey.Core.ChatClient;
using AgentHappey.Core.ChatRuntime;
using AgentHappey.Core.Responses;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using AIHappey.Vercel.Models;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;

namespace AgentHappey.Tests;

public sealed partial class AgentChatClientFixtureTests
{
    private const string ExecutionCostMetadata = """
        {"gateway":{"cost":0.10,"currency":"EUR","provider":"execution","details":{"retained":true}},"custom":"keep"}
        """;

    [Theory]
    [InlineData(false, "choice")]
    [InlineData(true, "choice")]
    [InlineData(false, "refusal")]
    [InlineData(true, "refusal")]
    [InlineData(false, "unknown")]
    [InlineData(true, "unknown")]
    public async Task Decision_cost_model_routing_counts_success_or_fallback_once(bool streaming, string answerType)
    {
        var decisions = 0;
        var inference = 0;
        using var http = CreateHttpClient(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/models") return CreateJsonResponse(RoutingCatalog);
            if (request.RequestUri.AbsolutePath == "/api/decisions")
            {
                decisions++;
                var answer = answerType switch
                {
                    "refusal" => """{"type":"refusal"}""",
                    "unknown" => """{"type":"choice","choice":"not-a-candidate"}""",
                    _ => """{"type":"choice","choice":"anthropic/second"}"""
                };
                return CreateJsonResponse("""
                    {"answers":{"model":ANSWER},"providerMetadata":{"gateway":{"cost":0.02,"provider":"do-not-copy-router-metadata"},"openai":{"private":"router-only"}}}
                    """.Replace("ANSWER", answer));
            }
            Assert.Equal("/v1/responses", request.RequestUri.AbsolutePath);
            using var json = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal(answerType == "choice" ? "anthropic/second" : "openai/first", json.RootElement.GetProperty("model").GetString());
            inference++;
            return CreateDecisionCostExecutionResponse(streaming, ExecutionCostMetadata);
        });
        var definition = CreateDecisionCostAgent();
        var original = JsonSerializer.Serialize(definition);
        using var client = CreateClient(http, definition);
        var agent = new ChatClientAgent(client, name: definition.Name);

        for (var turn = 0; turn < 2; turn++)
        {
            var (response, updates) = await RunDecisionCostAgentAsync(agent, streaming);
            var metadata = ReadDecisionCostFinishMetadata(response);
            Assert.Equal(turn == 0 ? 0.12m : 0.10m, metadata.GetProperty("gateway").GetProperty("cost").GetDecimal());
            Assert.Equal("keep", metadata.GetProperty("custom").GetString());
            Assert.Equal("execution", metadata.GetProperty("gateway").GetProperty("provider").GetString());
            Assert.Equal("EUR", metadata.GetProperty("gateway").GetProperty("currency").GetString());
            Assert.True(metadata.GetProperty("gateway").GetProperty("details").GetProperty("retained").GetBoolean());
            Assert.DoesNotContain("router-only", metadata.GetRawText());
            Assert.DoesNotContain("do-not-copy-router-metadata", metadata.GetRawText());
            await AssertDecisionCostMappingsAsync(response, updates, turn == 0 ? 0.12m : 0.10m);
        }
        Assert.Equal(1, decisions);
        Assert.Equal(2, inference);
        Assert.Equal(original, JsonSerializer.Serialize(definition));
    }

    public static IEnumerable<object?[]> DecisionCostOptionalMetadataCases()
    {
        string?[] metadata = [null, "{}", "{\"gateway\":null}", "{\"gateway\":[]}", "{\"gateway\":{}}",
            "{\"gateway\":{\"cost\":null}}", "{\"gateway\":{\"cost\":\"0.02\"}}",
            "{\"gateway\":{\"cost\":{}}}", "{\"gateway\":{\"cost\":1e100}}", "{\"gateway\":{\"cost\":0}}"];
        foreach (var streaming in new[] { false, true })
            foreach (var item in metadata)
                yield return [streaming, item];
    }

    [Theory]
    [MemberData(nameof(DecisionCostOptionalMetadataCases))]
    public async Task Decision_cost_missing_invalid_or_zero_metadata_does_not_change_selection_or_execution_cost(bool streaming, string? providerMetadata)
    {
        using var http = CreateHttpClient(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1/models" => CreateJsonResponse(RoutingCatalog),
            "/api/decisions" => CreateJsonResponse("""{"answers":{"model":{"type":"choice","choice":"anthropic/second"}},"providerMetadata":METADATA}"""
                .Replace("METADATA", providerMetadata ?? "null")),
            _ => CreateDecisionCostExecutionResponse(streaming, ExecutionCostMetadata)
        });
        using var client = CreateClient(http, CreateDecisionCostAgent());
        var (response, updates) = await RunDecisionCostAgentAsync(new ChatClientAgent(client), streaming);
        var metadata = ReadDecisionCostFinishMetadata(response);
        Assert.Equal(0.10m, metadata.GetProperty("gateway").GetProperty("cost").GetDecimal());
        Assert.Equal("execution", metadata.GetProperty("gateway").GetProperty("provider").GetString());
        await AssertDecisionCostMappingsAsync(response, updates, 0.10m);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Decision_cost_is_reported_without_execution_cost(bool streaming, bool zero)
    {
        using var http = CreateHttpClient(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1/models" => CreateJsonResponse(RoutingCatalog),
            "/api/decisions" => CreateJsonResponse("""
                {"answers":{"model":{"type":"choice","choice":"anthropic/second"}},"providerMetadata":{"gateway":{"cost":COST}}}
                """.Replace("COST", zero ? "0" : "0.02")),
            _ => CreateDecisionCostExecutionResponse(streaming, null)
        });
        using var client = CreateClient(http, CreateDecisionCostAgent());
        var (response, updates) = await RunDecisionCostAgentAsync(new ChatClientAgent(client), streaming);
        Assert.Equal(zero ? 0m : 0.02m, ReadDecisionCostFinishMetadata(response).GetProperty("gateway").GetProperty("cost").GetDecimal());
        await AssertDecisionCostMappingsAsync(response, updates, zero ? 0m : 0.02m);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Decision_cost_workflow_transfers_temporary_client_cost_and_combines_model_and_execution_costs(bool streaming, bool modelRouting)
    {
        var paths = new List<string>();
        using var http = CreateHttpClient(request =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath == "/v1/models") return CreateJsonResponse(RoutingCatalog);
            if (request.RequestUri.AbsolutePath == "/api/decisions")
            {
                using var json = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                return json.RootElement.GetProperty("questions").TryGetProperty("agent", out _)
                    ? CreateJsonResponse("""{"answers":{"agent":{"type":"choice","choice":"agent-1"}},"providerMetadata":{"gateway":{"cost":0.03}}}""")
                    : CreateJsonResponse("""{"answers":{"model":{"type":"refusal"}},"providerMetadata":{"gateway":{"cost":0.02}}}""");
            }
            return CreateDecisionCostExecutionResponse(streaming, ExecutionCostMetadata);
        });
        var selected = modelRouting ? CreateDecisionCostAgent() : CreateAgent();
        var request = new ChatRuntimeRequest(CreateUserMessages("Route me").ToList(), null, null,
            [CreateAgent(name: "Unselected"), selected], "decisions", new() { Decisions = new() { Model = "openai/workflow-router" } });
        var orchestrator = new ChatRuntimeOrchestrator(new StreamingContentMapper(), new ModelCatalog([]));
        var clients = new List<AgentChatClient>();
        try
        {
            var context = await orchestrator.PrepareAsync(new DefaultHttpContext().Response, request, agent =>
            {
                var client = CreateClient(http, agent);
                clients.Add(client);
                return client;
            });
            Assert.Equal(2, clients.Count);
            Assert.Same(selected, Assert.Single(context.ResolvedAgents));
            List<AgentResponseUpdate>? updates = streaming ? await CollectAsync(orchestrator.StreamAgentAsync(context)) : null;
            var response = updates is null ? await orchestrator.RunAgentAsync(context) : updates.ToAgentResponse();
            var expected = modelRouting ? 0.15m : 0.13m;
            Assert.Equal(expected, ReadDecisionCostFinishMetadata(response).GetProperty("gateway").GetProperty("cost").GetDecimal());
            await AssertDecisionCostMappingsAsync(response, updates, expected);

            // Reusing the discarded transport must not emit the transferred cost again.
            var (routerResponse, _) = await RunDecisionCostAgentAsync(new ChatClientAgent(clients[0]), streaming);
            Assert.Equal(0.10m, ReadDecisionCostFinishMetadata(routerResponse).GetProperty("gateway").GetProperty("cost").GetDecimal());
            Assert.Equal(modelRouting ? 2 : 1, paths.Count(path => path == "/api/decisions"));
        }
        finally
        {
            foreach (var client in clients) client.Dispose();
        }
    }

    [Theory]
    [MemberData(nameof(DecisionCostOptionalMetadataCases))]
    public async Task Decision_cost_workflow_missing_invalid_or_zero_cost_keeps_execution_metadata(bool streaming, string? providerMetadata)
    {
        var configured = 0;
        using var http = CreateHttpClient(request => request.RequestUri!.AbsolutePath == "/api/decisions"
            ? CreateJsonResponse("""{"answers":{"agent":{"type":"choice","choice":"agent-1"}},"providerMetadata":METADATA}"""
                .Replace("METADATA", providerMetadata ?? "null"))
            : CreateDecisionCostExecutionResponse(streaming, ExecutionCostMetadata));
        var selected = CreateAgent(name: "Selected");
        var request = new ChatRuntimeRequest(CreateUserMessages("Route me").ToList(), null, null,
            [CreateAgent(name: "Unselected"), selected], "decisions", new() { Decisions = new() { Model = "openai/workflow-router" } });
        var orchestrator = new ChatRuntimeOrchestrator(new StreamingContentMapper(), new ModelCatalog([]));
        var clients = new List<AgentChatClient>();
        try
        {
            var context = await orchestrator.PrepareAsync(new DefaultHttpContext().Response, request, agent =>
            {
                var client = CreateClient(http, agent);
                clients.Add(client);
                return client;
            }, (_, _) => configured++);
            Assert.Same(selected, Assert.Single(context.ResolvedAgents));
            Assert.Equal(1, configured);
            List<AgentResponseUpdate>? updates = streaming ? await CollectAsync(orchestrator.StreamAgentAsync(context)) : null;
            var response = updates is null ? await orchestrator.RunAgentAsync(context) : updates.ToAgentResponse();
            var metadata = ReadDecisionCostFinishMetadata(response);
            Assert.Equal(0.10m, metadata.GetProperty("gateway").GetProperty("cost").GetDecimal());
            Assert.Equal("execution", metadata.GetProperty("gateway").GetProperty("provider").GetString());
            await AssertDecisionCostMappingsAsync(response, updates, 0.10m);
        }
        finally
        {
            foreach (var client in clients) client.Dispose();
        }
    }

    [Theory]
    [InlineData("refusal")]
    [InlineData("unknown")]
    public async Task Decision_cost_workflow_invalid_answer_still_fails_without_executing_an_agent(string answerType)
    {
        var requests = 0;
        var configured = 0;
        using var http = CreateHttpClient(request =>
        {
            requests++;
            Assert.Equal("/api/decisions", request.RequestUri!.AbsolutePath);
            var answer = answerType == "refusal" ? """{"type":"refusal"}""" : """{"type":"choice","choice":"unknown"}""";
            return CreateJsonResponse("""{"answers":{"agent":ANSWER},"providerMetadata":{"gateway":{"cost":0.03}}}""".Replace("ANSWER", answer));
        });
        var request = new ChatRuntimeRequest(CreateUserMessages("Route me").ToList(), null, null,
            [CreateAgent(name: "First"), CreateAgent(name: "Second")], "decisions", new() { Decisions = new() { Model = "openai/workflow-router" } });
        var orchestrator = new ChatRuntimeOrchestrator(new StreamingContentMapper(), new ModelCatalog([]));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.PrepareAsync(
            new DefaultHttpContext().Response, request, agent => CreateClient(http, agent), (_, _) => configured++));
        Assert.Contains("No agent was executed", error.Message);
        Assert.Equal(1, requests);
        Assert.Equal(0, configured);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Decision_cost_tool_loop_counts_one_decision_and_both_inference_steps(bool streaming)
    {
        var inference = 0;
        var toolCalls = 0;
        using var http = CreateHttpClient(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/models") return CreateJsonResponse(RoutingCatalog);
            if (request.RequestUri.AbsolutePath == "/api/decisions")
                return CreateJsonResponse("""{"answers":{"model":{"type":"choice","choice":"anthropic/second"}},"providerMetadata":{"gateway":{"cost":0.02}}}""");
            if (++inference > 1) return CreateDecisionCostExecutionResponse(streaming, ExecutionCostMetadata);
            const string call = """{"id":"fc_cost","type":"function_call","call_id":"call_cost","name":"cost_tool","arguments":"{}","status":"completed"}""";
            var response = """{"id":"resp_tool","object":"response","status":"completed","model":"cost-model","output":[CALL],"metadata":METADATA}"""
                .Replace("CALL", call).Replace("METADATA", ExecutionCostMetadata);
            return streaming
                ? CreateStreamingResponse("event: response.output_item.done\ndata: {\"type\":\"response.output_item.done\",\"sequence_number\":1,\"output_index\":0,\"item\":" + call
                    + "}\n\nevent: response.completed\ndata: {\"type\":\"response.completed\",\"sequence_number\":2,\"response\":" + response + "}\n\ndata: [DONE]\n\n")
                : CreateJsonResponse(response);
        });
        using var client = CreateClient(http, CreateDecisionCostAgent());
        var agent = new ChatClientAgent(client, tools: [AIFunctionFactory.Create(() => { toolCalls++; return "done"; }, "cost_tool")]);
        var (response, updates) = await RunDecisionCostAgentAsync(agent, streaming);
        Assert.Equal(2, inference);
        Assert.Equal(1, toolCalls);
        var costs = response.Messages.SelectMany(message => message.Contents).OfType<DataContent>()
            .Where(data => data.Name == "finish_metadata")
            .Select(data => JsonSerializer.Deserialize<JsonElement>(data.Data.Span).GetProperty("gateway").GetProperty("cost").GetDecimal()).ToList();
        Assert.Equal(new[] { 0.12m, 0.10m }, costs);
        await AssertDecisionCostMappingsAsync(response, updates, 0.22m);
    }

    [Theory]
    [InlineData(false, "providerMetadata", false)]
    [InlineData(true, "providerMetadata", false)]
    [InlineData(false, "provider_metadata", false)]
    [InlineData(true, "provider_metadata", false)]
    [InlineData(false, "providerMetadata", true)]
    [InlineData(true, "providerMetadata", true)]
    public async Task Decision_cost_preserves_provider_scoped_gateway_precedence_and_metadata(bool streaming, string providerKey, bool directAlias)
    {
        var metadata = """
            {"PROVIDERS":{"anthropic":{"gateway":{"cost":0.45,"currency":"EUR","provider":"scoped"},"keep":"provider-field"}},"custom":"keep"DIRECT}
            """.Replace("PROVIDERS", providerKey)
            .Replace("DIRECT", directAlias ? ",\"gateway\":{\"cost\":0.01,\"provider\":\"direct\"}" : "");
        using var http = CreateHttpClient(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1/models" => CreateJsonResponse(RoutingCatalog),
            "/api/decisions" => CreateJsonResponse("""{"answers":{"model":{"type":"choice","choice":"anthropic/second"}},"providerMetadata":{"gateway":{"cost":0.02}}}"""),
            _ => CreateDecisionCostExecutionResponse(streaming, metadata)
        });
        using var client = CreateClient(http, CreateDecisionCostAgent());
        var (response, updates) = await RunDecisionCostAgentAsync(new ChatClientAgent(client), streaming);
        var finish = ReadDecisionCostFinishMetadata(response);
        var scoped = finish.GetProperty(providerKey).GetProperty("anthropic");
        Assert.Equal("provider-field", scoped.GetProperty("keep").GetString());
        Assert.Equal(0.47m, scoped.GetProperty("gateway").GetProperty("cost").GetDecimal());
        Assert.Equal("scoped", scoped.GetProperty("gateway").GetProperty("provider").GetString());
        Assert.Equal("EUR", scoped.GetProperty("gateway").GetProperty("currency").GetString());
        // Direct/scoped aliases retain their own execution costs. Native Responses
        // continues to prefer the scoped cost instead of adding both aliases.
        await AssertDecisionCostMappingsAsync(response, updates, 0.47m, directAlias ? 0.03m : 0.47m);
    }

    [Fact]
    public async Task Decision_cost_streaming_waits_for_completion_and_is_not_lost_after_failed_stream()
    {
        var inference = 0;
        using var http = CreateHttpClient(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1/models" => CreateJsonResponse(RoutingCatalog),
            "/api/decisions" => CreateJsonResponse("""{"answers":{"model":{"type":"choice","choice":"anthropic/second"}},"providerMetadata":{"gateway":{"cost":0.02}}}"""),
            _ => ++inference == 1
                ? CreateStreamingResponse("event: response.failed\ndata: {\"type\":\"response.failed\",\"sequence_number\":1,\"response\":{\"id\":\"failed\",\"status\":\"failed\",\"error\":{\"message\":\"original error\"}}}\n\ndata: [DONE]\n\n")
                : CreateDecisionCostExecutionResponse(true, ExecutionCostMetadata)
        });
        using var client = CreateClient(http, CreateDecisionCostAgent());
        var failed = await CollectAsync(client.GetStreamingResponseAsync(CreateUserMessages("First")));
        Assert.Single(failed.SelectMany(update => update.Contents).OfType<ErrorContent>());
        Assert.DoesNotContain(failed.SelectMany(update => update.Contents).OfType<DataContent>(), data => data.Name == "finish_metadata");
        var completed = await CollectAsync(client.GetStreamingResponseAsync(CreateUserMessages("Retry")));
        var final = Assert.Single(completed, update => update.FinishReason == ChatFinishReason.Stop);
        Assert.All(completed.Where(update => update != final), update => Assert.DoesNotContain(update.Contents.OfType<DataContent>(), data => data.Name == "finish_metadata"));
        var data = Assert.Single(final.Contents.OfType<DataContent>(), data => data.Name == "finish_metadata");
        Assert.Equal(0.12m, JsonSerializer.Deserialize<JsonElement>(data.Data.Span).GetProperty("gateway").GetProperty("cost").GetDecimal());
    }

    private static Agent CreateDecisionCostAgent()
    {
        var agent = CreateAgent();
        agent.Models = [Answer("openai/router", "decision"), Answer("openai/first", "language"), Answer("anthropic/second", "language")];
        return agent;
    }

    private static HttpResponseMessage CreateDecisionCostExecutionResponse(bool streaming, string? metadata)
    {
        var response = $$"""
            {"id":"resp_cost","object":"response","created_at":1735689600,"status":"completed","model":"cost-model","output":[{"id":"msg_cost","type":"message","role":"assistant","status":"completed","content":[{"type":"output_text","text":"Hello cost","annotations":[]}]}],"usage":{"input_tokens":3,"output_tokens":4,"total_tokens":7},"metadata":{{metadata ?? "null"}}}
            """;
        return streaming
            ? CreateStreamingResponse("event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"sequence_number\":1,\"item_id\":\"msg_cost\",\"output_index\":0,\"content_index\":0,\"delta\":\"Hello cost\"}\n\nevent: response.completed\ndata: {\"type\":\"response.completed\",\"sequence_number\":2,\"response\":" + response + "}\n\ndata: [DONE]\n\n")
            : CreateJsonResponse(response);
    }

    private static async Task<(AgentResponse Response, List<AgentResponseUpdate>? Updates)> RunDecisionCostAgentAsync(AIAgent agent, bool streaming)
    {
        if (!streaming) return (await agent.RunAsync(CreateUserMessages("Choose and answer")), null);
        var updates = await CollectAsync(agent.RunStreamingAsync(CreateUserMessages("Choose and answer")));
        return (updates.ToAgentResponse(), updates);
    }

    private static JsonElement ReadDecisionCostFinishMetadata(AgentResponse response)
    {
        var content = Assert.Single(response.Messages.SelectMany(message => message.Contents).OfType<DataContent>(), data => data.Name == "finish_metadata");
        return JsonSerializer.Deserialize<JsonElement>(content.Data.Span);
    }

    private static async Task AssertDecisionCostMappingsAsync(AgentResponse response, List<AgentResponseUpdate>? updates, decimal expectedNativeCost, decimal? expectedUiCost = null)
    {
        var request = new ResponseRequest { Model = "agent", Input = new ResponseInput("Choose and answer") };
        ResponseResult mapped;
        if (updates is null)
            mapped = new ResponsesNativeMapper().Map(request, "CostAgent", "openai", response);
        else
        {
            var native = await CollectAsync(new ResponsesNativeMapper().MapStreamingAsync(request, "CostAgent", "openai", ToAsync(updates)));
            mapped = Assert.Single(native.OfType<ResponseCompleted>()).Response;
        }
        var gateway = JsonSerializer.SerializeToElement(GetProviderMetadataGateway(mapped.Metadata!));
        Assert.Equal(expectedNativeCost, gateway.GetProperty("cost").GetDecimal());
        Assert.False(mapped.Metadata!.ContainsKey("gateway"));

        var uiUpdates = updates ?? response.Messages.Select(message => new AgentResponseUpdate(message.Role, message.Contents)).ToList();
        var ui = await CollectAsync(new StreamingContentMapper().MapAsync(ToAsync(uiUpdates)));
        var finish = Assert.Single(ui.OfType<FinishUIPart>());
        Assert.Equal(expectedUiCost ?? expectedNativeCost, finish.MessageMetadata!.Gateway!.Cost);
        Assert.DoesNotContain(ui.OfType<DataUIPart>(), part => part.Type == "data-finish_metadata");
    }
}
