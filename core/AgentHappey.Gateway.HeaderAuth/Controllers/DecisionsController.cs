using AgentHappey.Core.ChatClient;
using AgentHappey.Core.DecisionRuntime;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentHappey.HeaderAuth.Controllers;

[ApiController]
public sealed class DecisionsController(AgentDecisionService decisions, IHttpClientFactory httpClientFactory,
    IOptions<Config> options, ILogger<DecisionsController> logger)
    : AgentDecisionsControllerBase(decisions, logger)
{
    protected override Task<DecisionAgentTransport> CreateTransportAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var client = httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(options.Value.AiConfig.AiEndpoint!);
        var headers = HttpContext.Request.Headers
            .Where(header => header.Key.StartsWith("X-", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(header => header.Key, header => header.Value.FirstOrDefault(), StringComparer.OrdinalIgnoreCase);
        return Task.FromResult(new DecisionAgentTransport(client, agent => new AgentChatClient(client,
            httpClientFactory, agent, headers, null, options.Value.AgentPluginExtensionNamespace)));
    }
}
