using Microsoft.AspNetCore.Mvc;
using AgentHappey.Common.Models;
using Microsoft.Extensions.Options;
using AgentHappey.Core.ChatClient;
using AgentHappey.Core.ChatRuntime;

namespace AgentHappey.HeaderAuth.Controllers;

[ApiController]
[Route("api/chat")]
public class ChatController(IHttpClientFactory httpClientFactory,
    IOptions<Config> options,
    [FromServices] IChatRuntimeOrchestrator orchestrator) : ControllerBase
{
    private readonly string Endpoint = options.Value.AiConfig.AiEndpoint;

    [HttpPost]
    public async Task<IActionResult> Post([FromBody] AgentRequest chatRequest, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(Endpoint);

        var context = await orchestrator.PrepareAsync(
            Response,
            chatRequest,
            agent => new AgentChatClient(
                client,
                httpClientFactory,
                agent,
                HttpContext.Request.Headers.Where(a => a.Key.StartsWith("X-", StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(a => a.Key, a => a.Value.FirstOrDefault()),
                null),
            cancellationToken: cancellationToken);

        await orchestrator.ExecuteAsync(Response, chatRequest, context, cancellationToken);

        return new EmptyResult();
    }
}

