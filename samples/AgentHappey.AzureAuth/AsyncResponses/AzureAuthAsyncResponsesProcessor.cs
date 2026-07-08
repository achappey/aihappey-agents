using System.Net.Http.Headers;
using AgentHappey.AsyncResponses;
using AgentHappey.Core.ChatClient;
using AgentHappey.Core.ChatRuntime;
using AgentHappey.Core.MCP;
using AgentHappey.Core.Responses;
using AIHappey.Responses;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;

namespace AgentHappey.AzureAuth.AsyncResponses;

public sealed class AzureAuthAsyncResponsesProcessor(
    IOptions<Config> options,
    IHttpClientFactory httpClientFactory,
    IChatRuntimeOrchestrator orchestrator,
    IResponsesNativeMapper responsesMapper) : IAsyncResponsesProcessor
{
    private readonly Config config = options.Value;

    public async Task<ResponseResult> ProcessAsync(
        AsyncResponsesQueueMessage message,
        CancellationToken cancellationToken = default)
    {
        var request = message.Request;
        request.Stream = false;
        request.Background = false;

        var runtimeRequest = request.ToChatRuntimeRequest();
        var client = httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(message.Context.AiEndpoint ?? config.AiConfig.AiEndpoint);

        var userAccessToken = message.Context.UserAccessToken
            ?? throw new InvalidOperationException("No access token found in background response message.");
        var downstreamToken = await AcquireOboTokenAsync(
            userAccessToken,
            message.Context.AiScopes ?? config.AiConfig.AiScopes,
            cancellationToken);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", downstreamToken);

        var response = new DefaultHttpContext().Response;
        var context = await orchestrator.PrepareAsync(
            response,
            runtimeRequest,
            agent => new AgentChatClient(
                client,
                httpClientFactory,
                agent,
                new Dictionary<string, string?>(),
                (serverUrl, ct) => httpClientFactory.GetMcpTokenAsync(
                    serverUrl,
                    userAccessToken,
                    config.AzureAd,
                    config.McpConfig,
                    ct)),
            (agentClient, messages) => agentClient.SetHistory(messages),
            cancellationToken);

        var responseModel = ResolveResponseModel(runtimeRequest, request.Model, context);
        var providerKey = ResolveProviderKey(runtimeRequest, request.Model, context);
        return context.Agents.Count > 1
            ? responsesMapper.Map(
                request,
                responseModel,
                providerKey,
                await orchestrator.RunWorkflowAsync(runtimeRequest, context, emitTurnToken: true, cancellationToken))
            : responsesMapper.Map(
                request,
                responseModel,
                providerKey,
                await orchestrator.RunAgentAsync(context, cancellationToken));
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

    private async Task<string> AcquireOboTokenAsync(
        string userAccessToken,
        string? scopes,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(scopes))
            throw new InvalidOperationException("AiConfig:AiScopes is required for background response OBO.");

        var app = ConfidentialClientApplicationBuilder
            .Create(config.AzureAd.ClientId)
            .WithClientSecret(config.AzureAd.ClientSecret)
            .WithAuthority($"https://login.microsoftonline.com/{config.AzureAd.TenantId}")
            .Build();

        var result = await app.AcquireTokenOnBehalfOf([scopes], new UserAssertion(userAccessToken))
            .ExecuteAsync(cancellationToken);

        return result.AccessToken;
    }
}
