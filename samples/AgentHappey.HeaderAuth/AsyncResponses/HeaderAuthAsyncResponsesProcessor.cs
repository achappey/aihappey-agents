using AgentHappey.AsyncResponses;
using AgentHappey.Core.ChatClient;
using AgentHappey.Core.ChatRuntime;
using AgentHappey.Core.MCP;
using AgentHappey.Core.Responses;
using AIHappey.Responses;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace AgentHappey.HeaderAuth.AsyncResponses;

public sealed class HeaderAuthAsyncResponsesProcessor(
    IOptions<Config> options,
    IHttpClientFactory httpClientFactory,
    IChatRuntimeOrchestrator orchestrator,
    IResponsesNativeMapper responsesMapper,
    IServiceProvider serviceProvider) : IAsyncResponsesProcessor
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

        var response = new DefaultHttpContext().Response;
        var context = await orchestrator.PrepareAsync(
            response,
            runtimeRequest,
            agent => new AgentChatClient(
                client,
                httpClientFactory,
                agent,
                message.Context.Headers,
                serviceProvider.GetMcpTokenAsync),
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
}
