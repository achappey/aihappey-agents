using System.Text.Json;
using AgentHappey.Common.Extensions;
using AgentHappey.Common.Models;

namespace AgentHappey.Agents.JSON;

public static class StaticContentLoader
{
    public static IEnumerable<Agent> GetAgents(
        this string basePath,
        string? mcpBaseUrl,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var agents = new List<Agent>();

        foreach (var file in EnumerateAgentFiles(basePath, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var agentObj = LoadAgent(file, mcpBaseUrl);
            if (agentObj == null)
                continue;

            agents.Add(agentObj);
        }

        return agents;
    }

    public static IEnumerable<Model> GetModels(
        this string basePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var file in EnumerateAgentFiles(basePath, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var jsonContent = File.ReadAllText(file);
            var agentObj = JsonSerializer.Deserialize<Agent>(jsonContent, JsonSerializerOptions.Web);
            if (agentObj == null || string.IsNullOrWhiteSpace(agentObj.Name))
                continue;

            yield return new Model
            {
                Id = agentObj.Name,
                OwnedBy = agentObj.OwnedBy?.Trim() ?? "",
                Agent = agentObj,
                Description = agentObj.Description,
                Tags = agentObj.Tags,
                Created = File.GetLastWriteTimeUtc(file).ToUnixTimeSecondsSafe()
            };
        }
    }

    private static IEnumerable<string> EnumerateAgentFiles(string basePath, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(basePath))
            return [];

        var files = new List<string>();
        foreach (var file in Directory.EnumerateFiles(basePath, "*Agent.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            files.Add(file);
        }

        return files.OrderBy(file => file, StringComparer.OrdinalIgnoreCase);
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
