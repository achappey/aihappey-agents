using System.Net;
using System.Text.Json;
using AgentHappey.Common.Models;
using AgentHappey.Core.ChatClient;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace AgentHappey.Core.DecisionRuntime;

/// <summary>Shared routes and error handling; each host owns its transport and authentication.</summary>
public abstract class AgentDecisionsControllerBase(AgentDecisionService decisions,
    ILogger logger) : ControllerBase
{
    [HttpPost("/v1/decisions")]
    public Task<IActionResult> OpenAI([FromBody] OpenAIDecisionRequest request, CancellationToken cancellationToken)
        => ExecuteAsync(async factory => await decisions.ExecuteAsync(request, Response, factory, cancellationToken), cancellationToken);

    [HttpPost("/api/decisions")]
    public Task<IActionResult> Vercel([FromBody] DecisionRequest request, CancellationToken cancellationToken)
        => ExecuteAsync(async factory => await decisions.ExecuteAsync(request, Response, factory, cancellationToken), cancellationToken);

    protected abstract Task<DecisionAgentTransport> CreateTransportAsync(CancellationToken cancellationToken);

    private async Task<IActionResult> ExecuteAsync(Func<Func<Agent, AgentChatClient>, Task<object>> execute,
        CancellationToken cancellationToken)
    {
        try
        {
            using var transport = await CreateTransportAsync(cancellationToken);
            return Ok(await execute(transport.CreateClient));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return new EmptyResult(); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        {
            return BadRequest(new { error = new { message = error.Message, type = "invalid_request_error" } });
        }
        catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.BadRequest
            or HttpStatusCode.UnprocessableEntity or HttpStatusCode.NotImplemented)
        {
            logger.LogError(error, "Provider rejected structured decision request {TraceIdentifier}", HttpContext.TraceIdentifier);
            return StatusCode(502, new { error = new
            {
                message = "The configured model/provider rejected the structured decision request. Structured output support is required.",
                type = "provider_error", code = "structured_decision_request_rejected",
                upstream_status = (int)error.StatusCode.Value
            } });
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            logger.LogError(error, "Invalid structured decision response for request {TraceIdentifier}", HttpContext.TraceIdentifier);
            return StatusCode(502, new { error = new
            {
                message = "The agent did not return a complete valid structured decision.",
                type = "server_error", code = "invalid_decision_response"
            } });
        }
        catch (Exception error)
        {
            logger.LogError(error, "Agent decision execution failed for request {TraceIdentifier}", HttpContext.TraceIdentifier);
            return StatusCode(502, new { error = new { message = "The agent could not produce a complete valid decision.", type = "server_error" } });
        }
    }
}

public sealed class DecisionAgentTransport(HttpClient httpClient, Func<Agent, AgentChatClient> createClient) : IDisposable
{
    public Func<Agent, AgentChatClient> CreateClient { get; } = createClient;
    public void Dispose() => httpClient.Dispose();
}
