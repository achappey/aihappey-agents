using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using AgentHappey.Common.Models;
using AgentHappey.Common.Extensions;
using System.Net.Mime;

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

        var reqMessages = new List<object>();

        foreach (var m in messages)
        {
            if (m.Role == ChatRole.Tool)
            {
                foreach (var fr in m.Contents.OfType<FunctionResultContent>())
                {
                    reqMessages.Add(new
                    {
                        role = ChatRole.Tool.Value,
                        tool_call_id = fr.CallId,
                        content = new List<object>() { new {
                            type = "text",
                            text = JsonSerializer.Serialize(fr.Result, JsonSerializerOptions.Web) } } // raw textual result
                    });
                }

                continue; // IMPORTANT: skip normal serialization
            }

            if (m.Role == ChatRole.Assistant)
                reqMessages.Add(new
                {
                    role = m.Role.ToString().ToLowerInvariant(),
                    tool_calls = m.Contents
                        .OfType<FunctionCallContent>()
                        .Select(a => new
                        {
                            id = a.CallId,
                            type = "function",
                            function = new { a.Name, arguments = JsonSerializer.Serialize(a.Arguments) }
                        })
                        .ToList(),

                    content = m.Contents
                        .Select<AIContent, object?>(a =>
                        {
                            return a switch
                            {
                                TextContent t => new { type = "text", text = t.Text },

                                DataContent d =>
                                    d.MediaType.StartsWith("image/")
                                        ? new { type = "input_image", image_url = new { url = d.Base64Data } }
                                        : null,

                                _ => null
                            };
                        })
                        .Where(x => x != null)
                        .ToList()
                });
            else
                reqMessages.Add(new
                {
                    role = m.Role.ToString().ToLowerInvariant(),
                    content = m.Contents
                        .Select<AIContent, object?>(a =>
                        {
                            return a switch
                            {
                                TextContent t => new { type = "text", text = t.Text },
                                DataContent d =>
                                    d.MediaType.StartsWith("image/")
                                        ? new { type = "input_image", image_url = new { url = d.Base64Data } }
                                        : null,

                                _ => null
                            };
                        })
                        .Where(x => x != null)
                        .ToList()
                });
        }

        var req = new
        {
            messages = reqMessages,
            tools = options?.Tools?.OfType<AIFunctionDeclaration>().Select(a => new
            {
                type = "function",
                function = new
                {
                    a.Name,
                    a.Description,
                    Parameters = a.JsonSchema
                }
            }) ?? [],
            response_format = agent.GetCompletionsOutputSchema(),
            stream = false,
            tool_choice = "auto",
            parallel_tool_calls = true,
            model = agent.Model.Id,
            temperature = agent.Model.Options?.Temperature ?? 1
        };

        var json = JsonSerializer.Serialize(req, JsonSerializerOptions.Web);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await http.PostAsync("chat/completions", content, cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var msg = root
            .GetProperty("choices")[0]
            .GetProperty("message");

        var created = root
                   .GetProperty("created").GetInt32();

        var parts = new List<AIContent>();

        // ------------------------
        // 1) TEXT CONTENT
        // ------------------------
        if (msg.TryGetProperty("content", out var contentElement) &&
            contentElement.ValueKind == JsonValueKind.String)
        {
            var text = contentElement.GetString();

            if (!string.IsNullOrEmpty(text))
            {
                parts.Add(new TextContent(text));

                if (agent.OutputSchema != null)
                {
                    var bytes = Encoding.UTF8.GetBytes(text);

                    parts.Add(new DataContent(bytes, MediaTypeNames.Application.Json)
                    {
                        Name = agent.GetOutputName()
                    });
                }
            }
        }

        // ------------------------
        // 2) TOOL CALLS
        // ------------------------
        if (msg.TryGetProperty("tool_calls", out var toolCalls) &&
            toolCalls.ValueKind == JsonValueKind.Array)
        {
            static string Unwrap(string s)
            {
                s = s.Trim();

                // Remove leading/trailing quotes until valid JSON object
                while (s.StartsWith("\"") && s.EndsWith("\""))
                {
                    s = JsonSerializer.Deserialize<string>(s)!;
                }

                return s;
            }

            foreach (var tc in toolCalls.EnumerateArray())
            {
                var id = tc.GetProperty("id").GetString();
                var function = tc.GetProperty("function");
                var name = function.GetProperty("name").GetString();
                var args = function.GetProperty("arguments").GetRawText();
                var clean = Unwrap(args);

                parts.Add(new FunctionCallContent(
                    id!,
                    name!,
                    JsonSerializer.Deserialize<Dictionary<string, object?>>(clean)! // raw JSON args
                ));
            }
        }

        foreach (var pair in ElicitPairs?.Values ?? [])
        {
            // request part
            parts.Add(new DataContent(
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(pair.Request)),
                MediaTypeNames.Application.Json)
            {
                Name = "elicitation-request-" + pair.Request.Mode
            });

            // result part (may be null if still pending)
            if (pair.Result != null)
            {
                parts.Add(new DataContent(
                    Encoding.UTF8.GetBytes(JsonSerializer.Serialize(pair.Result)),
                    MediaTypeNames.Application.Json)
                {
                    Name = "elicitation-result-" + pair.Result.Action
                });
            }
        }


        var finalMessage = new ChatMessage(ChatRole.Assistant, parts);

        var usage = root.GetProperty("usage");

        return new ChatResponse
        {
            CreatedAt = DateTimeOffset.FromUnixTimeSeconds(created),
            Usage = new UsageDetails()
            {
                TotalTokenCount = usage.GetProperty("total_tokens").GetInt32(),
                InputTokenCount = usage.GetProperty("prompt_tokens").GetInt32(),
                OutputTokenCount = usage.GetProperty("completion_tokens").GetInt32()
            },
            ResponseId = root.GetProperty("id").GetString(),
            Messages = [finalMessage]
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

}