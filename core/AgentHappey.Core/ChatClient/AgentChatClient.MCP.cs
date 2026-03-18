using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using AgentHappey.Common.Extensions;
using AgentHappey.Core.Extensions;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;

namespace AgentHappey.Core.ChatClient;

public class AIModelList
{
    public IEnumerable<AIModel>? Data { get; set; }
}

public class AIModel
{
    public string Id { get; set; } = null!;
}

public class ElicitationPair
{
    public ElicitRequestParams Request { get; set; } = null!;
    public ElicitResult? Result { get; set; }   // initially null
}

public partial class AgentChatClient
{
    private readonly ConcurrentDictionary<string, Implementation> McpServerImplementations = [];

    private readonly ConcurrentDictionary<string, McpClient> McpClients = [];

    private readonly ConcurrentDictionary<string, string> McpServerInstructions = [];

    private readonly ConcurrentDictionary<string, IEnumerable<McpClientResource>> McpServerResources = [];

    private readonly ConcurrentDictionary<string, IEnumerable<McpClientResourceTemplate>> McpServerResourceTemplates = [];

    private readonly ConcurrentDictionary<string, ElicitationPair> ElicitPairs = new();

    private readonly ConcurrentBag<JsonNode> Logs = new();

    private readonly ConcurrentDictionary<string, AITool> Tools = new();

    //  private readonly ConcurrentBag<ChatMessage> Messages = [];

    private ChatMessage[] _history = [];

    public void SetHistory(IEnumerable<ChatMessage> msgs)
    {
        // materialize once, stable ordering
        var list = msgs as IList<ChatMessage> ?? [.. msgs];
        Volatile.Write(ref _history, [.. list]);
    }

    private ChatMessage[] GetHistorySnapshot()
        => Volatile.Read(ref _history);

    public async Task<IEnumerable<McpConnectionInfo>> GetConnections()
    {
        var options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        return await Task.FromResult(
            McpClients.Select(a => new McpConnectionInfo
            {
                Url = a.Key,
                NegotiatedProtocolVersion = a.Value.NegotiatedProtocolVersion,
                ServerInfo = a.Value.ServerInfo,
                ServerCapabilities = a.Value.ServerCapabilities,
                ServerInstructions = a.Value.ServerInstructions,
                SessionId = a.Value.SessionId
            })
        );
    }

    public class McpConnectionInfo
    {
        public string Url { get; init; } = null!;
        public string? SessionId { get; init; }
        public string? NegotiatedProtocolVersion { get; init; }
        public object? ServerInfo { get; init; }
        public object? ServerCapabilities { get; init; }
        public string? ServerInstructions { get; init; }
    }

    public async Task<List<AITool>> ConnectMcp(CancellationToken cancellationToken)
    {
        List<AITool> tools = [];

        McpClientOptions options = new()
        {
            Capabilities = agent.McpClient?.Capabilities,
            ClientInfo = agent.ToImplementation()
        };

        var enabledServers = agent.McpServers?.Where(a => a.Value.Disabled != true);

        foreach (var servers in enabledServers ?? [])
        {
            var httpClient = httpClientFactory.CreateClient();
            var url = servers.Value.Url.ToLowerInvariant();

            var transport = new HttpClientTransport(new()
            {
                Endpoint = new Uri(url),
                Name = agent.Name,
            }, httpClient, ownsHttpClient: true);

            var handlers = new Dictionary<string, Func<JsonRpcNotification, CancellationToken, ValueTask>>
            {
                ["notifications/message"] = async (n, ct) =>
                {
                    var msg = n.Params?.ToString();

                    if (n.Params != null)
                        Logs.Add(n.Params);

                    await ValueTask.CompletedTask;
                },

                ["notifications/progress"] = async (n, ct) =>
                {
                    // handle progress
                    await ValueTask.CompletedTask;
                }
            };

            options.Handlers.NotificationHandlers = handlers;

            if (agent.McpClient?.Capabilities?.Elicitation != null)
                options.Handlers.ElicitationHandler = async (value, cancel) =>
                           {
                               if (headers != null)
                                   foreach (var header in headers.Where(z => !http.DefaultRequestHeaders.Contains(z.Key)))
                                       http.DefaultRequestHeaders.Add(header.Key, header.Value);

                               var id = value?.ElicitationId ?? Guid.NewGuid().ToString("N");
                               var pair = ElicitPairs.GetOrAdd(id, _ => new ElicitationPair
                               {
                                   Request = value!
                               });
                               List<ChatMessage> history = [];

                               var historyBlocks =
                                    history
                                    .Where(a => a.Contents.Any())
                                    .Select(m => new
                                    {
                                        type = "text",
                                        text = JsonSerializer.Serialize(new
                                        {
                                            role = m.Role,
                                            content = m.Contents
                                        }, JsonSerializerOptions.Web)
                                    })
                                    .ToArray();

                               var systemContent =
                                   new[] {
                                    new {
                                        type = "text",
                                        text = JsonSerializer.Serialize(new {
                                            agent.Name,
                                            agent.Description,
                                            agent.Instructions
                                        }, JsonSerializerOptions.Web)
                                    }
                                   };

                               // 2) FULL MESSAGE ARRAY
                               var messages = new[]
                               {
                                    // SYSTEM TURN
                                    new {
                                        role = ChatRole.System.Value,
                                        content = systemContent
                                    },

                                    // USER TURN (history → elicit prompt → value)
                                    new {
                                        role = ChatRole.User.Value,
                                        content =
                                            historyBlocks
                                                .Concat([
                                                    new {
                                                        type = "text",
                                                        text = ElicitPrompt
                                                    },
                                                    new {
                                                        type = "text",
                                                        text = JsonSerializer.Serialize(value, JsonSerializerOptions.Web)
                                                    }
                                                ])
                                                .ToArray()
                                    }
                                };

                               var request = new
                               {
                                   messages,
                                   tool_choice = "none",
                                   parallel_tool_calls = true,
                                   stream = false,
                                   tools = Array.Empty<string>(),
                                   reasoning_effort = "low",
                                   //  model = string.Join("/", agent.Model.Id.Split("/").Skip(1)),
                                   model = "openai/gpt-5.2",
                                   temperature = agent.Model.Options?.Temperature ?? 1
                               };

                               var json = JsonSerializer.Serialize(request, JsonSerializerOptions.Web);
                               using var content = new StringContent(json, Encoding.UTF8, "application/json");

                               var response = await http.PostAsync("v1/chat/completions", content, cancellationToken);
                               response.EnsureSuccessStatusCode();

                               var body = await response.Content.ReadAsStringAsync(cancellationToken)
                                   ?? throw new Exception("Something went wrong");

                               using var doc = JsonDocument.Parse(body);

                               var assistantText =
                                   doc.RootElement
                                      .GetProperty("choices")[0]
                                      .GetProperty("message")
                                      .GetProperty("content")
                                      .GetString();

                               if (string.IsNullOrWhiteSpace(assistantText))
                                   throw new Exception("No assistant content found in response.");

                               var result = JsonSerializer.Deserialize<ElicitResult>(assistantText, JsonSerializerOptions.Web)!;

                               result.Meta ??= [];
                               result.Meta.Add("timestamp", DateTime.UtcNow.ToString("o"));
                               result.Meta.Add("author", agent.Name);
                               pair.Result = result;

                               return result;
                           };

            if (agent.McpClient?.Capabilities?.Sampling != null)
                options.Handlers.SamplingHandler = async (value, progress, cancel) =>
                {
                    if (value == null)
                    {
                        throw new Exception();
                    }

                    var modelResponse = await http.GetAsync("v1/models", cancellationToken);
                    var models = await modelResponse.Content.ReadFromJsonAsync<AIModelList>(cancellationToken: cancel);

                    var modelHint = (value.ModelPreferences?.Hints?.FirstOrDefault()) ?? throw new Exception();
                    var preferedProviders = value.Metadata?.Select(a => a.Key);

                    var allModelProviders = models?.Data?.Where(l => l.Id.EndsWith(modelHint.Name!)
                        && (l.Id.Split("/").Length == modelHint.Name?.Split("/").Length + 1)
                        && preferedProviders?.Contains(l.Id.Split("/").First()) == true)
                        .Where(a => !string.IsNullOrEmpty(a.Id))
                        .Select(a => a.Id.Split("/").First()) ?? [];

                    foreach (var modelProvider in allModelProviders)
                    {
                        try
                        {
                            value.ModelPreferences ??= new();
                            value.ModelPreferences.Hints = value?.ModelPreferences?.Hints?.Select(a =>
                                new ModelHint()
                                {
                                    Name = $"{modelProvider}/{modelHint.Name}",
                                }).ToList();

                            if (headers != null)
                                foreach (var header in headers.Where(z => !http.DefaultRequestHeaders.Contains(z.Key)))
                                    http.DefaultRequestHeaders.Add(header.Key, header.Value);

                            var json = JsonSerializer.Serialize(value, JsonSerializerOptions.Web);
                            using var content = new StringContent(json, Encoding.UTF8, "application/json");

                            var response = await http.PostAsync("sampling", content, cancellationToken);
                            response.EnsureSuccessStatusCode();

                            return await response.Content.ReadFromJsonAsync<CreateMessageResult>(cancellationToken)
                                ?? throw new Exception("Something went wrong");
                        }
                        catch (Exception) { }
                    }

                    throw new Exception("Sampling failed");
                };

            McpClient? mcpClient = null;

            try
            {
                mcpClient = await McpClient.CreateAsync(transport,
                    clientOptions: options,
                    cancellationToken: cancellationToken);

            }
            catch (Exception)
            {
                if (getMcpToken != null)
                {
                    var token = await getMcpToken(url, cancellationToken);

                    if (token != null)
                    {
                        httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

                        mcpClient = await McpClient.CreateAsync(transport,
                                           clientOptions: options,
                                           cancellationToken: cancellationToken);
                    }
                }
            }

            if (mcpClient == null)
                continue;

            McpClients.AddOrUpdate(url, mcpClient, (_, __) => mcpClient);
            McpServerImplementations.AddOrUpdate(url, mcpClient.ServerInfo, (_, __) => mcpClient.ServerInfo);

            if (!string.IsNullOrEmpty(mcpClient.ServerInstructions))
                McpServerInstructions.AddOrUpdate(url, mcpClient.ServerInstructions, (_, __) => mcpClient.ServerInstructions);

            if (mcpClient.ServerCapabilities.Tools != null)
            {
                IList<McpClientTool>? allTools = await mcpClient.ListToolsAsync(cancellationToken: cancellationToken);

                if (agent.McpClient?.Policy != null) allTools = [.. allTools
                    .Where(a => (agent.McpClient?.Policy?.ReadOnly != true || a.ProtocolTool.Annotations?.ReadOnlyHint == true)
                        && (agent.McpClient?.Policy?.Destructive != false || a.ProtocolTool.Annotations?.DestructiveHint != true)
                        && (agent.McpClient?.Policy?.OpenWorld != false || a.ProtocolTool.Annotations?.OpenWorldHint != true)
                        && (agent.McpClient?.Policy?.Idempotent != true || a.ProtocolTool.Annotations?.IdempotentHint == true))];

                tools.AddRange(allTools.Cast<AITool>());
            }

            if (mcpClient.ServerCapabilities.Resources != null)
            {
                var result = await mcpClient.ListResourcesAsync(cancellationToken: cancellationToken);
                McpServerResources.AddOrUpdate(url, result, (_, __) => result);

                var resultTemplates = await mcpClient.ListResourceTemplatesAsync(cancellationToken: cancellationToken);
                McpServerResourceTemplates.AddOrUpdate(url, resultTemplates, (_, __) => resultTemplates);
            }
        }

        if (!McpServerResources.IsEmpty || !McpServerResourceTemplates.IsEmpty)
            tools.Add(AIFunctionFactory.Create(ReadResourceAsync));

        foreach (var tool in tools)
        {
            Tools.AddOrUpdate(
                    tool.Name,
                    _ => tool,          // add
                    (_, __) => tool     // update (overwrite)
                );
        }

        return tools;
    }

    [DisplayName("read_resource")]
    [Description("Reads a resource by URI from an MCP server. serverUrl must be a connected MCP server; resource uri can be any valid MCP resource URI.")]
    private async Task<CallToolResult> ReadResourceAsync(
           [Description("URL of the MCP server. Must be the URL of a connected MCP server (not the resource URI).")]
        string serverUrl,
           [Description("URI of the resource to read (the MCP resource URI).")]
        string uri,
           CancellationToken cancellationToken)
    {
        try
        {
            if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out _))
                throw new ArgumentException($"Invalid serverUrl format: '{serverUrl}'.");

            if (!McpClients.TryGetValue(serverUrl.ToLowerInvariant(), out var client))
                throw new ArgumentException(
                    $"Unknown MCP serverUrl: '{serverUrl}'. Not connected. Connected servers: {string.Join(", ", McpClients.Keys)}");

            if (string.IsNullOrWhiteSpace(uri))
                throw new ArgumentException("uri is required.");

            var result = await client.ReadResourceAsync(new Uri(uri), cancellationToken: cancellationToken);

            return new CallToolResult
            {
                IsError = false,
                Content = [.. result.Contents.ToContentBlocks()]
            };
        }
        catch (Exception e)
        {
            return new CallToolResult
            {
                IsError = true,
                Content = [e.Message.ToContentBlock()]
            };
        }
    }

    //Use all available information in the request to fill in the form fields exactly. Decline if any required field cannot be filled with certainty.
    private static readonly string ElicitPrompt = @"You are an AI form-confirmation agent. 
        Just confirm the incoming request, nothing else. Use the incoming values (empty fields possible), do NOT make up information, just confirm the incoming form. 
        Respond with a valid JSON object matching the schema below:

        {
        ""action"": ""accept | decline"",
        ""content"": { /* dictionary of submitted form data, required if action is 'accept' */ }
        }

        Rules:
        - Use ""accept"" if you can fill in all required fields and include a 'content' object with all field values.
        - Use ""decline"" if the request should be declined. Omit 'content'.

        Examples:
        - All fields can be filled:
        {
        ""action"": ""accept"",
        ""content"": {
            ""email"": ""john@example.com"",
            ""age"": 42
        }
        }

        - Declined:
        {
        ""action"": ""decline"",
        ""_meta"": {
          ""reason"": ""Your decline reason""
        }
        }

        Always use this format. Do NOT include any explanation or other text outside the JSON object.";

}