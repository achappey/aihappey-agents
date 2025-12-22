using System.Text.Json.Nodes;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Declarative;
using Microsoft.Extensions.AI;

namespace AgentHappey.Core.Extensions;

public static class WorkflowExtensions
{
    public static Workflow ParseWorkflow<TInput>(this string yamlText, WorkflowAgentProvider workflowAgentProvider)
    {
        using var reader = new StringReader(yamlText);
        var options = new DeclarativeWorkflowOptions(workflowAgentProvider);

        return DeclarativeWorkflowBuilder.Build<string>(reader, options, input => new ChatMessage(ChatRole.User, input));
    }



    public static Workflow BuildHandoffWorkflow(
          this IEnumerable<AIAgent> agents,
          JsonArray? handoffsJson)
    {
        if (handoffsJson is null || handoffsJson.Count == 0)
            throw new InvalidOperationException("Handoff workflow selected but no handoffs defined.");

        var agentByName = agents.ToDictionary(a => a.Name!, a => a, StringComparer.OrdinalIgnoreCase);
        var firstAgent = agents.First();
        var builder = AgentWorkflowBuilder.CreateHandoffBuilderWith(firstAgent);

        foreach (var item in handoffsJson)
        {
            if (item is not JsonArray pair || pair.Count != 2)
                throw new InvalidOperationException("Each handoff must be an array with exactly 2 items.");

            var leftRaw = pair[0];
            var rightRaw = pair[1];

            if (leftRaw == null || rightRaw == null)
                throw new InvalidOperationException("Invalid handoff item.");

            // Parse left and right sides
            var left = ParseSide(leftRaw, agentByName);
            var right = ParseSide(rightRaw, agentByName);

            // Enforce ONLY ONE SIDE as array
            bool leftIsArray = left is AIAgent[];
            bool rightIsArray = right is AIAgent[];

            if (leftIsArray && rightIsArray)
                throw new InvalidOperationException("Invalid handoff: both sides cannot be arrays.");

            if (!leftIsArray && !rightIsArray)
                throw new InvalidOperationException("Invalid handoff: one side must be an array.");

            // Route to correct overload
            builder = (left, right) switch
            {
                (AIAgent fromSingle, AIAgent[] toMany) =>
                    builder.WithHandoffs(fromSingle, toMany),

                (AIAgent[] fromMany, AIAgent toSingle) =>
                    builder.WithHandoffs(fromMany, toSingle),

                _ => throw new InvalidOperationException("Invalid handoff format.")
            };
        }
        return builder.Build();
    }

    private static object ParseSide(JsonNode raw, Dictionary<string, AIAgent> agentByName)
    {
        if (raw is JsonValue)
        {
            var name = raw.GetValue<string>();
            return agentByName.TryGetValue(name, out var agent)
                ? agent
                : throw new InvalidOperationException($"Agent '{name}' not found.");
        }

        if (raw is JsonArray arr)
        {
            return arr
                .Select(v => v!.GetValue<string>())
                .Select(name =>
                    agentByName.TryGetValue(name, out var agent)
                        ? agent
                        : throw new InvalidOperationException($"Agent '{name}' not found.")
                )
                .ToArray();
        }

        throw new InvalidOperationException("Invalid handoff structure.");
    }


}
