using AgentHappey.Core.MCP.Tools;

namespace AgentHappey.Core.MCP;

public static class ModelContextServers
{
   public static readonly Dictionary<string, bool> Servers = new()
   {
      {"DefaultAgents", false},
      {"Runtime",false},
      {"Editor", true},
      {"SharePointRuntime", true}
   };

   public static readonly Dictionary<string, Type[]> ToolTypes = new(StringComparer.OrdinalIgnoreCase)
   {
      ["DefaultAgents"] = [typeof(AgentTools)],
      ["Runtime"] = [typeof(RuntimeTools)],
      ["Editor"] = [typeof(AgentEditorTools)],
      ["SharePointRuntime"] = [typeof(SharePointRuntimeTools)],
   };

   public static readonly Dictionary<string, string> Titles = new(StringComparer.OrdinalIgnoreCase)
   {
      ["DefaultAgents"] = "Default Agents",
      ["Runtime"] = "Agent Framework Runtime",
      ["Editor"] = "Agent Framework Editor",
      ["SharePointRuntime"] = "Agent Framework SharePoint Runtime"
   };

   public static readonly Dictionary<string, string> Descriptions = new(StringComparer.OrdinalIgnoreCase)
   {
      ["DefaultAgents"] = "Discover Agent Framework models and retrieve their complete agent definitions.",
      ["Runtime"] = "Run Agent Framework agents.",
      ["Editor"] = "Create and edit Agents on SharePoint and OneDrive.",
      ["SharePointRuntime"] = "Run Agent Framework agents from SharePoint files."
   };
}
