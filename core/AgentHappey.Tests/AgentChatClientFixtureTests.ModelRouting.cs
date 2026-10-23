using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AgentHappey.Core.ChatClient;
using Microsoft.Extensions.AI;
using ConfiguredModel = AgentHappey.Common.Models.AIModel;

namespace AgentHappey.Tests;

public sealed partial class AgentChatClientFixtureTests
{
    private const string RoutingCatalog = """
        {"data":[
          {"id":"openai/router","type":"decision","description":"Router"},
          {"id":"openai/second-router","type":"decision"},
          {"id":"openai/first","type":"language","name":"Fast","description":"Fast general chat","context_window":120000,"pricing":{"input":0.1},"providerHeaders":{"secret":"catalog-SECRET"}},
          {"id":"anthropic/second","type":"language","name":"Reasoner","description":"Deep analysis","tags":["reasoning"]},
          {"id":"google/legacy","type":"language"}]}
        """;

    private static ConfiguredModel Answer(string id, string? type = null) => new() { Id = id, Type = type };

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Model_routing_uses_catalog_then_api_decisions_once_and_keeps_selected_configuration(bool streaming, bool misleadingSavedTypes)
    {
        var paths = new List<string>();
        var inferenceCount = 0;
        using var http = CreateHttpClient(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            paths.Add(path);
            Assert.Equal("azure-token", request.Headers.Authorization?.Parameter);
            Assert.DoesNotContain(request.Headers.SelectMany(pair => pair.Value), value => value.Contains("unrelated-SECRET"));
            if (path == "/v1/models")
            {
                Assert.Equal("openai-key", Assert.Single(request.Headers.GetValues("X-OpenAI-Key")));
                Assert.Equal("anthropic-key", Assert.Single(request.Headers.GetValues("X-Anthropic-Key")));
                return CreateJsonResponse(RoutingCatalog);
            }
            var json = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (path == "/api/decisions")
            {
                Assert.Equal("openai/router", root.GetProperty("model").GetString());
                Assert.False(request.Headers.Contains("X-Anthropic-Key"));
                Assert.Equal("router-setting", Assert.Single(request.Headers.GetValues("X-Router-Setting")));
                Assert.Equal("low", root.GetProperty("providerOptions").GetProperty("openai").GetProperty("reasoning_effort").GetString());
                var criteria = root.GetProperty("questions").GetProperty("model").GetProperty("criteria");
                Assert.Equal(2, criteria.EnumerateObject().Count());
                Assert.Contains("Fast general chat", json);
                Assert.Contains("Deep analysis", json);
                Assert.DoesNotContain("SECRET", json);
                Assert.DoesNotContain("data:image", json);
                return CreateJsonResponse("""{"answers":{"model":{"type":"choice","choice":"anthropic/second"}}}""");
            }
            Assert.Equal("/v1/responses", path);
            inferenceCount++;
            Assert.Equal("anthropic/second", root.GetProperty("model").GetString());
            Assert.Equal(0.25, root.GetProperty("temperature").GetDouble());
            Assert.Equal(42, root.GetProperty("metadata").GetProperty("anthropic").GetProperty("max_tokens").GetInt32());
            Assert.Equal("selected-setting", Assert.Single(request.Headers.GetValues("X-Selected-Setting")));
            Assert.False(request.Headers.Contains("X-OpenAI-Key"));
            Assert.False(request.Headers.Contains("X-Router-Setting"));
            return streaming ? CreateStreamingResponse(LoadFixture(StreamingFixturePath)) : CreateJsonResponse(LoadFixture(StructuredFixturePath));
        });
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "azure-token");
        http.DefaultRequestHeaders.Add("X-Unrelated-Key", "unrelated-SECRET");
        var first = Answer("openai/first");
        var selected = Answer("anthropic/second");
        selected.Options = new() { Temperature = 0.25f };
        selected.ProviderMetadata = new() { ["max_tokens"] = 42 };
        selected.ProviderHeaders = new() { ["X-Selected-Setting"] = "selected-setting" };
        var router = Answer("openai/router");
        router.ProviderMetadata = new() { ["reasoning_effort"] = "low" };
        router.ProviderHeaders = new() { ["X-Router-Setting"] = "router-setting" };
        var agent = CreateAgent(modelId: "google/legacy");
        agent.Models = [router, first, selected, first, Answer("openai/second-router")];
        if (misleadingSavedTypes)
        {
            router.Type = "language";
            first.Type = selected.Type = "decision";
        }
        using var client = new AgentChatClient(http, new StaticHttpClientFactory(http), agent, new Dictionary<string, string?>
        {
            ["X-OpenAI-Key"] = "openai-key", ["X-Anthropic-Key"] = "anthropic-key"
        });
        var messages = CreateUserMessages("Analyze this problem").ToList();
        messages[0].Contents.Add(new DataContent(new byte[] { 1, 2, 3 }, "image/png"));
        for (var turn = 0; turn < 2; turn++)
            if (streaming) await CollectAsync(client.GetStreamingResponseAsync(messages));
            else await client.GetResponseAsync(messages);
        Assert.Equal(2, inferenceCount);
        Assert.Equal(["/v1/models", "/api/decisions", "/v1/responses", "/v1/responses"], paths);
        Assert.Equal("google/legacy", agent.Model!.Id); // Stored/shared definitions are not rewritten.
    }

    [Theory]
    [InlineData(false, "single")]
    [InlineData(true, "single")]
    [InlineData(false, "typed-language")]
    [InlineData(true, "typed-language")]
    [InlineData(false, "typed-decision")]
    [InlineData(true, "typed-decision")]
    [InlineData(false, "duplicates")]
    [InlineData(true, "duplicates")]
    [InlineData(false, "normalized")]
    [InlineData(true, "normalized")]
    [InlineData(false, "collection-only")]
    [InlineData(true, "collection-only")]
    public async Task Model_routing_single_distinct_model_executes_directly_and_keeps_configuration(bool streaming, string scenario)
    {
        var paths = new List<string>();
        using var http = CreateHttpClient(request =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            Assert.Equal("/v1/responses", request.RequestUri.AbsolutePath);
            using var json = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal("openai/first", json.RootElement.GetProperty("model").GetString());
            Assert.Equal(0.25, json.RootElement.GetProperty("temperature").GetDouble());
            Assert.Equal(42, json.RootElement.GetProperty("metadata").GetProperty("openai").GetProperty("max_tokens").GetInt32());
            Assert.Equal("selected-setting", Assert.Single(request.Headers.GetValues("X-Selected-Setting")));
            return streaming ? CreateStreamingResponse(LoadFixture(StreamingFixturePath)) : CreateJsonResponse(LoadFixture(StructuredFixturePath));
        });
        var selected = Answer("openai/first", scenario switch
        {
            "typed-language" => "language",
            "typed-decision" => "decision",
            _ => null
        });
        selected.Options = new() { Temperature = 0.25f };
        selected.ProviderMetadata = new() { ["max_tokens"] = 42 };
        selected.ProviderHeaders = new() { ["X-Selected-Setting"] = "selected-setting" };
        var agent = CreateAgent(modelId: "google/legacy");
        agent.Models = scenario switch
        {
            "duplicates" => [selected, Answer(selected.Id, "decision")],
            "normalized" => [null!, Answer(""), Answer(" "), selected, Answer(selected.Id)],
            _ => [selected]
        };
        if (scenario == "collection-only") agent.Model = null;
        var original = JsonSerializer.Serialize(agent);
        using var client = CreateClient(http, agent);
        var messages = CreateUserMessages("hello").ToList();

        await client.InitializeModelAsync(messages);
        Assert.Empty(paths);
        for (var turn = 0; turn < 2; turn++)
            if (streaming) await CollectAsync(client.GetStreamingResponseAsync(messages));
            else await client.GetResponseAsync(messages);

        Assert.Equal(["/v1/responses", "/v1/responses"], paths);
        Assert.Equal(original, JsonSerializer.Serialize(agent));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("typed-none")]
    [InlineData("one")]
    [InlineData("typed-one")]
    [InlineData("duplicates")]
    [InlineData("empty")]
    [InlineData("legacy")]
    public async Task Model_routing_fast_paths_preserve_legacy_and_collection_precedence(string scenario)
    {
        var catalogCalls = 0;
        var agent = CreateAgent(modelId: "google/legacy");
        agent.Models = scenario switch
        {
            "none" => [Answer("openai/first"), Answer("anthropic/second")],
            "typed-none" => [Answer("openai/first", "language"), Answer("anthropic/second", "language")],
            "one" => [Answer("openai/router"), Answer("openai/first")],
            "typed-one" => [Answer("openai/router", "decision"), Answer("openai/first", "language")],
            "duplicates" => [Answer("openai/router"), Answer("openai/first"), Answer("openai/first")],
            "empty" => [],
            _ => null
        };
        if (scenario == "one") agent.Model = null; // Collection-only wire contract.
        using var http = CreateHttpClient(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/models") { catalogCalls++; return CreateJsonResponse(RoutingCatalog); }
            Assert.Equal("/v1/responses", request.RequestUri.AbsolutePath);
            using var json = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal(scenario is "legacy" or "empty" ? "google/legacy" : "openai/first", json.RootElement.GetProperty("model").GetString());
            return CreateJsonResponse(LoadFixture(StructuredFixturePath));
        });
        using var client = CreateClient(http, agent);
        await client.GetResponseAsync(CreateUserMessages("hello"));
        Assert.Equal(scenario is "legacy" or "empty" ? 0 : 1, catalogCalls);
    }

    [Theory]
    [InlineData("refusal")]
    [InlineData("unknown")]
    [InlineData("malformed")]
    [InlineData("http")]
    [InlineData("catalog")]
    public async Task Model_routing_failures_gently_use_first_answer(string failure)
    {
        var catalogCalls = 0;
        var decisionCalls = 0;
        using var http = CreateHttpClient(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/models")
            {
                catalogCalls++;
                return failure == "catalog" ? CreateJsonResponse("{}", HttpStatusCode.ServiceUnavailable) : CreateJsonResponse(RoutingCatalog);
            }
            if (request.RequestUri.AbsolutePath == "/api/decisions")
            {
                decisionCalls++;
                return failure switch
                {
                    "refusal" => CreateJsonResponse("""{"answers":{"model":{"type":"refusal"}}}"""),
                    "unknown" => CreateJsonResponse("""{"answers":{"model":{"type":"choice","choice":"openai/router"}}}"""),
                    "http" => CreateJsonResponse("{}", HttpStatusCode.Unauthorized),
                    _ => CreateJsonResponse("not json")
                };
            }
            Assert.Equal("/v1/responses", request.RequestUri.AbsolutePath);
            using var json = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal("openai/first", json.RootElement.GetProperty("model").GetString());
            return CreateJsonResponse(LoadFixture(StructuredFixturePath));
        });
        var agent = CreateAgent(modelId: "google/legacy");
        agent.Model = null;
        agent.Models = [Answer("openai/router", "decision"), Answer("openai/first", "language"), Answer("anthropic/second", "language")];
        using var client = CreateClient(http, agent);
        await client.GetResponseAsync(CreateUserMessages("hello"));
        Assert.Equal(1, catalogCalls);
        Assert.Equal(failure == "catalog" ? 0 : 1, decisionCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Model_routing_single_model_honors_pre_cancelled_requests_without_http(bool streaming)
    {
        var requests = 0;
        using var http = CreateHttpClient(_ =>
        {
            requests++;
            return CreateJsonResponse(LoadFixture(StructuredFixturePath));
        });
        var agent = CreateAgent();
        agent.Models = [Answer("openai/first")];
        using var client = CreateClient(http, agent);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        if (streaming)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CollectAsync(client.GetStreamingResponseAsync(CreateUserMessages("hello"), cancellationToken: cancellation.Token)));
        else
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetResponseAsync(CreateUserMessages("hello"), cancellationToken: cancellation.Token));

        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task Model_routing_honors_caller_cancellation_without_inference()
    {
        using var cancellation = new CancellationTokenSource();
        using var http = CreateHttpClient(request =>
        {
            Assert.Equal("/v1/models", request.RequestUri!.AbsolutePath);
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });
        var agent = CreateAgent(); agent.Models = [Answer("openai/first"), Answer("anthropic/second")];
        using var client = CreateClient(http, agent);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetResponseAsync(CreateUserMessages("hello"), cancellationToken: cancellation.Token));
    }
}
