using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using AgentHappey.Core.ChatClient;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AgentHappey.Tests;

public sealed partial class AgentChatClientFixtureTests
{
    private static HttpResponseMessage SearchDecisionResponse(JsonElement questions, bool equal = false)
    {
        var answers = questions.EnumerateObject().ToDictionary(question => question.Name, question =>
        {
            var keys = question.Value.GetProperty("criteria").EnumerateObject().Select(property => property.Name).ToList();
            var sum = equal ? keys.Count : keys.Count * (keys.Count + 1) / 2d;
            return (object)new
            {
                type = "choice", choice = keys[^1],
                probabilities = keys.Select((key, index) => (key, index))
                    .ToDictionary(item => item.key, item => (equal ? 1 : item.index + 1) / sum)
            };
        });
        return CreateJsonResponse(JsonSerializer.Serialize(new
        {
            answers, warnings = Array.Empty<object>(), providerMetadata = new { gateway = new { cost = 0.02m } }
        }));
    }

    private static async Task<AIFunction> SeedSearchToolsAsync(AgentChatClient client, int count = 12)
    {
        var tools = await client.ConnectMcp(default);
        var search = Assert.Single(tools.OfType<AIFunction>(), tool => tool.Name == "client_tool_search");
        var catalog = GetPrivateField<ConcurrentDictionary<string, AITool>>(client, "Tools");
        catalog.Clear();
        for (var index = 0; index < count; index++)
        {
            var tool = AIFunctionFactory.Create((string value) => value, $"tool_{index}", $"Capability {index}");
            catalog[tool.Name] = tool;
        }
        return search;
    }

    private static CallToolResult ReadSearchToolResult(object? result)
        => JsonSerializer.SerializeToElement(result).Deserialize<CallToolResult>()!;

    private static Task<CallToolResult> InvokeResourceSearchAsync(AgentChatClient client, string query = "Mail",
        string serverUrl = TestMcpUrl, CancellationToken cancellationToken = default)
        => (Task<CallToolResult>)typeof(AgentChatClient).GetMethod("SearchResourcesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(client, [serverUrl, query, cancellationToken])!;

    private static void SeedSearchResources(AgentChatClient client, int count = 22, bool templatesOnly = false)
    {
        // Search only checks connected identity; it must not read or execute an MCP resource.
        GetPrivateField<ConcurrentDictionary<string, McpClient>>(client, "McpClients")[TestMcpUrl] = null!;
        GetPrivateField<ConcurrentDictionary<string, string>>(client, "McpServerInstructions")[TestMcpUrl] = "Server search guidance";
        GetPrivateField<ConcurrentDictionary<string, IEnumerable<object>>>(client, "McpServerResources")[TestMcpUrl] = templatesOnly ? [] :
            Enumerable.Range(0, count).Select(index => (object)new { uri = $"mail://{index}", name = $"Mail {index}", _meta = new { keep = index } })
                .Append(new { uri = "secret://hidden", annotations = new { audience = new[] { "user" } } }).ToList();
        GetPrivateField<ConcurrentDictionary<string, IEnumerable<object>>>(client, "McpServerResourceTemplates")[TestMcpUrl] =
            Enumerable.Range(0, count).Select(index => (object)new { uriTemplate = $"mail://{index}", name = $"Template {index}", mimeType = "text/plain" }).ToList();
        GetPrivateField<ConcurrentDictionary<string, IEnumerable<object>>>(client, "McpServerResources")["https://other/server"] =
            [new { uri = "other://server", name = "Not authorized for this search" }];
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Search_decisions_use_catalog_router_independently_of_answer_routing_and_keep_tool_cap(bool multipleAnswers, bool equal)
    {
        var paths = new List<string>();
        var offered = new List<string>();
        using var http = CreateHttpClient(request =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath == "/v1/models") return CreateJsonResponse(RoutingCatalog);
            Assert.Equal("/api/decisions", request.RequestUri.AbsolutePath);
            Assert.Equal("token", request.Headers.Authorization?.Parameter);
            Assert.Equal("router-key", Assert.Single(request.Headers.GetValues("X-OpenAI-Key")));
            Assert.False(request.Headers.Contains("X-Anthropic-Key"));
            Assert.Equal("router-setting", Assert.Single(request.Headers.GetValues("X-Router-Setting")));
            using var document = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var root = document.RootElement;
            Assert.Equal("openai/router", root.GetProperty("model").GetString());
            Assert.Equal("low", root.GetProperty("providerOptions").GetProperty("openai").GetProperty("effort").GetString());
            var questions = root.GetProperty("questions");
            if (questions.TryGetProperty("model", out _))
                return CreateJsonResponse("""{"answers":{"model":{"type":"choice","choice":"anthropic/second"}},"providerMetadata":{"gateway":{"cost":0.02}}}""");
            Assert.Single(questions.EnumerateObject());
            var question = questions.GetProperty("tools");
            Assert.Equal("choice", question.GetProperty("type").GetString());
            Assert.Equal("Find tools", root.GetProperty("state").GetProperty("goal").GetString());
            Assert.DoesNotContain("router-key", root.GetRawText());
            offered = question.GetProperty("criteria").EnumerateObject().Select(property =>
            {
                Assert.Equal("object", property.Value.GetProperty("inputSchema").GetProperty("type").GetString());
                return property.Value.GetProperty("name").GetString()!;
            }).ToList();
            return SearchDecisionResponse(questions, equal);
        });
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "token");
        var definition = CreateAgent(modelId: "google/legacy");
        var router = Answer("openai/router", "language"); // Catalog classification overrides misleading saved hints.
        router.ProviderMetadata = new() { ["effort"] = "low" };
        router.ProviderHeaders = new() { ["X-Router-Setting"] = "router-setting" };
        definition.Models = [router, Answer("openai/first", "decision"), Answer("openai/second-router", "decision")];
        if (multipleAnswers) definition.Models = [.. definition.Models, Answer("anthropic/second", "decision")];
        using var client = new AgentChatClient(http, new StaticHttpClientFactory(http), definition, new Dictionary<string, string?>
        {
            ["X-OpenAI-Key"] = "router-key", ["X-Anthropic-Key"] = "answer-key"
        });
        await client.InitializeModelAsync(CreateUserMessages("hello"));
        var function = await SeedSearchToolsAsync(client);
        var result = ReadSearchToolResult(await function.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["goal"] = "Find tools" })));
        Assert.False(result.IsError);
        var json = JsonSerializer.SerializeToElement(result);
        var selected = json.GetProperty("structuredContent").GetProperty("selectedTools").EnumerateArray().Select(tool => tool.GetProperty("name").GetString()).ToList();
        Assert.Equal((equal ? offered : offered.AsEnumerable().Reverse()).Take(10), selected);
        Assert.Equal(12, offered.Count);
        Assert.Equal(multipleAnswers ? 2 : 1, paths.Count(path => path == "/api/decisions"));
        Assert.Single(paths, path => path == "/v1/models");
        Assert.Equal(0.02m * (multipleAnswers ? 2 : 1), GetPrivateField<decimal?>(client, "pendingDecisionCost"));
        var effective = typeof(AgentChatClient).GetProperty("EffectiveModel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client);
        Assert.Equal(multipleAnswers ? "anthropic/second" : "openai/first", Assert.IsType<AgentHappey.Common.Models.AIModel>(effective).Id);
        Assert.Equal("google/legacy", definition.Model!.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Search_decisions_resource_categories_are_one_request_scoped_visible_and_capped(bool templatesOnly)
    {
        var calls = 0;
        using var http = CreateHttpClient(request =>
        {
            calls++;
            Assert.Equal("/api/decisions", request.RequestUri!.AbsolutePath);
            using var document = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var root = document.RootElement;
            Assert.DoesNotContain("secret://hidden", root.GetRawText());
            Assert.DoesNotContain("other://server", root.GetRawText());
            Assert.Equal("Server search guidance", root.GetProperty("state").GetProperty("mcpServerInstructions").GetString());
            Assert.Equal(templatesOnly ? 1 : 2, root.GetProperty("questions").EnumerateObject().Count());
            return SearchDecisionResponse(root.GetProperty("questions"));
        });
        var definition = CreateAgent(); definition.Models = [Answer("openai/router", "decision")];
        using var client = CreateClient(http, definition);
        SeedSearchResources(client, templatesOnly: templatesOnly);
        var result = await InvokeResourceSearchAsync(client);
        Assert.False(result.IsError);
        var payload = JsonSerializer.SerializeToElement(result).GetProperty("structuredContent");
        Assert.Equal(templatesOnly ? 0 : 20, payload.GetProperty("resources").GetArrayLength());
        Assert.Equal(20, payload.GetProperty("resourceTemplates").GetArrayLength());
        Assert.Equal("mail://21", payload.GetProperty("resourceTemplates")[0].GetProperty("uriTemplate").GetString());
        Assert.Equal("text/plain", payload.GetProperty("resourceTemplates")[0].GetProperty("mimeType").GetString());
        if (!templatesOnly) Assert.Equal(21, payload.GetProperty("resources")[0].GetProperty("_meta").GetProperty("keep").GetInt32());
        Assert.True((await InvokeResourceSearchAsync(client, serverUrl: "https://other/server")).IsError);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("legacy")]
    [InlineData("catalog-language")]
    [InlineData("offline-hints")]
    public async Task Search_decisions_preserve_LLM_search_without_decision_and_use_saved_hints_if_catalog_unavailable(string scenario)
    {
        var decisionCalls = 0;
        var inferenceCalls = 0;
        using var http = CreateHttpClient(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/models")
                return scenario == "offline-hints" ? CreateJsonResponse("{}", HttpStatusCode.ServiceUnavailable) : CreateJsonResponse(RoutingCatalog);
            using var document = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            if (request.RequestUri.AbsolutePath == "/api/decisions")
            {
                decisionCalls++;
                return SearchDecisionResponse(document.RootElement.GetProperty("questions"));
            }
            Assert.Equal("/v1/responses", request.RequestUri.AbsolutePath);
            inferenceCalls++;
            Assert.Equal("openai/first", document.RootElement.GetProperty("model").GetString());
            var text = document.RootElement.GetRawText().Contains("selectedResourceUris", StringComparison.Ordinal)
                ? "{\"selectedResourceUris\":[\"mail://0\"],\"selectedResourceTemplateUriTemplates\":[]}"
                : "{\"selectedToolNames\":[\"tool_0\"]}";
            return SearchModelResponse(false, new { type = "message", id = "selection", role = "assistant", content = new[] { new { type = "output_text", text } } });
        });
        var definition = CreateAgent(modelId: "openai/first");
        if (scenario == "catalog-language") definition.Models = [Answer("openai/first", "decision"), Answer("anthropic/second", "decision")];
        if (scenario == "offline-hints") definition.Models = [Answer("openai/router", "decision"), Answer("openai/first", "language")];
        using var client = CreateClient(http, definition);
        await client.InitializeModelAsync(CreateUserMessages("hello"));
        var function = await SeedSearchToolsAsync(client, 1);
        var result = ReadSearchToolResult(await function.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["goal"] = "Find tools" })));
        Assert.False(result.IsError);
        SeedSearchResources(client, 1);
        Assert.False((await InvokeResourceSearchAsync(client)).IsError);
        Assert.Equal(scenario == "offline-hints" ? 2 : 0, decisionCalls);
        Assert.Equal(scenario == "offline-hints" ? 0 : 2, inferenceCalls);
    }

    public static IEnumerable<object[]> InvalidSearchDecisionAnswers()
    {
        string[] values = [
            """{"type":"refusal"}""",
            """{"type":"choice","choice":"0"}""",
            """{"type":"choice","choice":"0","probabilities":{"0":1}}""",
            """{"type":"choice","choice":"outside","probabilities":{"0":0.5,"1":0.5}}""",
            """{"type":"choice","choice":"0","probabilities":{"0":0.5,"outside":0.5}}""",
            """{"type":"choice","choice":"0","probabilities":{"0":2,"1":-1}}""",
            """{"type":"choice","choice":"0","probabilities":{"0":0.6,"1":0.6}}""",
            """{"type":"choice","choice":"0","probabilities":{"0":0.2,"1":0.8}}""",
            """{"type":"boolean","probability":0.9}""",
            "http", "not-json"
        ];
        foreach (var resource in new[] { false, true })
            foreach (var value in values) yield return [resource, value];
    }

    [Theory]
    [MemberData(nameof(InvalidSearchDecisionAnswers))]
    public async Task Search_decisions_fail_closed_without_LLM_retry(bool resource, string answer)
    {
        var calls = 0;
        using var http = CreateHttpClient(request =>
        {
            calls++;
            Assert.Equal("/api/decisions", request.RequestUri!.AbsolutePath);
            if (answer == "http") return CreateJsonResponse("{}", HttpStatusCode.ServiceUnavailable);
            if (answer == "not-json") return CreateJsonResponse("not-json");
            return CreateJsonResponse("{\"answers\":{\"" + (resource ? "resourceTemplates" : "tools") + "\":" + answer + "}}");
        });
        var definition = CreateAgent(); definition.Models = [Answer("openai/router", "decision")];
        using var client = CreateClient(http, definition);
        var function = await SeedSearchToolsAsync(client, 2);
        SeedSearchResources(client, 2, templatesOnly: true);
        var result = resource ? await InvokeResourceSearchAsync(client)
            : ReadSearchToolResult(await function.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["goal"] = "Find tools" })));
        Assert.True(result.IsError);
        Assert.Null(result.StructuredContent);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Search_decisions_skip_empty_searches_and_propagate_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        using var http = CreateHttpClient(request =>
        {
            calls++;
            Assert.Equal("/api/decisions", request.RequestUri!.AbsolutePath);
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });
        var definition = CreateAgent(); definition.Models = [Answer("openai/router", "decision")];
        using var client = CreateClient(http, definition);
        var function = await SeedSearchToolsAsync(client, 0);
        var arguments = new AIFunctionArguments(new Dictionary<string, object?> { ["goal"] = "Find tools" });
        Assert.False(ReadSearchToolResult(await function.InvokeAsync(arguments)).IsError);
        SeedSearchResources(client, 0);
        Assert.False((await InvokeResourceSearchAsync(client)).IsError);
        Assert.Equal(0, calls);
        await SeedSearchToolsAsync(client, 1);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await function.InvokeAsync(arguments, cancellation.Token));
        Assert.Equal(1, calls);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InvokeResourceSearchAsync(client, cancellationToken: cancellation.Token));
        Assert.Equal(1, calls);
    }
}
