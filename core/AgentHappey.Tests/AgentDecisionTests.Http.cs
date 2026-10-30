using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using AgentHappey.Common.Models;
using AgentHappey.Core;
using AgentHappey.Core.ChatRuntime;
using AgentHappey.Core.DecisionRuntime;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;
using HeaderDecisions = AgentHappey.HeaderAuth.Controllers.DecisionsController;
using AzureDecisions = AgentHappey.AzureAuth.Controllers.DecisionsController;

namespace AgentHappey.Tests;

public sealed partial class AgentDecisionTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task Both_routes_use_one_structured_execution_in_both_hosts_with_correct_transport(bool azure, bool native, bool tools)
    {
        var f = new Fixture { UseSkill = tools };
        await using var host = await HttpHost.Start(f, azure);
        if (azure)
        {
            using var denied = await host.Client.PostAsJsonAsync(native ? "v1/decisions" : "api/decisions", Body(native));
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            Assert.Empty(f.Sent);
            host.Client.DefaultRequestHeaders.Authorization = new("Bearer", "incoming-user");
        }
        host.Client.DefaultRequestHeaders.Add("X-OpenAI-Key", "caller-openai-secret");
        host.Client.DefaultRequestHeaders.Add("X-Anthropic-Key", "unrelated-secret");
        host.Client.DefaultRequestHeaders.Add("X-Agent-Name", "spoofed-agent");
        using var response = await host.Client.PostAsJsonAsync(native ? "v1/decisions" : "api/decisions", Body(native));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var answer = native ? result.GetProperty("answers")[0] : result.GetProperty("answers").GetProperty("eligible");
        Assert.Equal(native ? "predicate" : "boolean", answer.GetProperty("type").GetString());
        Assert.Equal(0.8, answer.GetProperty("probability").GetDouble());
        Assert.Equal(tools ? 2 : 1, f.Sent.Count);
        Assert.All(f.Sent, call =>
        {
            Assert.True(IsDecisionRequest(call.Body));
            Assert.Equal(2, UserTexts(call.Body).Length);
            Assert.Equal("AuditAgent", call.Headers["X-Agent-Name"]);
            Assert.Equal("configured-secret", call.Headers["X-Provider-Key"]);
            Assert.False(call.Headers.ContainsKey("X-Anthropic-Key"));
            if (azure)
            {
                Assert.Equal("Bearer downstream-user-token", call.Headers["Authorization"]);
                Assert.False(call.Headers.ContainsKey("X-OpenAI-Key"));
            }
            else Assert.Equal("caller-openai-secret", call.Headers["X-OpenAI-Key"]);
        });
        Assert.DoesNotContain("secret", result.GetRawText());
        if (azure) Assert.Equal(1, host.Tokens.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Http_errors_reject_invalid_questions_and_do_not_expose_invalid_model_output(bool native)
    {
        var f = new Fixture { FinalPayload = "provider-secret-not-json" };
        await using var host = await HttpHost.Start(f, azure: false);
        var path = native ? "v1/decisions" : "api/decisions";
        using var invalid = await host.Client.PostAsJsonAsync(path,
            native ? new { model = "AuditAgent", input = "input", questions = Array.Empty<object>() } : (object)new { model = "AuditAgent", state = "input", questions = new { } });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Empty(f.Sent);
        using var badOutput = await host.Client.PostAsJsonAsync(path, Body(native));
        Assert.Equal(HttpStatusCode.BadGateway, badOutput.StatusCode);
        var text = await badOutput.Content.ReadAsStringAsync();
        Assert.Contains("server_error", text);
        Assert.Contains("invalid_decision_response", text);
        Assert.DoesNotContain("provider-secret", text);
        Assert.Single(f.Sent);
    }

    [Theory]
    [InlineData(false, false, 400)]
    [InlineData(false, true, 422)]
    [InlineData(true, false, 501)]
    [InlineData(true, true, 400)]
    public async Task Unsupported_provider_requests_fail_explicitly_without_fallback_or_secret_leak(bool azure, bool native, int status)
    {
        var f = new Fixture { ProviderErrorStatus = (HttpStatusCode)status };
        await using var host = await HttpHost.Start(f, azure);
        if (azure) host.Client.DefaultRequestHeaders.Authorization = new("Bearer", "incoming-user");
        using var response = await host.Client.PostAsJsonAsync(native ? "v1/decisions" : "api/decisions", Body(native));
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("structured_decision_request_rejected", result.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(status, result.GetProperty("error").GetProperty("upstream_status").GetInt32());
        Assert.DoesNotContain("provider-secret", result.GetRawText());
        Assert.True(IsDecisionRequest(Assert.Single(f.Sent).Body));
    }

    [Fact]
    public async Task Concurrent_http_requests_do_not_share_credentials_or_schemas()
    {
        var f = new Fixture();
        await using var host = await HttpHost.Start(f, azure: false);
        var calls = Enumerable.Range(0, 6).Select(async index =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, index % 2 == 0 ? "v1/decisions" : "api/decisions")
            {
                Content = JsonContent.Create(Body(index % 2 == 0))
            };
            request.Headers.Add("X-OpenAI-Key", "request-key-" + index);
            request.Headers.Add("X-Request-Id", "request-" + index);
            using var response = await host.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        });
        await Task.WhenAll(calls);
        Assert.Equal(6, f.Sent.Count);
        foreach (var group in f.Sent.GroupBy(call => call.Headers["X-Request-Id"]))
        {
            Assert.Single(group);
            Assert.All(group, call => Assert.Equal(group.Key.Replace("request-", "request-key-"), call.Headers["X-OpenAI-Key"]));
            Assert.All(group, call => Assert.True(IsDecisionRequest(call.Body)));
        }
    }

    private static object Body(bool native) => native ? NativeRequest() : new DecisionRequest
    {
        Model = "AuditAgent", State = "request-evidence",
        Questions = new() { ["eligible"] = new DecisionBooleanQuestion { Instructions = "Does this qualify?" } }
    };

    private sealed class HttpHost(WebApplication app, HttpClient client, TokenProxy tokens) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;
        public TokenProxy Tokens { get; } = tokens;

        public static async Task<HttpHost> Start(Fixture fixture, bool azure)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddLogging();
            builder.Services.AddSingleton<IModelCatalog>(fixture);
            builder.Services.AddSingleton<IHttpClientFactory>(fixture);
            builder.Services.AddSingleton<IChatRuntimeOrchestrator>(new ChatRuntimeOrchestrator(new StreamingContentMapper(), fixture));
            builder.Services.AddSingleton<AgentDecisionService>();
            builder.Services.Configure<AgentHappey.HeaderAuth.Config>(options => options.AiConfig = new() { AiEndpoint = "https://gateway.test/" });
            builder.Services.Configure<AgentHappey.AzureAuth.Config>(options => options.AiConfig = new() { AiEndpoint = "https://gateway.test/", AiScopes = "gateway-scope" });
            var tokenService = DispatchProxy.Create<ITokenAcquisition, TokenProxy>();
            var tokens = (TokenProxy)(object)tokenService;
            builder.Services.AddSingleton(tokenService);
            builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestAuthentication>("test", _ => { });
            builder.Services.AddAuthorization();
            var controllerType = azure ? typeof(AzureDecisions) : typeof(HeaderDecisions);
            var mvc = builder.Services.AddControllers().AddApplicationPart(controllerType.Assembly);
            mvc.PartManager.FeatureProviders.Add(new OnlyController(controllerType));
            var app = builder.Build();
            app.UseAuthentication(); app.UseAuthorization(); app.MapControllers();
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new(app, new HttpClient { BaseAddress = new(address) }, tokens);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync(); await app.DisposeAsync();
        }
    }

    public class TokenProxy : DispatchProxy
    {
        public int Calls;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Assert.Equal("GetAccessTokenForUserAsync", targetMethod!.Name);
            Assert.Contains("gateway-scope", Assert.IsAssignableFrom<IEnumerable<string>>(args![0]));
            Assert.Contains(args, argument => argument is ClaimsPrincipal principal && principal.Identity!.IsAuthenticated);
            Interlocked.Increment(ref Calls);
            return Task.FromResult("downstream-user-token");
        }
    }

    private sealed class OnlyController(Type selected) : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            foreach (var controller in feature.Controllers.Where(c => c.AsType() != selected).ToArray()) feature.Controllers.Remove(controller);
        }
    }

    private sealed class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            => Task.FromResult(Request.Headers.Authorization == "Bearer incoming-user"
                ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", "test-user")], "test")), "test"))
                : AuthenticateResult.NoResult());
    }
}
