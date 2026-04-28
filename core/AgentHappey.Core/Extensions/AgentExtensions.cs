using System.Collections;
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
        IReadOnlyCollection<LoadedAgentSkill>? skills = null,
        IDictionary<string, Implementation>? mcpImplementations = null,
        IDictionary<string, string>? mcpInstructions = null,
        IDictionary<string, IEnumerable<object>>? mcpResources = null,
        IDictionary<string, IEnumerable<object>>? mcpResourceTemplates = null)
    {
        var sections = new List<string>();

        if (mcpImplementations is { Count: > 0 })
        {
            sections.AddRange(BuildMcpServerInstructionBlocks(
                mcpImplementations,
                mcpInstructions,
                mcpResources,
                mcpResourceTemplates));
        }

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

        if (!string.IsNullOrWhiteSpace(agent.Instructions))
            sections.Add(agent.Instructions.Trim());

        return string.Join("\n\n", sections.Where(section => !string.IsNullOrWhiteSpace(section)));
    }

    public static IEnumerable<string> BuildMcpServerInstructionBlocks(
        IDictionary<string, Implementation> implementations,
        IDictionary<string, string>? instructions = null,
        IDictionary<string, IEnumerable<object>>? resources = null,
        IDictionary<string, IEnumerable<object>>? templates = null)
    {
        foreach (var kv in implementations.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            var url = kv.Key;
            var server = kv.Value;

            string? instructionValue = null;
            IEnumerable<object> resourceList = [];
            IEnumerable<object> templateList = [];

            if (instructions is not null)
                instructions.TryGetValue(url, out instructionValue);

            if (resources is not null && resources.TryGetValue(url, out var r))
                resourceList = r ?? [];

            if (templates is not null && templates.TryGetValue(url, out var t))
                templateList = t ?? [];

            var serverBlock = new Dictionary<string, object?>
            {
                ["name"] = server.Name,
                ["version"] = server.Version,
                ["mcpServerUrl"] = url,
            };

            AddIfNotNull(serverBlock, "title", server.Title);
            AddIfNotNull(serverBlock, "websiteUrl", server.WebsiteUrl);

            var block = new Dictionary<string, object?>
            {
                ["modelContextProtocolServer"] = serverBlock,
            };

            var assistantResources = (resourceList ?? [])
                .Where(IsForAssistant)
                .Select(BuildMcpResourceObject)
                .Where(item => item.Count > 0)
                .ToList();
            if (assistantResources.Count > 0)
                block["resources"] = assistantResources;

            var assistantTemplates = (templateList ?? [])
                .Where(IsForAssistant)
                .Select(BuildMcpResourceTemplateObject)
                .Where(item => item.Count > 0)
                .ToList();
            if (assistantTemplates.Count > 0)
                block["resourceTemplates"] = assistantTemplates;

            var trimmedInstructions = instructionValue?.Trim();
            if (!string.IsNullOrWhiteSpace(trimmedInstructions))
                block["instructions"] = trimmedInstructions;

            yield return JsonSerializer.Serialize(block, JsonSerializerOptions.Web);
        }
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
       IDictionary<string, IEnumerable<object>> resources,
       IDictionary<string, IEnumerable<object>> templates)
    {
        foreach (var block in BuildMcpServerInstructionBlocks(implementations, instructions, resources, templates))
            yield return block.ToTextUIPart();
    }

    private static Dictionary<string, object?> BuildMcpResourceObject(object resource)
    {
        var item = new Dictionary<string, object?>();

        AddIfNotNull(item, "name", GetStringProperty(resource, "Name"));
        AddIfNotNull(item, "uri", GetStringProperty(resource, "Uri"));
        AddIfNotNull(item, "description", GetStringProperty(resource, "Description"));
        AddIfNotNull(item, "mimeType", GetStringProperty(resource, "MimeType"));

        var size = GetLongProperty(resource, "Size");
        if (size is not null)
            item["size"] = size.Value;

        var annotations = BuildAnnotations(resource, ["Priority", "LastModified"]);
        if (annotations.Count > 0)
            item["annotations"] = annotations;

        return item;
    }

    private static Dictionary<string, object?> BuildMcpResourceTemplateObject(object template)
    {
        var item = new Dictionary<string, object?>();

        AddIfNotNull(item, "name", GetStringProperty(template, "Name"));
        AddIfNotNull(item, "uriTemplate", GetStringProperty(template, "UriTemplate"));
        AddIfNotNull(item, "description", GetStringProperty(template, "Description"));
        AddIfNotNull(item, "mimeType", GetStringProperty(template, "MimeType"));

        var annotations = BuildAnnotations(template, ["Priority"]);
        if (annotations.Count > 0)
            item["annotations"] = annotations;

        return item;
    }

    private static Dictionary<string, object?> BuildAnnotations(object source, IEnumerable<string> keys)
    {
        var annotations = GetPropertyValue(source, "Annotations");
        if (annotations is null)
            return [];

        var result = new Dictionary<string, object?>();
        foreach (var key in keys)
        {
            var value = GetPropertyValue(annotations, key);
            if (value is null)
                continue;

            var jsonKey = char.ToLowerInvariant(key[0]) + key[1..];
            result[jsonKey] = NormalizeValue(value);
        }

        return result;
    }

    private static bool IsForAssistant(object source)
    {
        var annotations = GetPropertyValue(source, "Annotations");
        var audience = GetPropertyValue(annotations, "Audience");
        if (audience is null)
            return true;

        if (audience is string single)
            return string.Equals(single, "assistant", StringComparison.OrdinalIgnoreCase);

        if (audience is IEnumerable sequence)
        {
            var entries = sequence
                .Cast<object?>()
                .Where(entry => entry is not null)
                .Select(entry => entry is string value ? value : entry!.ToString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();

            return entries.Count == 0 || entries.Any(value => string.Equals(value, "assistant", StringComparison.OrdinalIgnoreCase));
        }

        return string.Equals(audience.ToString(), "assistant", StringComparison.OrdinalIgnoreCase);
    }

    private static object? GetPropertyValue(object? instance, string propertyName)
        => instance?
            .GetType()
            .GetProperty(propertyName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.IgnoreCase)
            ?.GetValue(instance);

    private static string? GetStringProperty(object source, string propertyName)
    {
        var value = GetPropertyValue(source, propertyName);
        return value switch
        {
            null => null,
            string text => text,
            Uri uri => uri.ToString(),
            _ => value.ToString()
        };
    }

    private static long? GetLongProperty(object source, string propertyName)
    {
        var value = GetPropertyValue(source, propertyName);
        return value switch
        {
            null => null,
            byte number => number,
            short number => number,
            int number => number,
            long number => number,
            uint number => number,
            ulong number when number <= long.MaxValue => (long)number,
            _ when long.TryParse(value.ToString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static object? NormalizeValue(object value)
        => value switch
        {
            Uri uri => uri.ToString(),
            Enum enumValue => enumValue.ToString(),
            _ => value
        };

    private static void AddIfNotNull(IDictionary<string, object?> target, string key, object? value)
    {
        if (value is null)
            return;

        if (value is string text && string.IsNullOrWhiteSpace(text))
            return;

        target[key] = value;
    }
}
