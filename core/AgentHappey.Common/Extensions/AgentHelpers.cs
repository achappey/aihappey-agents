using AgentHappey.Common.Models;

namespace AgentHappey.Common.Extensions;

public static class AgentHelpers
{
    public static McpServer ToMcpServer(this string url) => new() { Url = url };

    public static object? GetCompletionsOutputSchema(this Agent agent)
    {
        if (agent.OutputSchema == null)
            return null;

        return new Dictionary<string, object?>
        {
            ["type"] = "json_schema",
            ["json_schema"] = agent.GetJsonOutputSchema()?
                .Where(a => a.Key != "type")
                .ToDictionary(a => a.Key, a => a.Value)
        };
    }

    public static string GetOutputName(this Agent agent) => $"{agent.Name.ToLowerInvariant()}_output";

    public static Dictionary<string, object?>? GetJsonOutputSchema(this Agent agent)
    {
        if (agent.OutputSchema == null)
            return null;

        // Extract properties into plain dictionaries
        var props = agent.OutputSchema.Properties
            .ToDictionary(
                p => p.Key,
                p => (object)new Dictionary<string, object?>
                {
                    ["type"] = p.Value.Type,                // assume string
                    ["description"] = p.Value.Description,  // assume string
                }
            );

        // Required list
        var required = agent.OutputSchema.Properties
            .Where(p => p.Value.Required == true)
            .Select(p => p.Key)
            .ToList();

        // Final payload — NOTHING custom, NOTHING typed
        return new Dictionary<string, object?>
        {
            ["type"] = "json_schema",
            ["json_schema"] = new Dictionary<string, object?>
            {
                ["name"] = agent.GetOutputName(),
                ["description"] = agent.Description,
                ["schema"] = new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["properties"] = props,
                    ["required"] = required,
                    ["additionalProperties"] = false
                },
                ["strict"] = true,
            },
        };
    }

    public static IEnumerable<McpServer> ToMcpServers(this IEnumerable<string> urls) => urls.Select(a => a.ToMcpServer());
}
