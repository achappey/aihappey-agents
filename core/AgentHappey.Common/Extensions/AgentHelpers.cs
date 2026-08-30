using AgentHappey.Common.Models;

namespace AgentHappey.Common.Extensions;

public static class AgentHelpers
{
    public static McpServer ToMcpServer(this string url) => new() { Url = url };

    public static string ToDataUri(this string base64, string mimeType)
    {
        if (string.IsNullOrWhiteSpace(base64))
            return string.Empty;

        return $"data:{mimeType};base64,{base64}";
    }

    public static object? GetResponsesText(this Agent agent)
    {
        var jsonSchema = agent.ResponseFormat?.JsonSchema;
        if (jsonSchema is null)
            return null;

        return new
        {
            format = new
            {
                type = "json_schema",
                name = jsonSchema.Name,
                description = jsonSchema.Description,
                schema = jsonSchema.Schema,
                strict = jsonSchema.Strict
            }
        };
    }

    public static string GetOutputName(this Agent agent)
        => agent.ResponseFormat?.JsonSchema.Name ?? $"{agent.Name.ToLowerInvariant()}_output";

    public static IEnumerable<McpServer> ToMcpServers(this IEnumerable<string> urls) => urls.Select(a => a.ToMcpServer());
}
