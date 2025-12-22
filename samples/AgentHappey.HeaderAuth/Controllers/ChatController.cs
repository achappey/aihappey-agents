using Microsoft.AspNetCore.Mvc;
using Microsoft.Agents.AI;
using AgentHappey.Core;
using Microsoft.Extensions.AI;
using AgentHappey.Common.Models;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Options;
using AgentHappey.Common.Extensions;
using AgentHappey.Core.ChatClient;
using AgentHappey.Core.Extensions;

namespace AgentHappey.HeaderAuth.Controllers;

[ApiController]
[Route("api/chat")]
public class ChatController(IHttpClientFactory httpClientFactory,
    IOptions<Config> options,
    [FromServices] IStreamingContentMapper mapper) : ControllerBase
{
    private readonly string Endpoint = options.Value.AiConfig.AiEndpoint;

    [HttpPost]
    public async Task<IActionResult> Post([FromBody] AgentRequest chatRequest, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(Endpoint);

        List<AIAgent> agents = [];
        ChatClientAgentRunOptions? runOpts = null;

        var messages = chatRequest.Messages.ToMessages().ToList();

        foreach (var agent in chatRequest.Agents)
        {
            var agentItem = new AgentChatClient(client, httpClientFactory, agent,
                HttpContext.Request.Headers.Where(a => a.Key.StartsWith("X-", StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(a => a.Key, a => a.Value.FirstOrDefault()), null);

            var tools = await agentItem.ConnectMcp(cancellationToken);

            agents.Add(new ChatClientAgent(agentItem,
                instructions: agent.Instructions,
                name: agent.Name,
                tools: tools,
                description: agent.Description));

            runOpts = new ChatClientAgentRunOptions(new ChatOptions
            {
                Tools = tools
            });

        }

        var aiAgent = agents.FirstOrDefault() ?? throw new Exception("No agent found");

        Response.ContentType = "text/event-stream";
        Response.Headers["x-vercel-ai-ui-message-stream"] = "v1";

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
                _ => throw new InvalidOperationException("Invalid workflow type.")
            };

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
        else
        {
            var updates = aiAgent.RunStreamingAsync(messages, options: runOpts, cancellationToken: cancellationToken);
            var mapped = mapper.MapAsync(updates, cancellationToken);

            await Response.WritePartsAsync(mapped, cancellationToken);
        }

        return new EmptyResult();
    }
}

