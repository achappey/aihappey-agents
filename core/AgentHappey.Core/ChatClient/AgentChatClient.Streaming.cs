using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using AIHappey.Common.Model;
using AgentHappey.Common.Extensions;
using AgentHappey.Core.Extensions;
using System.Net.Mime;
using AIHappey.Vercel.Models;

namespace AgentHappey.Core.ChatClient;


public partial class AgentChatClient
{
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureHeaders();

        var agentSystemMessage = agent.GetSystemMessage(
            AgentExtensions.ToMcpServerParts(McpServerImplementations,
                McpServerInstructions,
                McpServerResources,
                McpServerResourceTemplates),
            tenantId);

        List<Tool> tools = [.. options?.Tools?.ToTools() ?? []];
        List<ChatMessage> messageList = [.. messages];
        SetHistory(messages);

        List<UIMessage> uiMessages = [agentSystemMessage, .. messageList.ToMessages()];

        var providerMetadata = new Dictionary<string, Dictionary<string, object>>
               {
                   {
                       agent.Model.Id.Split('/')[0],
                       agent.Model.ProviderMetadata ?? []
                   }
               };

        var req = new
        {
            messages = uiMessages,
            tools,
            providerMetadata,
            model = agent.Model.Id,
            response_format = agent.GetJsonOutputSchema(),
            temperature = agent.Model.Options?.Temperature ?? 1,
            id = Guid.NewGuid()
        };

        var json = JsonSerializer.Serialize(req, JsonSerializerOptions.Web);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/chat")
        {
            Content = content,
        };

        request.Headers.Accept.ParseAdd("text/event-stream");

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await foreach (var line in ReadSseLines(response, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var update = JsonSerializer.Deserialize<UIMessagePart>(line);
            if (update == null) continue;

            string modelName = string.Join("/", agent.Model.Id.Split("/").Skip(1));
            var parts = update.ToChatResponseUpdates(modelName, agent.Name);

            foreach (var part in parts ?? [])
                if (part is not null)
                    yield return part;

            List<AIContent> elicitParts = [];

            foreach (var pair in Logs ?? [])
            {
                // request part
                elicitParts.Add(new DataContent(
                    Encoding.UTF8.GetBytes(pair.ToJsonString()),
                    MediaTypeNames.Application.Json)
                {
                    Name = "model-context-log"
                });
            }

            foreach (var pair in ElicitPairs?.Values ?? [])
            {
                // request part
                elicitParts.Add(new DataContent(
                    Encoding.UTF8.GetBytes(JsonSerializer.Serialize(pair.Request, JsonSerializerOptions.Web)),
                    MediaTypeNames.Application.Json)
                {
                    Name = "elicitation-request-" + pair.Request.Mode
                });

                // result part (may be null if still pending)
                if (pair.Result != null)
                {
                    elicitParts.Add(new DataContent(
                        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(pair.Result, JsonSerializerOptions.Web)),
                        MediaTypeNames.Application.Json)
                    {
                        Name = "elicitation-result-" + pair.Result.Action
                    });
                }
            }

            if (elicitParts?.Count > 0)
                yield return new ChatResponseUpdate(ChatRole.Assistant, elicitParts);

            ElicitPairs?.Clear();
            Logs?.Clear();
        }
    }

    private static async IAsyncEnumerable<string> ReadSseLines(
        HttpResponseMessage response,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line == null) yield break;

            // SSE sends "data: ..."
            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var payload = line["data:".Length..].Trim();
                if (payload.Length > 0)
                    yield return payload;
            }
        }
    }
}