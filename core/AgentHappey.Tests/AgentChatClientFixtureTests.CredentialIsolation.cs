using System.Net.Http.Headers;
using System.Reflection;
using AgentHappey.Common.Models;
using AgentHappey.Core.ChatClient;

namespace AgentHappey.Tests;

public sealed partial class AgentChatClientFixtureTests
{
    [Theory]
    [InlineData("openai/model", true)]
    [InlineData("opaque-model", false)]
    public async Task Credential_isolation_suppresses_unrelated_shared_defaults_and_unknown_model_keys(string modelId, bool expectsOpenAi)
    {
        using var http = CreateHttpClient(request =>
        {
            Assert.DoesNotContain(request.Headers.SelectMany(header => header.Value), value => value.Contains("unrelated-SECRET"));
            Assert.Equal("azure-token", request.Headers.Authorization?.Parameter);
            var values = request.Headers.TryGetValues("X-OpenAI-Key", out var found) ? found.Where(value => !string.IsNullOrEmpty(value)).ToArray() : [];
            Assert.Equal(expectsOpenAi ? ["configured-openai-key"] : Array.Empty<string>(), values);
            return CreateJsonResponse(LoadFixture(StructuredFixturePath));
        });
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "azure-token");
        http.DefaultRequestHeaders.Add("X-OpenAI-Key", "configured-openai-key");
        http.DefaultRequestHeaders.Add("X-Anthropic-Key", "unrelated-SECRET");
        using var client = CreateClient(http, CreateAgent(modelId: modelId));
        await client.GetResponseAsync(CreateUserMessages("hello"));
        Assert.Equal("unrelated-SECRET", Assert.Single(http.DefaultRequestHeaders.GetValues("X-Anthropic-Key")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Credential_isolation_scopes_each_agent_without_mutating_shared_defaults(bool streaming)
    {
        var captured = new List<Dictionary<string, string>>();
        using var http = CreateHttpClient(request =>
        {
            captured.Add(request.Headers.ToDictionary(pair => pair.Key, pair => string.Join(",", pair.Value), StringComparer.OrdinalIgnoreCase));
            return streaming ? CreateStreamingResponse(LoadFixture(StreamingFixturePath)) : CreateJsonResponse(LoadFixture(StructuredFixturePath));
        });
        var inherited = new Dictionary<string, string?>
        {
            ["X-OpenAI-Key"] = "openai-SECRET", ["X-Anthropic-Key"] = "anthropic-SECRET",
            ["X-OpenAIHK-Key"] = "other-SECRET", ["X-Request-Id"] = "correlation"
        };
        using var openai = new AgentChatClient(http, new StaticHttpClientFactory(http), CreateAgent(), inherited);
        using var anthropic = new AgentChatClient(http, new StaticHttpClientFactory(http), CreateAgent(modelId: "anthropic/model"), inherited);
        async Task Run(AgentChatClient client)
        {
            if (streaming) { await foreach (var update in client.GetStreamingResponseAsync(CreateUserMessages("hello"))) { } }
            else await client.GetResponseAsync(CreateUserMessages("hello"));
        }
        await Task.WhenAll(Run(openai), Run(anthropic));
        await Run(openai);
        Assert.Equal(3, captured.Count);
        Assert.Equal("openai-SECRET", captured[0]["X-OpenAI-Key"]);
        Assert.False(captured[0].ContainsKey("X-Anthropic-Key"));
        Assert.Equal("anthropic-SECRET", captured[1]["X-Anthropic-Key"]);
        Assert.False(captured[1].ContainsKey("X-OpenAI-Key"));
        Assert.All(captured, item => Assert.False(item.ContainsKey("X-OpenAIHK-Key")));
        Assert.All(captured, item => Assert.Equal("correlation", item["X-Request-Id"]));
        Assert.False(http.DefaultRequestHeaders.Contains("X-OpenAI-Key"));
        Assert.False(http.DefaultRequestHeaders.Contains("X-Agent-Name"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Credential_isolation_preserves_AzureAuth_bearer_and_explicit_model_credentials(bool streaming)
    {
        Dictionary<string, string>? captured = null;
        using var http = CreateHttpClient(request =>
        {
            captured = request.Headers.ToDictionary(pair => pair.Key, pair => string.Join(",", pair.Value), StringComparer.OrdinalIgnoreCase);
            return streaming ? CreateStreamingResponse(LoadFixture(StreamingFixturePath)) : CreateJsonResponse(LoadFixture(StructuredFixturePath));
        });
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "azure-downstream-token");
        var agent = CreateAgent(providerHeaders: new() { ["X-Api-Key"] = "configured-provider-credential", ["X-Provider-Route"] = "configured-route" });
        using var client = CreateClient(http, agent); // AzureAuth supplies no inherited HeaderAuth key collection.
        if (streaming) { await foreach (var update in client.GetStreamingResponseAsync(CreateUserMessages("hello"))) { } }
        else await client.GetResponseAsync(CreateUserMessages("hello"));
        Assert.Equal("Bearer azure-downstream-token", captured!["Authorization"]);
        Assert.Equal("configured-provider-credential", captured["X-Api-Key"]);
        Assert.Equal("configured-route", captured["X-Provider-Route"]);
        Assert.Equal("StructuredAgent", captured["X-Agent-Name"]);
        Assert.Equal("azure-downstream-token", http.DefaultRequestHeaders.Authorization.Parameter);
    }

    [Fact]
    public void Credential_isolation_MCP_uses_only_explicit_connection_requirements_and_preserves_AzureAuth()
    {
        using var inference = new HttpClient { BaseAddress = new Uri("https://backend.test/") };
        inference.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "azure-downstream-token");
        using var client = new AgentChatClient(inference, new StaticHttpClientFactory(inference), CreateAgent(), new Dictionary<string, string?>
        {
            ["X-OpenAI-Key"] = "unrelated-SECRET", ["X-Connection-Key"] = "connection-SECRET"
        });
        using var connection = new HttpClient();
        var configuration = new McpServer { Url = "https://tools.test/mcp", Headers = new() { ["x-connection-key"] = "" } };
        typeof(AgentChatClient).GetMethod("ApplyForwardedHeaders", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(client, [connection, configuration]);
        Assert.False(connection.DefaultRequestHeaders.Contains("X-OpenAI-Key"));
        Assert.Equal("connection-SECRET", Assert.Single(connection.DefaultRequestHeaders.GetValues("X-Connection-Key")));
        InvokePrivateStatic("ApplyInferenceAuthorizationForSameEndpoint", inference, connection, "https://backend.test/mcp");
        Assert.Equal("azure-downstream-token", connection.DefaultRequestHeaders.Authorization?.Parameter);

        connection.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "configured-mcp-token");
        InvokePrivateStatic("ApplyInferenceAuthorizationForSameEndpoint", inference, connection, "https://backend.test/mcp");
        Assert.Equal("configured-mcp-token", connection.DefaultRequestHeaders.Authorization?.Parameter);
        using var remote = new HttpClient();
        InvokePrivateStatic("ApplyInferenceAuthorizationForSameEndpoint", inference, remote, "https://tools.test/mcp");
        Assert.Null(remote.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public void Inference_authorization_is_not_forwarded_to_same_host_and_port_with_a_different_scheme()
    {
        using var inference = new HttpClient { BaseAddress = new Uri("https://backend.test/") };
        inference.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "azure-downstream-token");
        using var mcp = new HttpClient();

        InvokePrivateStatic(
            "ApplyInferenceAuthorizationForSameEndpoint",
            inference,
            mcp,
            "http://backend.test:443/mcp");

        Assert.Null(mcp.DefaultRequestHeaders.Authorization);
    }
}
