using Microsoft.AspNetCore.Mvc;
using AgentHappey.Common.Models;
using Microsoft.Extensions.Options;
using AgentHappey.Core.ChatClient;
using AgentHappey.Core.ChatRuntime;
using AgentHappey.Core;

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
        try
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
        }
        catch (OperationCanceledException e)
        {
            await TryWriteAbortAsync(e.Message);
        }
        catch (Exception e)
        {
            await TryWriteErrorAsync(e.Message);
        }

        return new EmptyResult();
    }

    private async Task TryWriteAbortAsync(string? reason)
    {
        try
        {
            await Response.WriteAbortPartAsync(reason, HttpContext.RequestAborted);
        }
        catch
        {
            // The client may already have disconnected after the stream failed.
        }
    }

    private async Task TryWriteErrorAsync(string? error)
    {
        try
        {
            await Response.WriteErrorPartAsync(error, HttpContext.RequestAborted);
        }
        catch
        {
            // The client may already have disconnected after the stream failed.
        }
    }
}

