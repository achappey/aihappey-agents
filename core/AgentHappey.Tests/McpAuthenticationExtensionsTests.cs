using System.Reflection;
using System.Security.Claims;
using AgentHappey.Core;
using AgentHappey.Core.MCP;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AgentHappey.Tests;

public sealed class McpAuthenticationExtensionsTests
{
    [Theory]
    [InlineData("https://runtime.test/mcp", "https://runtime.test/", true)]
    [InlineData("https://runtime.test:443/mcp", "https://runtime.test/agents", true)]
    [InlineData("https://RUNTIME.test/mcp", "https://runtime.TEST/", true)]
    [InlineData("http://runtime.test/mcp", "https://runtime.test/", false)]
    [InlineData("https://runtime.test:8443/mcp", "https://runtime.test/", false)]
    [InlineData("https://mcp.runtime.test/mcp", "https://runtime.test/", false)]
    [InlineData("https://runtime.test.evil.example/mcp", "https://runtime.test/", false)]
    public void Same_origin_requires_matching_scheme_host_and_effective_port(
        string serverUrl,
        string runtimeUrl,
        bool expected)
    {
        Assert.Equal(expected, InvokeIsSameOrigin(new Uri(serverUrl), new Uri(runtimeUrl)));
    }

    [Fact]
    public async Task Own_runtime_mcp_server_reuses_current_request_token_without_oauth_discovery()
    {
        var (services, factory) = CreateServices(
            requestScheme: "https",
            requestHost: "runtime.test",
            authorization: "Bearer current-request-token",
            mcpBaseUrl: "https://external-mcp.test/");

        var token = await services.GetMcpTokenAsync("https://runtime.test/mcp/sharepoint");

        Assert.Equal("current-request-token", token);
        Assert.Equal(0, factory.CreateClientCalls);
    }

    [Theory]
    [InlineData("http", "runtime.test", "https://runtime.test/mcp")]
    [InlineData("https", "runtime.test:8443", "https://runtime.test/mcp")]
    [InlineData("https", "other.test", "https://runtime.test/mcp")]
    public async Task Different_request_origin_never_forwards_current_token(
        string requestScheme,
        string requestHost,
        string serverUrl)
    {
        var (services, factory) = CreateServices(
            requestScheme,
            requestHost,
            "Bearer current-request-token",
            "https://external-mcp.test/");

        var token = await services.GetMcpTokenAsync(serverUrl);

        Assert.Null(token);
        Assert.Equal(1, factory.CreateClientCalls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Basic credentials")]
    [InlineData("Bearer")]
    public async Task Own_runtime_server_does_not_invent_or_forward_a_missing_bearer(string? authorization)
    {
        var (services, factory) = CreateServices(
            "https",
            "runtime.test",
            authorization,
            "https://external-mcp.test/");

        var token = await services.GetMcpTokenAsync("https://runtime.test/mcp");

        Assert.Null(token);
        Assert.Equal(0, factory.CreateClientCalls);
    }

    [Fact]
    public async Task Request_origin_uses_host_and_scheme_after_forwarded_headers_middleware()
    {
        var (services, factory) = CreateServices(
            "https",
            "public.runtime.test:8443",
            "Bearer current-request-token",
            "https://external-mcp.test/");

        var token = await services.GetMcpTokenAsync("https://public.runtime.test:8443/mcp");

        Assert.Equal("current-request-token", token);
        Assert.Equal(0, factory.CreateClientCalls);
    }

    [Theory]
    [InlineData("http://localhost:3021/mcp", "http://localhost:3021", true)]
    [InlineData("http://LOCALHOST:3021/mcp/conversations", "http://localhost:3021/other", true)]
    [InlineData("https://localhost:3021/mcp", "http://localhost:3021", false)]
    [InlineData("http://localhost:3022/mcp", "http://localhost:3021", false)]
    [InlineData("http://conversations.localhost:3021/mcp", "http://localhost:3021", false)]
    public void Conversation_mcp_requires_matching_scheme_host_and_effective_port(
        string serverUrl,
        string conversationsBaseUrl,
        bool expected)
    {
        Assert.Equal(expected, InvokeIsConversationMcpServer(
            serverUrl,
            new ConversationsConfig { McpBaseUrl = conversationsBaseUrl }));
    }

    [Fact]
    public void Missing_conversation_configuration_does_not_select_conversation_authentication()
    {
        Assert.False(InvokeIsConversationMcpServer("http://localhost:3021/mcp", null));
    }

    private static (IServiceProvider Services, TrackingHttpClientFactory Factory) CreateServices(
        string requestScheme,
        string requestHost,
        string? authorization,
        string mcpBaseUrl)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user")], "test"))
        };
        context.Request.Scheme = requestScheme;
        context.Request.Host = new HostString(requestHost);

        if (authorization is not null)
            context.Request.Headers.Authorization = authorization;

        var factory = new TrackingHttpClientFactory();
        var collection = new ServiceCollection();
        collection.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = context });
        collection.AddSingleton<IHttpClientFactory>(factory);
        collection.AddSingleton(new McpConfig { McpBaseUrl = mcpBaseUrl });
        collection.AddSingleton(new AzureAd
        {
            ClientId = "client-id",
            ClientSecret = "client-secret",
            TenantId = "tenant-id"
        });

        return (collection.BuildServiceProvider(), factory);
    }

    private static bool InvokeIsSameOrigin(Uri left, Uri right)
    {
        var method = typeof(AuthenticationExtensions).GetMethod(
            "IsSameOrigin",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("IsSameOrigin was not found.");

        return (bool)method.Invoke(null, [left, right])!;
    }

    private static bool InvokeIsConversationMcpServer(string serverUrl, ConversationsConfig? conversationsConfig)
    {
        var method = typeof(AuthenticationExtensions).GetMethod(
            "IsConversationMcpServer",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("IsConversationMcpServer was not found.");

        return (bool)method.Invoke(null, [serverUrl, conversationsConfig])!;
    }

    private sealed class TrackingHttpClientFactory : IHttpClientFactory
    {
        public int CreateClientCalls { get; private set; }

        public HttpClient CreateClient(string name)
        {
            CreateClientCalls++;
            return new HttpClient();
        }
    }
}
