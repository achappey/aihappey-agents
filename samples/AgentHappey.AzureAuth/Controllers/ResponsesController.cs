using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using AgentHappey.Core;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using Microsoft.Extensions.Options;
using AgentHappey.Core.ChatClient;
using AgentHappey.Core.ChatRuntime;
using AgentHappey.Core.Responses;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Identity.Web;
using System.Runtime.CompilerServices;
using AgentHappey.Core.MCP;

namespace AgentHappey.AzureAuth.Controllers;

[ApiController]
[Route("v1/responses")]
public class ResponsesController(IHttpClientFactory httpClientFactory,
    IOptions<Config> options,
    [FromServices] IChatRuntimeOrchestrator orchestrator,
    IServiceProvider serviceProvider,
    ITokenAcquisition tokenAcquisition) : ControllerBase
{
    private readonly string Endpoint = options.Value.AiConfig.AiEndpoint!;

    private readonly string? AiScopes = options.Value.AiConfig.AiScopes;

    [HttpPost]
    [Authorize]
    public async Task<IActionResult> Post([FromBody] ResponseRequest requestDto, CancellationToken cancellationToken)
    {
        if (requestDto == null || requestDto.Input == null)
            return BadRequest(new { error = "'input' is a required field" });

        var runtimeRequest = requestDto.ToChatRuntimeRequest();
        if (runtimeRequest.Messages.Count == 0)
            return BadRequest(new { error = "The request did not contain any supported conversation items." });

        if (string.IsNullOrWhiteSpace(runtimeRequest.Model)
            && runtimeRequest.Models is not { Count: > 0 }
            && runtimeRequest.Agents is not { Count: > 0 })
            return BadRequest(new { error = "Provide 'model', 'models', or metadata.agents." });

        var client = httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(Endpoint);

        string downstreamToken = await tokenAcquisition.GetAccessTokenForUserAsync(
                 scopes: [AiScopes!],
                 user: HttpContext.User);

        client.DefaultRequestHeaders.Authorization = new("Bearer", downstreamToken);

        var context = await orchestrator.PrepareAsync(
            Response,
            runtimeRequest,
            agent => new AgentChatClient(
                client,
                httpClientFactory,
                agent,
                new Dictionary<string, string?>(),
                serviceProvider.GetMcpTokenAsync,
                options.Value.AzureAd.TenantId),
            (agentClient, messages) => agentClient.SetHistory(messages),
            cancellationToken);

        var streamingMapper = serviceProvider.GetRequiredService<IStreamingContentMapper>();
        var serializer = new ResponsesRuntimeStreamSerializer(requestDto, runtimeRequest.Model ?? requestDto.Model);

        if (requestDto.Stream == true)
        {
            Response.ContentType = "text/event-stream";
            await using var writer = new StreamWriter(Response.Body);

            foreach (var startEvent in serializer.StartEvents())
                await WriteEventAsync(writer, startEvent, cancellationToken);

            await foreach (var uiPart in CreateUiPartsAsync(runtimeRequest, context, streamingMapper, cancellationToken))
            {
                foreach (var streamPart in serializer.Process(uiPart))
                    await WriteEventAsync(writer, streamPart, cancellationToken);
            }

            foreach (var finalEvent in serializer.Complete())
                await WriteEventAsync(writer, finalEvent, cancellationToken);

            await writer.WriteAsync("data: [DONE]\n\n");
            await writer.FlushAsync(cancellationToken);
            return new EmptyResult();
        }

        await foreach (var uiPart in CreateUiPartsAsync(runtimeRequest, context, streamingMapper, cancellationToken))
            foreach (var _ in serializer.Process(uiPart))
            {
            }

        foreach (var _ in serializer.Complete())
        {
        }

        return Ok(serializer.BuildResult());
    }

    private async IAsyncEnumerable<AIHappey.Vercel.Models.UIMessagePart> CreateUiPartsAsync(
        ChatRuntimeRequest runtimeRequest,
        ChatRuntimeContext context,
        IStreamingContentMapper streamingMapper,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (context.Agents.Count > 1)
        {
            var workflow = orchestrator.BuildWorkflow(runtimeRequest, context.Agents);
            await using var run = await InProcessExecution.RunStreamingAsync(
                workflow,
                context.Messages,
                cancellationToken: cancellationToken);

            await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

            await foreach (var part in streamingMapper.MapAsync(run.WatchStreamAsync(cancellationToken), cancellationToken).WithCancellation(cancellationToken))
                yield return part;

            yield break;
        }

        var updates = context.PrimaryAgent.RunStreamingAsync(
            context.Messages,
            options: context.SingleAgentRunOptions,
            cancellationToken: cancellationToken);

        await foreach (var part in streamingMapper.MapAsync(updates, cancellationToken).WithCancellation(cancellationToken))
            yield return part;
    }

    private static async Task WriteEventAsync(StreamWriter writer, ResponseStreamPart streamPart, CancellationToken cancellationToken)
    {
        await writer.WriteAsync($"data: {JsonSerializer.Serialize(streamPart, ResponseJson.Default)}\n\n");
        await writer.FlushAsync(cancellationToken);
    }
}

