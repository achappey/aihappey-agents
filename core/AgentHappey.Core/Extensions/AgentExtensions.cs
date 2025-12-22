using System.Text.Json;
using AgentHappey.Common.Extensions;
using AgentHappey.Common.Models;
using AIHappey.Common.Model;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;

namespace AgentHappey.Core.Extensions;

public static class AgentExtensions
{
 
    public static IEnumerable<AIHappey.Common.Model.Tool> ToTools(
            this IEnumerable<AITool> tools) => tools?
            .OfType<AIFunctionDeclaration>()
            .Select(a => new AIHappey.Common.Model.Tool()
            {
                Name = a.Name,
                Description = a.Description,
                InputSchema = JsonSerializer
                    .Deserialize<ToolInputSchema>(a.JsonSchema.ToString())
            }) ?? [];

    public static Implementation ToImplementation(
         this Agent agent) => new()
         {
             Name = agent.Name,
             Title = agent.Name,
             Description = agent.Description,
             Version = "1.0.0"
         };

    public static UIMessage GetSystemMessage(
        this Agent agent,
        IEnumerable<UIMessagePart>? mcpParts = null,
        string? tenantId = null)
    {
        var parts = new List<UIMessagePart>
        {
            $"You are {agent.Name}. {agent.Description}".ToTextUIPart(),

            // Instructions
            agent.Instructions.ToTextUIPart(),

            // System info (timestamp + tenant)
            JsonSerializer.Serialize(
                new AgentSystemInfo
                {
                    UtcNow = DateTime.UtcNow,
                    TenantId = tenantId
                },
                JsonSerializerOptions.Web)
            .WithHeader("# SystemInfo")
            .ToTextUIPart()
        };

        // Optional MCP server parts
        if (mcpParts is not null)
            parts.AddRange(mcpParts);

        return new UIMessage
        {
            Role = AIHappey.Common.Model.Role.system,
            Id = Guid.NewGuid().ToString(),
            Parts = parts
        };
    }


    public static IEnumerable<UIMessagePart> ToMcpServerParts(
       IDictionary<string, Implementation> implementations,
       IDictionary<string, string> instructions,
       IDictionary<string, IEnumerable<ModelContextProtocol.Client.McpClientResource>> resources,
       IDictionary<string, IEnumerable<ModelContextProtocol.Client.McpClientResourceTemplate>> templates)
    {
        foreach (var kv in implementations)
        {
            var key = kv.Key;
            var server = kv.Value;

            instructions.TryGetValue(key, out var instructionValue);
            resources.TryGetValue(key, out var resourceList);
            templates.TryGetValue(key, out var templateList);

            var obj = new
            {
                McpServerUrl = key,
                server.Name,
                server.Description,
                server.WebsiteUrl,
                instructions = instructionValue,
                resources = resourceList?.Select(y => new
                {
                    y.Uri,
                    y.Name,
                    y.MimeType,
                    y.Description
                }),
                resourceTemplates = templateList?.Select(y => new
                {
                    y.UriTemplate,
                    y.Name,
                    y.MimeType,
                    y.Description
                })
            };

            yield return JsonSerializer
                .Serialize(obj, JsonSerializerOptions.Web)
                .ToTextUIPart();
        }
    }
}
