using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using AgentHappey.Core.ChatClient;
using AgentHappey.Common.Models;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;
using AgentHappey.Core.Extensions;
using Microsoft.Extensions.AI;
using AgentHappey.Core.ChatRuntime;
using AgentHappey.Core.MCP;
using System.Text;
using AIHappey.Vercel.Models;
using AgentHappey.Core;

namespace AgentHappey.AzureAuth.Controllers;

[ApiController]
[Route("api/chat")]
public class ChatController(IHttpClientFactory httpClientFactory,
    IOptions<Config> options,
    [FromServices] IChatRuntimeOrchestrator orchestrator,
    IServiceProvider serviceProvider,
    ITokenAcquisition tokenAcquisition) : ControllerBase
{
    private readonly string Endpoint = options.Value.AiConfig.AiEndpoint!;

    private readonly string? AiScopes = options.Value.AiConfig.AiScopes;

    [HttpPost]
    [Authorize]
    public async Task<IActionResult> Post([FromBody] AgentRequest chatRequest, CancellationToken cancellationToken)
    {
        try
        {
            var client = httpClientFactory.CreateClient();
            client.BaseAddress = new Uri(Endpoint);

            string downstreamToken = await tokenAcquisition.GetAccessTokenForUserAsync(
                     scopes: [AiScopes!],
                     user: HttpContext.User);

            client.DefaultRequestHeaders.Authorization = new("Bearer", downstreamToken);

            var context = await orchestrator.PrepareAsync(
                Response,
                chatRequest,
                agent => new AgentChatClient(
                    client,
                    httpClientFactory,
                    agent,
                    new Dictionary<string, string?>(),
                    serviceProvider.GetMcpTokenAsync),
                (agentClient, messages) => agentClient.SetHistory(messages),
                cancellationToken);

            var yamlFile = chatRequest.Messages
                    .SelectMany(m => m.Parts)
                    .OfType<FileUIPart>()
                    .LastOrDefault(f => f.MediaType == "application/yaml");

            string? yamlContent = null;

            if (yamlFile != null)
            {
                var url = yamlFile.Url;
                var base64 = url[(url.IndexOf("base64,") + "base64,".Length)..];
                yamlContent = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            }

            if (context.Agents.Count > 1 && !string.IsNullOrEmpty(yamlContent))
            {
                var workflow = yamlContent.ParseWorkflow<string>(context.CreateWorkflowAgentProvider());
                var latestUserInput = context.Messages.LastOrDefault(message => message.Role == ChatRole.User)?.Text
                    ?? throw new InvalidOperationException("No user message found for YAML workflow input.");

                await orchestrator.ExecuteWorkflowAsync(
                    Response,
                    workflow,
                    latestUserInput,
                    emitTurnToken: false,
                    cancellationToken);
            }
            else
            {
                await orchestrator.ExecuteAsync(Response, chatRequest, context, cancellationToken);
            }
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

