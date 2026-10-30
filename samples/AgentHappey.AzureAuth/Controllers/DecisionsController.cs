using AgentHappey.Core.ChatClient;
using AgentHappey.Core.DecisionRuntime;
using AgentHappey.Core.MCP;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;

namespace AgentHappey.AzureAuth.Controllers;

[ApiController]
[Authorize]
public sealed class DecisionsController(AgentDecisionService decisions, IHttpClientFactory httpClientFactory,
    IOptions<Config> options, IServiceProvider serviceProvider, ITokenAcquisition tokenAcquisition,
    ILogger<DecisionsController> logger) : AgentDecisionsControllerBase(decisions, logger)
{
    protected override async Task<DecisionAgentTransport> CreateTransportAsync(CancellationToken cancellationToken)
    {
        var token = await tokenAcquisition.GetAccessTokenForUserAsync(
            scopes: [options.Value.AiConfig.AiScopes!], user: HttpContext.User);
        cancellationToken.ThrowIfCancellationRequested();
        var client = httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(options.Value.AiConfig.AiEndpoint!);
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return new(client, agent => new AgentChatClient(client, httpClientFactory, agent,
            new Dictionary<string, string?>(), serviceProvider.GetMcpTokenAsync, options.Value.AgentPluginExtensionNamespace));
    }
}
