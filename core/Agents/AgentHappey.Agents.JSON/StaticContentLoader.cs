using System.Text.Json;
using AgentHappey.Common.Extensions;
using AgentHappey.Common.Models;

namespace AgentHappey.Agents.JSON;

public static class StaticContentLoader
{
    public static IEnumerable<Agent> GetAgents(this string basePath, string mcpBaseUrl)
    {
        var agents = new List<Agent>();

        foreach (var subDir in Directory.GetDirectories(basePath, "*", SearchOption.AllDirectories))
        {
            var serverJsonFiles = Directory.GetFiles(subDir, "*Agent.json", SearchOption.TopDirectoryOnly);
            if (serverJsonFiles.Length == 0)
                continue;

            foreach (var file in serverJsonFiles)
            {
                var jsonContent = File.ReadAllText(file);

                var agentObj = JsonSerializer.Deserialize<Agent>(jsonContent, JsonSerializerOptions.Web);
                if (agentObj == null)
                    continue;


                var original = agentObj.McpServers;
                if (original == null)
                {
                    agents.Add(agentObj);
                    continue;
                }

                var rewritten = new Dictionary<string, McpServer>();

                foreach (var server in original.Values.ToList())
                {
                    // normalize URL first
                    if (server.Url.StartsWith('/'))
                        server.Url = $"{mcpBaseUrl}{server.Url}";

                    var key = server.Url.ToReverseDnsKey();

                    rewritten[key] = server; // last write wins by design
                }

                agentObj.McpServers = rewritten;
                agents.Add(agentObj);
            }
        }

        return agents;
    }


}