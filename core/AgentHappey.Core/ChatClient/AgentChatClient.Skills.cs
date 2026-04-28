using System.ComponentModel;
using System.Text.Json;
using AgentHappey.Common.Extensions;
using AgentHappey.Core.Extensions;
using AgentHappey.Core.Skills;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;

namespace AgentHappey.Core.ChatClient;

public partial class AgentChatClient
{
    private IReadOnlyList<LoadedAgentSkill>? loadedSkills;

    public string GetComposedInstructions() => agent.ComposeInstructions(
        skills: GetEnabledSkills(),
        mcpImplementations: McpServerImplementations,
        mcpInstructions: McpServerInstructions,
        mcpResources: McpServerResources,
        mcpResourceTemplates: McpServerResourceTemplates);

    private IReadOnlyList<LoadedAgentSkill> GetEnabledSkills()
        => loadedSkills ??= AgentSkillCatalog.Load(agent.Skills);

    private LoadedAgentSkill ResolveEnabledSkill(string skillId)
    {
        if (string.IsNullOrWhiteSpace(skillId))
            throw new InvalidOperationException("Missing skill_id.");

        var skill = GetEnabledSkills().FirstOrDefault(item => string.Equals(item.SkillId, skillId, StringComparison.Ordinal));
        if (skill is null)
        {
            var enabled = string.Join(", ", GetEnabledSkills().Select(item => item.SkillId));
            throw new InvalidOperationException($"Skill '{skillId}' is not enabled. Enabled skills: {enabled}.");
        }

        return skill;
    }

    [DisplayName("activate_skill")]
    [Description("Loads the body instructions for an enabled agent skill. Use this when one of the available skills matches the current task. After activation, use read_skill_resource to load referenced bundled files by relative path.")]
    private Task<CallToolResult> ActivateSkillAsync(
        [Description("Exact enabled skill id to activate.")]
        string skill_id,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var skill = ResolveEnabledSkill(skill_id);
        var resourcePaths = skill.ResourcePaths;
        var resourcesXml = resourcePaths.Count > 0
            ? string.Join("\n", [
                "<skill_resources>",
                .. resourcePaths.Select(path => $"  <file>{path}</file>"),
                "</skill_resources>"
            ])
            : "<skill_resources />";

        return Task.FromResult(new CallToolResult
        {
            IsError = false,
            StructuredContent = JsonSerializer.SerializeToElement(new
            {
                skill = new
                {
                    skill_id = skill.SkillId,
                    name = skill.Name,
                    description = skill.Description,
                    resourcePaths,
                    instructions = skill.Body
                }
            }, JsonSerializerOptions.Web),
            Content =
            [
                string.Join("\n", [
                    $"<skill_content skill_id=\"{EscapeAttribute(skill.SkillId)}\" name=\"{EscapeAttribute(skill.Name)}\">",
                    skill.Body,
                    string.Empty,
                    "Use read_skill_resource with this skill_id and a relative path from the resource list when you need bundled files referenced by the instructions.",
                    resourcesXml,
                    "</skill_content>"
                ]).ToContentBlock()
            ]
        });
    }

    [DisplayName("read_skill_resource")]
    [Description("Reads a bundled file from an enabled skill by relative path. Use this after activate_skill when the skill instructions reference scripts, references, or assets. Paths are relative to the skill root.")]
    private Task<CallToolResult> ReadSkillResourceAsync(
        [Description("Exact enabled skill id that owns the resource.")]
        string skill_id,
        [Description("Relative path within the skill directory, for example references/REFERENCE.md or scripts/run.py.")]
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var skill = ResolveEnabledSkill(skill_id);
        var relativePath = AgentSkillCatalog.NormalizeRelativePath(path);
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new InvalidOperationException("Missing path. Provide a relative path inside the skill directory.");

        if (!skill.Resources.TryGetValue(relativePath, out var resource))
            throw new InvalidOperationException($"Resource '{relativePath}' was not found in skill '{skill.SkillId}'.");

        if (resource.IsText)
        {
            var text = resource.ReadText();
            return Task.FromResult(new CallToolResult
            {
                IsError = false,
                StructuredContent = JsonSerializer.SerializeToElement(new
                {
                    skillResource = new
                    {
                        skill_id = skill.SkillId,
                        skillName = skill.Name,
                        path = relativePath,
                        mimeType = resource.MimeType,
                        text
                    }
                }, JsonSerializerOptions.Web),
                Content =
                [
                    string.Join("\n", [
                        $"<skill_resource skill_id=\"{EscapeAttribute(skill.SkillId)}\" name=\"{EscapeAttribute(skill.Name)}\" path=\"{EscapeAttribute(relativePath)}\" mimeType=\"{EscapeAttribute(resource.MimeType)}\">",
                        text,
                        "</skill_resource>"
                    ]).ToContentBlock()
                ]
            });
        }

        var base64 = Convert.ToBase64String(resource.Bytes);
        return Task.FromResult(new CallToolResult
        {
            IsError = false,
            StructuredContent = JsonSerializer.SerializeToElement(new
            {
                skillResource = new
                {
                    skill_id = skill.SkillId,
                    skillName = skill.Name,
                    path = relativePath,
                    mimeType = resource.MimeType,
                    encoding = "base64",
                    data = base64
                }
            }, JsonSerializerOptions.Web),
            Content =
            [
                $"Binary skill resource {relativePath} from skill {skill.Name}. mimeType={resource.MimeType}. Base64 payload is available in structuredContent.skillResource.data.".ToContentBlock()
            ]
        });
    }

    private static string EscapeAttribute(string value)
        => string.IsNullOrEmpty(value)
            ? string.Empty
            : value
                .Replace("&", "&amp;", StringComparison.Ordinal)
                .Replace("\"", "&quot;", StringComparison.Ordinal)
                .Replace("<", "&lt;", StringComparison.Ordinal)
                .Replace(">", "&gt;", StringComparison.Ordinal);
}
