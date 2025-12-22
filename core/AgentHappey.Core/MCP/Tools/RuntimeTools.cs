using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentHappey.Common.Extensions;
using AgentHappey.Common.Models;
using AgentHappey.Core.ChatClient;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Identity.Web;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentHappey.Core.MCP.Tools;

[McpServerToolType]
public class RuntimeTools
{
    [Description("Ask a default AI agent.")]
    [McpServerTool(Title = "Ask a default AI agent", Idempotent = false,
        ReadOnly = false,
        OpenWorld = true)]
    public static async Task<CallToolResult> AgentRuntime_Ask(
        string agentName,
        string task,
        IServiceProvider services,
        RequestContext<CallToolRequestParams> _,
        CancellationToken cancellationToken = default)
    {
        var agents = services.GetRequiredService<ReadOnlyCollection<Agent>>();
        var context = services.GetRequiredService<IHttpContextAccessor>();
        var httpClientFactory = services.GetRequiredService<IHttpClientFactory>();
        var mapper = services.GetRequiredService<IStreamingContentMapper>();
        var aiConfig = services.GetRequiredService<AiConfig>();
        var mcpConfig = services.GetRequiredService<McpConfig>();
        var azureAd = services.GetRequiredService<AzureAd>();
        var tokenAcquisition = services.GetRequiredService<ITokenAcquisition>();
        var client = httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(aiConfig.AiEndpoint);
        var agent = agents.FirstOrDefault(a => a.Name == agentName) ?? throw new Exception("Agent not found");
        IEnumerable<ChatMessage> messages = [new ChatMessage(ChatRole.User, task)];

        if (context.HttpContext?.User != null && !string.IsNullOrEmpty(aiConfig.AiScopes))
        {
            string downstreamToken = await tokenAcquisition.GetAccessTokenForUserAsync(
                            scopes: [aiConfig.AiScopes],
                            user: context.HttpContext?.User);

            client.DefaultRequestHeaders.Authorization = new("Bearer", downstreamToken);
        }

        var agentItem = new AgentChatClient(client, httpClientFactory, agent,
             context.HttpContext?.Request.Headers.Where(a => a.Key.StartsWith("X-", StringComparison.OrdinalIgnoreCase))
                 .ToDictionary(a => a.Key, a => a.Value.FirstOrDefault()) ?? [],
                 services.GetMcpTokenAsync,
                 azureAd.TenantId);

        var tools = await agentItem.ConnectMcp(cancellationToken);

        var aiAgent = new ChatClientAgent(agentItem,
            instructions: agent.Instructions,
            name: agent.Name,
            tools: tools,
            description: agent.Description);

        ChatClientAgentRunOptions runOpts = new(new()
        {
            Tools = tools
        });

        var response = await aiAgent.RunAsync(messages, options: runOpts, cancellationToken: cancellationToken);

        return new()
        {
            StructuredContent = await JsonNode.ParseAsync(
                BinaryData.FromObjectAsJson(response,
                    JsonSerializerOptions.Web)
                    .ToStream(),
                cancellationToken: cancellationToken)
        };
    }

    [Description("Ask a custom AI agent by supplying name, descirption, instructions, MCP servers, policy and capabilities.")]
    [McpServerTool(Title = "Ask a custom AI agent",
       Idempotent = false,
       ReadOnly = false,
       OpenWorld = true)]
    public static async Task<CallToolResult> AgentRuntime_AskCustom(
       IServiceProvider services,
       RequestContext<CallToolRequestParams> _,
       string agentName,
       string description,
       string instructions,
       string task,
       IEnumerable<string>? mcpServers = null,
       bool? readOnly = true,
       bool? idempotent = true,
       bool? openWorld = false,
       bool? destructive = false,
       bool? samplingCapability = false,
       bool? elicitCapability = false,
       CancellationToken cancellationToken = default)
    {
        var agents = services.GetRequiredService<ReadOnlyCollection<Agent>>();
        var context = services.GetRequiredService<IHttpContextAccessor>();
        var httpClientFactory = services.GetRequiredService<IHttpClientFactory>();
        var mapper = services.GetRequiredService<IStreamingContentMapper>();
        var aiConfig = services.GetRequiredService<AiConfig>();
        var mcpConfig = services.GetRequiredService<McpConfig>();
        var azureAd = services.GetRequiredService<AzureAd>();
        var tokenAcquisition = services.GetRequiredService<ITokenAcquisition>();
        var client = httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(aiConfig.AiEndpoint);
        
        var agent = new Agent()
        {
            Name = agentName,
            Description = description,
            Instructions = instructions,
            McpServers = mcpServers?
                .Select(url => url.ToMcpServer())
                .ToDictionary(a => a.Url.ToReverseDnsKey(), a => a),
            McpClient = new()
            {
                Policy = new McpPolicy
                {
                    ReadOnly = readOnly,
                    Idempotent = idempotent,
                    OpenWorld = openWorld,
                    Destructive = destructive
                },
                Capabilities = new ClientCapabilities
                {
                    Sampling = samplingCapability == true ? new() : null,
                    Elicitation = elicitCapability == true ? new() : null
                }
            },
       /*     Mcp = new()
            {
                Servers = mcpServers?.ToMcpServers(),
                Policy = new()
                {
                    ReadOnly = readOnly,
                    Idempotent = idempotent,
                    OpenWorld = openWorld,
                    Destructive = destructive
                },
                ClientCapabilities = new()
                {
                    Sampling = samplingCapability == true ? new() : null,
                    Elicitation = elicitCapability == true ? new() : null
                }
            }*/
        };

        IEnumerable<ChatMessage> messages = [new ChatMessage(ChatRole.User, task)];

        if (context.HttpContext?.User != null && !string.IsNullOrEmpty(aiConfig.AiScopes))
        {
            string downstreamToken = await tokenAcquisition.GetAccessTokenForUserAsync(
                            scopes: [aiConfig.AiScopes],
                            user: context.HttpContext?.User);

            client.DefaultRequestHeaders.Authorization = new("Bearer", downstreamToken);
        }

        var agentItem = new AgentChatClient(client, httpClientFactory, agent,
             context.HttpContext?.Request.Headers.Where(a => a.Key.StartsWith("X-", StringComparison.OrdinalIgnoreCase))
                 .ToDictionary(a => a.Key, a => a.Value.FirstOrDefault()) ?? [],
                 services.GetMcpTokenAsync,
                 azureAd.TenantId);

        var tools = await agentItem.ConnectMcp( cancellationToken);

        var aiAgent = new ChatClientAgent(agentItem,
            instructions: agent.Instructions,
            name: agent.Name,
            tools: tools,
            description: agent.Description);

        ChatClientAgentRunOptions runOpts = new(new()
        {
            Tools = tools
        });

        var response = await aiAgent.RunAsync(messages, options: runOpts, cancellationToken: cancellationToken);

        return new()
        {
            StructuredContent = await JsonNode.ParseAsync(
                BinaryData.FromObjectAsJson(response,
                    JsonSerializerOptions.Web)
                    .ToStream(),
                cancellationToken: cancellationToken)
        };
    }
}
