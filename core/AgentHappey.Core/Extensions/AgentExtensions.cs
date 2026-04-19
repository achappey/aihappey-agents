using System.Text.Json;
using AgentHappey.Common.Extensions;
using AgentHappey.Common.Models;
using AgentHappey.Core.Skills;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;

namespace AgentHappey.Core.Extensions;

public static class AgentExtensions
{
    public static string ComposeInstructions(
        this Agent agent,
        IReadOnlyCollection<LoadedAgentSkill>? skills = null)
    {
        var sections = new List<string>();

        if (!string.IsNullOrWhiteSpace(agent.Instructions))
            sections.Add(agent.Instructions.Trim());

        if (skills is { Count: > 0 })
        {
            sections.Add(JsonSerializer.Serialize(new
            {
                availableSkills = new
                {
                    activationTool = "activate_skill",
                    resourceTool = "read_skill_resource",
                    instructions =
                        "The following skills provide specialized instructions for specific tasks. When a task matches a skill description, call activate_skill with the exact skill_id to load its instructions. After activation, use read_skill_resource with the same skill_id and a relative path when the instructions reference bundled files.",
                    skills = skills.Select(skill => new
                    {
                        skill_id = skill.SkillId,
                        name = skill.Name,
                        description = skill.Description
                    })
                }
            }, JsonSerializerOptions.Web));
        }

        return string.Join("\n\n", sections.Where(section => !string.IsNullOrWhiteSpace(section)));
    }

 
    public static IEnumerable<AIHappey.Vercel.Models.Tool> ToTools(
            this IEnumerable<AITool> tools) => tools?
            .OfType<AIFunctionDeclaration>()
            .Select(a => new AIHappey.Vercel.Models.Tool()
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
            agent.ComposeInstructions().ToTextUIPart(),

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
            Role = AIHappey.Vercel.Models.Role.system,
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
