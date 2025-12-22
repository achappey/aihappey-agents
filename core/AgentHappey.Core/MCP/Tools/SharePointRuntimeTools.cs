using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentHappey.Common.Models;
using AgentHappey.Core.ChatClient;
using AgentHappey.Core.Extensions;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Declarative;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Identity.Web;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentHappey.Core.MCP.Tools;

[McpServerToolType]
public class SharePointRuntimeTools
{


    [Description("Ask an AI agent by SharePoint agent.json file.")]
    [McpServerTool(Title = "Ask an AI agent",
        Name = "agent_sharepoint_runtime_ask",
        Idempotent = false,
        ReadOnly = false,
        OpenWorld = true)]
    public static async Task<CallToolResult> AgentSharePointRuntime_Ask(
        [Description("Sharepoint or OneDrive link of the agent json file")] string agentJsonFileUrl,
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

        IEnumerable<ChatMessage> messages = [new ChatMessage(ChatRole.User, task)];

        var graph = await services.GetOboGraphClientAsync(
            ["https://graph.microsoft.com/.default"],
            cancellationToken) ?? throw new Exception("Graphclient not found");

        var item = await graph
            .Shares[agentJsonFileUrl.EncodeSharingUrl()]
            .DriveItem
            .Content
            .GetAsync(cancellationToken: cancellationToken) ?? throw new Exception("Agent file not found"); ;

        string json;

        using (var reader = new StreamReader(item))
        {
            json = await reader.ReadToEndAsync(cancellationToken);
        }

        var agent = JsonSerializer.Deserialize<Agent>(json, JsonSerializerOptions.Web)
            ?? throw new Exception("Failed to parse Agent JSON file.");

        var user = context.HttpContext?.User ?? throw new Exception("User not found");

        string downstreamToken = await tokenAcquisition.GetAccessTokenForUserAsync(
                        scopes: [aiConfig.AiScopes!],
                        user: user);

        client.DefaultRequestHeaders.Authorization = new("Bearer", downstreamToken);

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

    [Description("Run a workflow by a group of AI agents.")]
    [McpServerTool(Title = "Run an AI workflow",
    Name = "agent_sharepoint_runtime_run_workflow",
    Idempotent = false,
    ReadOnly = false,
    OpenWorld = true)]
    public static async Task<CallToolResult> AgentSharePointRuntime_RunWorkflow(
    [Description("Sharepoint or OneDrive link of the agent json files")] List<string> agentJsonFileUrls,
    [Description("Sharepoint or OneDrive link of the workflow yaml file")] string workflowYaml,
    string task,
    IServiceProvider services,
    RequestContext<CallToolRequestParams> requestContext,
    CancellationToken cancellationToken = default)

    {
        try
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

            if (!agentJsonFileUrls.Any()) return new CallToolResult()
            {
                IsError = true,
                Content = [new TextContentBlock() {
                Text = "Agents missing. Please include the Agent JSON file urls."
            }]
            };
            IEnumerable<ChatMessage> messages = [new ChatMessage(ChatRole.User, task)];

            var graph = await services.GetOboGraphClientAsync(
                ["https://graph.microsoft.com/.default"],
                cancellationToken) ?? throw new Exception("Graphclient not found");

            List<AIAgent> flowAgents = [];
            List<AITool> tools = [];
            //  List<AIFunction> functions = [];
            ChatClientAgentRunOptions? runOpts = null;
            foreach (var agentJson in agentJsonFileUrls ?? [])
            {
                var item = await graph
                         .Shares[agentJson.EncodeSharingUrl()]
                         .DriveItem
                         .Content
                         .GetAsync(cancellationToken: cancellationToken) ?? throw new Exception("Agent file not found");

                string json;

                using (var reader = new StreamReader(item))
                {
                    json = await reader.ReadToEndAsync(cancellationToken);
                }

                var agent = JsonSerializer.Deserialize<Agent>(json, JsonSerializerOptions.Web)
                    ?? throw new Exception("Failed to parse Agent JSON file.");

                var agentChatClientItem = new AgentChatClient(client,
                                httpClientFactory,
                                agent,
                                new Dictionary<string, string?>(),
                                services.GetMcpTokenAsync,
                                azureAd.TenantId);
                                
                agentChatClientItem.SetHistory(messages);

                var agentTools = await agentChatClientItem.ConnectMcp(cancellationToken);

                tools.AddRange(agentTools);
                var clientChatAgent = new ChatClientAgent(agentChatClientItem,
                                instructions: agent.Instructions,
                                name: agent.Name,
                                tools: tools,
                                description: agent.Description);

                runOpts = new(new()
                {
                    Tools = agentTools
                });

                flowAgents.Add(clientChatAgent);
            }

            var provider = new InMemoryWorkflowAgentProvider(
                       flowAgents.Select(a => (a.Name!, a)),
                       functions: tools,
                       allowMultipleToolCalls: true);

            var user = context.HttpContext?.User ?? throw new Exception("User not found");

            var yamlItem = await graph
                             .Shares[workflowYaml.EncodeSharingUrl()]
                             .DriveItem
                             .Content
                             .GetAsync(cancellationToken: cancellationToken) ?? throw new Exception("Agent file not found");

            string yaml;

            using (var reader = new StreamReader(yamlItem))
            {
                yaml = await reader.ReadToEndAsync(cancellationToken);
            }


            string downstreamToken = await tokenAcquisition.GetAccessTokenForUserAsync(
                            scopes: [aiConfig.AiScopes!],
                            user: user);

            client.DefaultRequestHeaders.Authorization = new("Bearer", downstreamToken);

            var workflow = yaml.ParseWorkflow<string>(provider);

            await using var run = await InProcessExecution.RunAsync(
                               workflow,
                               messages.LastOrDefault(a => a.Role == ChatRole.User)?.Text!,
                               // messages.LastOrDefault()?.Text!,
                               cancellationToken: cancellationToken
                           );
            // var response = await workflow.AsAgent().RunAsync(messages, options: runOpts, cancellationToken: cancellationToken);

            var events = run.OutgoingEvents
                // jij bepaalt zelf welke events je wilt
                //.Where(e => e is WorkflowOutputEvent || e is WorkflowErrorEvent)
                .Select(e => e.ToString())
                .ToList();

            return new()
            {
                StructuredContent = JsonSerializer.SerializeToNode(
                    new { Events = events },
                    JsonSerializerOptions.Web
                )
            };
        }
        catch (Exception e)
        {
            return new()
            {
                IsError = true,
                Content = [new TextContentBlock() {
                    Text = e.Message + e.StackTrace
                }]
                /*    StructuredContent = await JsonNode.ParseAsync(
                        BinaryData.FromObjectAsJson(run,
                            JsonSerializerOptions.Web)
                            .ToStream(),
                        cancellationToken: cancellationToken)*/
            };
        }
    }
}
