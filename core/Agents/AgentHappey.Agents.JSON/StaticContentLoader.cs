using System.Text.Json;
using AgentHappey.Common.Extensions;
using AgentHappey.Common.Models;

namespace AgentHappey.Agents.JSON;

public static class StaticContentLoader
{
    public static IEnumerable<Agent> GetAgents(this string basePath, string? mcpBaseUrl)
    {
        var agents = new List<Agent>();

        foreach (var file in EnumerateAgentFiles(basePath))
        {
            var agentObj = LoadAgent(file, mcpBaseUrl);
            if (agentObj == null)
                continue;

            agents.Add(agentObj);
        }

        return agents;
    }

    public static IEnumerable<Model> GetModels(this string basePath)
    {
        foreach (var file in EnumerateAgentFiles(basePath))
        {
            var jsonContent = File.ReadAllText(file);
            var agentObj = JsonSerializer.Deserialize<Agent>(jsonContent, JsonSerializerOptions.Web);
            if (agentObj == null || string.IsNullOrWhiteSpace(agentObj.Name))
                continue;

            yield return new Model
            {
                Id = agentObj.Name,
                OwnedBy = "agenthappey",
                Created = File.GetLastWriteTimeUtc(file).ToUnixTimeSecondsSafe()
            };
        }
    }

    private static IEnumerable<string> EnumerateAgentFiles(string basePath)
    {
        if (!Directory.Exists(basePath))
            return [];

        return Directory
            .EnumerateFiles(basePath, "*Agent.json", SearchOption.AllDirectories)
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase);
    }

    private static Agent? LoadAgent(string file, string? mcpBaseUrl)
    {
        var jsonContent = File.ReadAllText(file);

        var agentObj = JsonSerializer.Deserialize<Agent>(jsonContent, JsonSerializerOptions.Web);
        if (agentObj == null)
            return null;

        var original = agentObj.McpServers;
        if (original == null)
            return agentObj;

        var rewritten = new Dictionary<string, McpServer>();

        foreach (var server in original.Values.ToList())
        {
            if (server.Url.StartsWith('/') && !string.IsNullOrWhiteSpace(mcpBaseUrl))
                server.Url = $"{mcpBaseUrl}{server.Url}";

            var key = server.Url.ToReverseDnsKey();

            rewritten[key] = server;
        }

        agentObj.McpServers = rewritten;
        return agentObj;
    }

    private static long ToUnixTimeSecondsSafe(this DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        return new DateTimeOffset(utc).ToUnixTimeSeconds();
    }

}
