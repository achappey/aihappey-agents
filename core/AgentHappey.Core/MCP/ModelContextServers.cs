using AgentHappey.Core.MCP.Tools;
using ModelContextProtocol.Protocol;

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
      //  ["List"] = [typeof(AgentTools)],
      ["Runtime"] = [typeof(RuntimeTools)],
      ["Editor"] = [typeof(AgentEditorTools)],
      ["SharePointRuntime"] = [typeof(SharePointRuntimeTools)],
   };

   public static readonly Dictionary<string, ListResourcesResult> Resources = new(StringComparer.OrdinalIgnoreCase)
   {
      ["DefaultAgents"] = new ListResourcesResult()
      {
         Resources = [new() {
             Name = "Agents",
             Uri = "agents://list",
             Description = "List all default agents",
             MimeType = "application/vnd.agents+json",
             Annotations = new() {
                 Audience = [
                     Role.Assistant,
                     Role.User
                 ]
             }
         }]
      }
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
      ["DefaultAgents"] = "List default Agent Framework agents.",
      ["Runtime"] = "Run Agent Framework agents.",
      ["Editor"] = "Create and edit Agents on SharePoint and OneDrive.",
      ["SharePointRuntime"] = "Run Agent Framework agents from SharePoint files."
   };
}
