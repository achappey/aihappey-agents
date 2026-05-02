using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using AIHappey.Responses;
using Microsoft.Extensions.Options;
using AgentHappey.Core.ChatClient;
using AgentHappey.Core.ChatRuntime;
using AgentHappey.Core.MCP;
using AgentHappey.Core.Responses;
using AIHappey.Responses.Streaming;

namespace AgentHappey.HeaderAuth.Controllers;

[ApiController]
[Route("v1/responses")]
public class ResponsesController(IHttpClientFactory httpClientFactory,
    IOptions<Config> options,
    [FromServices] IChatRuntimeOrchestrator orchestrator,
    [FromServices] IResponsesNativeMapper responsesMapper,
    IServiceProvider serviceProvider) : ControllerBase
{
    private readonly string Endpoint = options.Value.AiConfig.AiEndpoint!;

    [HttpPost]
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

        if (requestDto.Stream == true)
        {
            Response.ContentType = "text/event-stream";

            try
            {
                var (context, responseModel) = await PrepareRuntimeAsync(runtimeRequest, requestDto.Model, cancellationToken);
                await using var writer = new StreamWriter(Response.Body);

                var stream = context.Agents.Count > 1
                    ? responsesMapper.MapStreamingAsync(
                        requestDto,
                        responseModel,
                        orchestrator.StreamWorkflowAsync(runtimeRequest, context, emitTurnToken: true, cancellationToken),
                        cancellationToken)
                    : responsesMapper.MapStreamingAsync(
                        requestDto,
                        responseModel,
                        orchestrator.StreamAgentAsync(context, cancellationToken),
                        cancellationToken);

                await foreach (var part in stream.WithCancellation(cancellationToken))
                    await WriteEventAsync(writer, part, cancellationToken);

                await writer.WriteAsync("data: [DONE]\n\n");
                await writer.FlushAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return new EmptyResult();
            }
            catch (Exception e)
            {
                await TryWriteResponsesStreamErrorAsync(e.Message);
            }

            return new EmptyResult();
        }

        try
        {
            var (context, responseModel) = await PrepareRuntimeAsync(runtimeRequest, requestDto.Model, cancellationToken);

            var result = context.Agents.Count > 1
                ? responsesMapper.Map(
                    requestDto,
                    responseModel,
                    await orchestrator.RunWorkflowAsync(runtimeRequest, context, emitTurnToken: true, cancellationToken))
                : responsesMapper.Map(
                    requestDto,
                    responseModel,
                    await orchestrator.RunAgentAsync(context, cancellationToken));

            return Ok(result);
        }
        catch (OperationCanceledException)
        {
            return new EmptyResult();
        }
        catch (Exception e)
        {
            return StatusCode(500, new
            {
                error = new
                {
                    message = e.Message,
                    type = "server_error"
                }
            });
        }
    }

    private static async Task WriteEventAsync(StreamWriter writer, ResponseStreamPart streamPart, CancellationToken cancellationToken)
    {
        await writer.WriteAsync($"data: {JsonSerializer.Serialize(streamPart, ResponseJson.Default)}\n\n");
        await writer.FlushAsync(cancellationToken);
    }

    private async Task<(ChatRuntimeContext Context, string? ResponseModel)> PrepareRuntimeAsync(
        ChatRuntimeRequest runtimeRequest,
        string? requestModel,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(Endpoint);

        var context = await orchestrator.PrepareAsync(
            Response,
            runtimeRequest,
            agent => new AgentChatClient(
                client,
                httpClientFactory,
                agent,
                HttpContext.Request.Headers.Where(a => a.Key.StartsWith("X-", StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(a => a.Key, a => a.Value.FirstOrDefault()),
                serviceProvider.GetMcpTokenAsync),
            (agentClient, messages) => agentClient.SetHistory(messages),
            cancellationToken);

        return (context, runtimeRequest.Model ?? requestModel);
    }

    private async Task TryWriteResponsesStreamErrorAsync(string? message)
    {
        try
        {
            var error = new ResponseError
            {
                Message = string.IsNullOrWhiteSpace(message) ? "Something went wrong" : message,
                Code = "server_error",
                Param = null!
            };

            await Response.WriteAsync($"data: {JsonSerializer.Serialize(error, ResponseJson.Default)}\n\n", HttpContext.RequestAborted);
            await Response.Body.FlushAsync(HttpContext.RequestAborted);
        }
        catch
        {
            // The client may already have disconnected after the stream failed.
        }
    }
}

