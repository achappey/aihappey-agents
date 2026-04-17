using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using AgentHappey.Common.Models;
using AgentHappey.Common.Extensions;
using System.Net.Mime;
using AIHappey.Responses.Extensions;

namespace AgentHappey.Core.ChatClient;

public partial class AgentChatClient(
    HttpClient http,
    IHttpClientFactory httpClientFactory,
    Agent agent,
    IDictionary<string, string?> headers,
    Func<string, CancellationToken, Task<string?>>? getMcpToken = null,
    string? tenantId = null) : IChatClient
{
    public async void EnsureHeaders()
    {
        if (headers != null)
            foreach (var header in headers.Where(z => !http.DefaultRequestHeaders.Contains(z.Key)))
                http.DefaultRequestHeaders.Add(header.Key, header.Value);
    }

    public async Task<ChatResponse> GetResponseAsync(
     IEnumerable<ChatMessage> messages,
     ChatOptions? options = null,
     CancellationToken cancellationToken = default)
    {
        EnsureHeaders();

        var request = BuildResponseRequest(messages, options);
        var response = await http.GetResponses(request, ct: cancellationToken);

        return ToChatResponse(response);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

}
