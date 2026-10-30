using Microsoft.Extensions.AI;
using AgentHappey.Common.Models;
using AgentHappey.Core.ChatRuntime;
using AIHappey.Responses;

namespace AgentHappey.Core.ChatClient;

public partial class AgentChatClient(
    HttpClient http,
    IHttpClientFactory httpClientFactory,
    Agent agent,
    IDictionary<string, string?> headers,
    Func<string, CancellationToken, Task<string?>>? getMcpToken = null,
    string? agentPluginExtensionNamespace = null) : IChatClient
{
    private const string AgentNameHeader = "X-Agent-Name";

    public void EnsureHeaders()
    {
        // Retained for source compatibility. Never mutate a shared HttpClient:
        // credentials and agent identity are applied to individual requests.
    }

    private Dictionary<string, string> GetInferenceHeaders(string? modelId)
    {
        var scoped = ProviderCredentialHeaders.ForModel(headers, modelId);
        if (!string.IsNullOrWhiteSpace(agent.Name)) scoped[AgentNameHeader] = agent.Name;
        return scoped;
    }

    public async Task<ChatResponse> GetResponseAsync(
     IEnumerable<ChatMessage> messages,
     ChatOptions? options = null,
     CancellationToken cancellationToken = default)
    {
        EnsureHeaders();
        var replay = await RestoreInputRequiredAsync(messages, cancellationToken);
        await InitializeModelAsync(replay, cancellationToken);
        if (decisionExecution) SetHistory(replay);
        var request = BuildResponseRequest(replay, options);
        request.Headers = GetInferenceHeaders(request.Model);
        var response = await http.GetResponses(
            request,
            providerHeaders: EffectiveModel.ProviderHeaders,
            ct: cancellationToken);

        if (decisionExecution)
            CaptureDecisionInference(response);
        var result = ToChatResponse(response);
        ClientToolDeclaration.MarkCalls(result.Messages.SelectMany(message => message.Contents), options);
        return result;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

}
