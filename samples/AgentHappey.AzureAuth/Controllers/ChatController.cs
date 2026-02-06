using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Agents.AI;
using AgentHappey.Core.ChatClient;
using AgentHappey.Common.Models;
using Microsoft.Extensions.Options;
using AgentHappey.Common.Extensions;
using Microsoft.Agents.AI.Workflows;
using AgentHappey.Core;
using Microsoft.Identity.Web;
using AgentHappey.Core.Extensions;
using Microsoft.Extensions.AI;
using AgentHappey.Core.MCP;
using AIHappey.Common.Model;
using System.Text;
using AIHappey.Vercel.Models;

namespace AgentHappey.AzureAuth.Controllers;

[ApiController]
[Route("api/chat")]
public class ChatController(IHttpClientFactory httpClientFactory,
    IOptions<Config> options,
    [FromServices] IStreamingContentMapper mapper,
    IServiceProvider serviceProvider,
    ITokenAcquisition tokenAcquisition) : ControllerBase
{
    private readonly string Endpoint = options.Value.AiConfig.AiEndpoint!;

    private readonly string? AiScopes = options.Value.AiConfig.AiScopes;

    static async IAsyncEnumerable<T> ToAsync<T>(IEnumerable<T> source)
    {
        foreach (var item in source)
        {
            yield return item;
            await Task.Yield(); // optioneel
        }
    }

    [HttpPost]
    [Authorize]
    public async Task<IActionResult> Post([FromBody] AgentRequest chatRequest, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(Endpoint);

        List<AIAgent> agents = [];
        ChatClientAgentRunOptions? runOpts = null;

        string downstreamToken = await tokenAcquisition.GetAccessTokenForUserAsync(
                 scopes: [AiScopes!],
                 user: HttpContext.User);

        client.DefaultRequestHeaders.Authorization = new("Bearer", downstreamToken);
        var messages = chatRequest.Messages.ToMessages().ToList();
        Response.ContentType = "text/event-stream";
        Response.Headers["x-vercel-ai-ui-message-stream"] = "v1";

        foreach (var agent in chatRequest.Agents)
        {
            var agentItem = new AgentChatClient(client,
                httpClientFactory,
                agent,
                new Dictionary<string, string?>(),
                serviceProvider.GetMcpTokenAsync,
                options.Value.AzureAd.TenantId);

            agentItem.SetHistory(messages);

            var tools = await agentItem.ConnectMcp(cancellationToken);

            var clientChatAgent = new ChatClientAgent(agentItem,
                            instructions: agent.Instructions,
                            name: agent.Name,
                            tools: tools,
                            description: agent.Description);

            agents.Add(clientChatAgent);

            runOpts = new(new()
            {
                Tools = tools
            });

            var connections = await agentItem.GetConnections();
            var items = connections.Select(z => new DataUIPart()
            {
                Type = "data-model-context",
                Data = z
            });

            await Response.WritePartsAsync(ToAsync(items), cancellationToken);
        }

        var yamlFile = chatRequest.Messages
                .SelectMany(m => m.Parts)
                .OfType<FileUIPart>()
                .LastOrDefault(f => f.MediaType == "application/yaml");

        string? yamlContent = null;

        if (yamlFile != null)
        {
            var url = yamlFile.Url;
            var base64 = url[(url.IndexOf("base64,") + "base64,".Length)..];
            yamlContent = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        }

        var aiAgent = agents.FirstOrDefault() ?? throw new Exception("No agent found");


        var provider = new InMemoryWorkflowAgentProvider(
            agents.Select(a => (a.Name!, a))
        );

        if (agents.Count > 1)
        {
            var workflow = chatRequest.WorkflowType switch
            {
                "sequential" => AgentWorkflowBuilder.BuildSequential(agents),
                "concurrent" => AgentWorkflowBuilder.BuildConcurrent(agents),
                "groupchat" => AgentWorkflowBuilder.CreateGroupChatBuilderWith(team =>
                                        new RoundRobinGroupChatManager(team)
                                        { MaximumIterationCount = chatRequest.WorkflowMetadata?.Groupchat?.MaximumIterationCount ?? 5 })
                                            .AddParticipants(agents)
                                            .Build(),
                "handoff" => agents.BuildHandoffWorkflow(chatRequest.WorkflowMetadata?.Handoff?.Handoffs),
                // "yaml" => yaml!.ParseWorkflow<string>(provider),
                _ => throw new InvalidOperationException("Invalid workflow type.")
            };

            if (!string.IsNullOrEmpty(yamlContent))
            {
                workflow = yamlContent.ParseWorkflow<string>(provider);

                await using var run = await InProcessExecution.StreamAsync(
                              workflow,
                              messages.LastOrDefault(a => a.Role == ChatRole.User)?.Text!,
                              cancellationToken: cancellationToken
                          );

                //await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

                var updates = run.WatchStreamAsync(cancellationToken);
                var mapped = mapper.MapAsync(updates, cancellationToken);

                await Response.WritePartsAsync(mapped, cancellationToken);

            }
            else
            {
                /* var wfAgent = workflow.AsAgent();
                 var updates = wfAgent.RunStreamingAsync(messages, options: runOpts, cancellationToken: cancellationToken);
                 var mapped = mapper.MapAsync(updates, cancellationToken);

                 await Response.WritePartsAsync(mapped, cancellationToken);*/

                // SINGLE execution path
                await using var run = await InProcessExecution.StreamAsync(
                    workflow,
                    messages,
                    cancellationToken: cancellationToken
                );

                await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

                var updates = run.WatchStreamAsync(cancellationToken);
                var mapped = mapper.MapAsync(updates, cancellationToken);

                await Response.WritePartsAsync(mapped, cancellationToken);
            }

        }
        else
        {
            var updates = aiAgent.RunStreamingAsync(messages, options: runOpts, cancellationToken: cancellationToken);
            var mapped = mapper.MapAsync(updates, cancellationToken);

            await Response.WritePartsAsync(mapped, cancellationToken);
        }

        return new EmptyResult();
    }
}

