using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentHappey.Core.MCP;

internal static class AgentProgressStreaming
{
    public static async Task<AgentResponse> RunAgentAsync(
        IAsyncEnumerable<AgentResponseUpdate> updates,
        RequestContext<CallToolRequestParams> requestContext,
        CancellationToken cancellationToken)
    {
        var progress = new ProgressWriter(requestContext);

        async IAsyncEnumerable<AgentResponseUpdate> Observe()
        {
            await foreach (var update in updates.WithCancellation(cancellationToken))
            {
                await progress.WriteAsync(update, cancellationToken);
                yield return update;
            }
        }

        return await Observe().ToAgentResponseAsync(cancellationToken);
    }

    public static async Task<List<WorkflowEvent>> RunWorkflowAsync(
        IAsyncEnumerable<WorkflowEvent> events,
        RequestContext<CallToolRequestParams> requestContext,
        CancellationToken cancellationToken)
    {
        var progress = new ProgressWriter(requestContext);
        List<WorkflowEvent> result = [];

        await foreach (var workflowEvent in events.WithCancellation(cancellationToken))
        {
            result.Add(workflowEvent);

            if (workflowEvent is AgentResponseUpdateEvent updateEvent)
                await progress.WriteAsync(updateEvent.Update, cancellationToken);
        }

        return result;
    }

    private sealed class ProgressWriter(RequestContext<CallToolRequestParams> requestContext)
    {
        private readonly Dictionary<string, StringBuilder> _buffers = new(StringComparer.Ordinal);
        private int _progress = 1;

        public async Task WriteAsync(AgentResponseUpdate update, CancellationToken cancellationToken)
        {
            if (requestContext.Params?.ProgressToken is null)
                return;

            var messageId = string.IsNullOrWhiteSpace(update.MessageId)
                ? update.ResponseId ?? "response"
                : update.MessageId;

            foreach (var content in update.Contents)
            {
                var item = content switch
                {
                    TextContent text when !string.IsNullOrEmpty(text.Text)
                        => (Type: "text", Delta: text.Text),
                    TextReasoningContent reasoning when !string.IsNullOrEmpty(reasoning.Text)
                        => (Type: "reasoning", Delta: reasoning.Text),
                    _ => default
                };

                if (item.Delta is null)
                    continue;

                var key = $"{messageId}:{item.Type}";
                if (!_buffers.TryGetValue(key, out var buffer))
                {
                    buffer = new StringBuilder();
                    _buffers[key] = buffer;
                }

                buffer.Append(item.Delta);
                await SendAsync(buffer.ToString(), cancellationToken);
            }
        }

        private Task SendAsync(string message, CancellationToken cancellationToken)
        {
            var progressToken = requestContext.Params?.ProgressToken;
            if (progressToken is null)
                return Task.CompletedTask;

            return requestContext.Server.SendNotificationAsync(
                "notifications/progress",
                new ProgressNotificationParams
                {
                    ProgressToken = progressToken.Value,
                    Progress = new ProgressNotificationValue
                    {
                        Progress = _progress++,
                        Message = message
                    }
                },
                cancellationToken: cancellationToken);
        }
    }
}
