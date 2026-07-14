using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using AgentHappey.Core.ChatClient;
using AgentHappey.Core.ChatRuntime;
using AgentHappey.Core.Responses;
using Microsoft.Identity.Web;
using AgentHappey.Core.MCP;
using AgentHappey.AsyncResponses;

namespace AgentHappey.AzureAuth.Controllers;

[ApiController]
[Route("v1/responses")]
public class ResponsesController(IHttpClientFactory httpClientFactory,
    IOptions<Config> options,
    [FromServices] IChatRuntimeOrchestrator orchestrator,
    [FromServices] IResponsesNativeMapper responsesMapper,
    [FromServices] IAsyncResponsesService asyncResponses,
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

        if (requestDto.Background == true)
        {
            if (requestDto.Stream == true)
                return BadRequest(new { error = "background responses are only supported for non-streaming requests" });

            if (!asyncResponses.IsEnabled)
                return StatusCode(503, new { error = "background responses are not configured" });

            var accessToken = GetBearerToken();
            if (string.IsNullOrWhiteSpace(accessToken))
                return Unauthorized(new { error = "A bearer access token is required for background responses" });

            var userId = GetCurrentUserId();
            if (string.IsNullOrWhiteSpace(userId))
                return Unauthorized(new { error = "A stable user object id claim is required for background responses" });

            try
            {
                var queued = await asyncResponses.EnqueueAsync(
                    requestDto,
                    new AsyncResponsesRequestContext
                    {
                        UserId = userId,
                        UserAccessToken = accessToken,
                        CorrelationId = HttpContext.TraceIdentifier,
                        AiEndpoint = Endpoint,
                        AiScopes = AiScopes
                    },
                    cancellationToken);

                return Ok(queued);
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

        if (requestDto.Stream == true)
        {
            Response.ContentType = "text/event-stream";

            try
            {
                var (context, responseModel, providerKey) = await PrepareRuntimeAsync(runtimeRequest, requestDto.Model, cancellationToken);
                await using var writer = new StreamWriter(Response.Body);

                var stream = context.Agents.Count > 1
                    ? responsesMapper.MapStreamingAsync(
                        requestDto,
                        responseModel,
                        providerKey,
                        orchestrator.StreamWorkflowAsync(runtimeRequest, context, emitTurnToken: true, cancellationToken),
                        cancellationToken)
                    : responsesMapper.MapStreamingAsync(
                        requestDto,
                        responseModel,
                        providerKey,
                        orchestrator.StreamAgentAsync(context, cancellationToken),
                        cancellationToken);

                await foreach (var streamPart in stream.WithCancellation(cancellationToken))
                    await WriteEventAsync(writer, streamPart, cancellationToken);

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
            var (context, responseModel, providerKey) = await PrepareRuntimeAsync(runtimeRequest, requestDto.Model, cancellationToken);

            var result = context.Agents.Count > 1
                ? responsesMapper.Map(
                    requestDto,
                    responseModel,
                    providerKey,
                    await orchestrator.RunWorkflowAsync(runtimeRequest, context, emitTurnToken: true, cancellationToken))
                : responsesMapper.Map(
                    requestDto,
                    responseModel,
                    providerKey,
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

    [HttpGet]
    [Authorize]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized(new { error = "A stable user object id claim is required for background responses" });

        var responses = await asyncResponses.ListAsync(cancellationToken, userId);
        return Ok(new { @object = "list", data = responses });
    }

    [HttpGet("{responseId}")]
    [Authorize]
    public async Task<IActionResult> Get(string responseId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(responseId))
            return BadRequest(new { error = "response_id is required" });

        var userId = GetCurrentUserId();
        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized(new { error = "A stable user object id claim is required for background responses" });

        var response = await asyncResponses.GetAsync(responseId, cancellationToken, userId);

        if (response is null)
            return NotFound(new { error = new { message = "Response not found", type = "not_found" } });

        return Ok(response);
    }

    [HttpDelete("{responseId}")]
    [Authorize]
    public async Task<IActionResult> Delete(string responseId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(responseId))
            return BadRequest(new { error = "response_id is required" });

        var userId = GetCurrentUserId();
        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized(new { error = "A stable user object id claim is required for background responses" });

        return await asyncResponses.DeleteAsync(responseId, cancellationToken, userId)
            ? Ok(new { id = responseId, @object = "response", deleted = true })
            : NotFound(new { error = new { message = "Response not found", type = "not_found" } });
    }

    private static async Task WriteEventAsync(StreamWriter writer, ResponseStreamPart streamPart, CancellationToken cancellationToken)
    {
        await writer.WriteAsync($"data: {JsonSerializer.Serialize(streamPart, ResponseJson.Default)}\n\n");
        await writer.FlushAsync(cancellationToken);
    }

    private async Task<(ChatRuntimeContext Context, string? ResponseModel, string? ProviderKey)> PrepareRuntimeAsync(
        ChatRuntimeRequest runtimeRequest,
        string? requestModel,
        CancellationToken cancellationToken)
    {
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
                serviceProvider.GetMcpTokenAsync),
            (agentClient, messages) => agentClient.SetHistory(messages),
            cancellationToken);

        return (context, ResolveResponseModel(runtimeRequest, requestModel, context), ResolveProviderKey(runtimeRequest, requestModel, context));
    }

    private static string? ResolveResponseModel(
        ChatRuntimeRequest runtimeRequest,
        string? requestModel,
        ChatRuntimeContext context)
        => context.Agents.Count == 1
            ? context.PrimaryAgent.Name
            : runtimeRequest.Model ?? requestModel;

    private static string? ResolveProviderKey(
        ChatRuntimeRequest runtimeRequest,
        string? requestModel,
        ChatRuntimeContext context)
    {
        var modelId = context.ResolvedAgents.FirstOrDefault()?.Model?.Id
            ?? runtimeRequest.Model
            ?? requestModel;

        return string.IsNullOrWhiteSpace(modelId)
            ? null
            : modelId.Split('/')[0];
    }

    private string? GetBearerToken()
    {
        var authorization = HttpContext.Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(authorization))
            return null;

        const string bearer = "Bearer ";
        return authorization.StartsWith(bearer, StringComparison.OrdinalIgnoreCase)
            ? authorization[bearer.Length..].Trim()
            : null;
    }

    private string? GetCurrentUserId()
        => HttpContext.User.FindFirstValue("oid")
            ?? HttpContext.User.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier");

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

