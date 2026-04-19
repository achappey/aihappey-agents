using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentHappey.Common.Models;

namespace AgentHappey.Core.Skills;

public sealed class LoadedAgentSkill(
    string skillId,
    string name,
    string description,
    string body,
    string rootDirectoryName,
    IReadOnlyDictionary<string, LoadedAgentSkillResource> resources)
{
    public string SkillId { get; } = skillId;

    public string Name { get; } = name;

    public string Description { get; } = description;

    public string Body { get; } = body;

    public string RootDirectoryName { get; } = rootDirectoryName;

    public IReadOnlyDictionary<string, LoadedAgentSkillResource> Resources { get; } = resources;

    public IReadOnlyList<string> ResourcePaths { get; } = resources.Keys.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
}

public sealed class LoadedAgentSkillResource(
    string path,
    byte[] bytes,
    string mimeType,
    bool isText)
{
    public string Path { get; } = path;

    public byte[] Bytes { get; } = bytes;

    public string MimeType { get; } = mimeType;

    public bool IsText { get; } = isText;

    public string ReadText() => Encoding.UTF8.GetString(Bytes);
}

internal sealed class ParsedSkillMarkdown(
    string skillId,
    string description,
    string body)
{
    public string SkillId { get; } = skillId;

    public string Description { get; } = description;

    public string Body { get; } = body;
}

public static class AgentSkillCatalog
{
    private static readonly Regex SkillNameRegex = new("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<LoadedAgentSkill> Load(IEnumerable<AISkill>? skills)
        => (skills ?? []).Select(Load).ToArray();

    public static LoadedAgentSkill Load(AISkill skill)
    {
        ArgumentNullException.ThrowIfNull(skill);

        if (!string.Equals(skill.Type, "inline", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Skill '{skill.Name}' uses unsupported type '{skill.Type}'. Only inline skills are currently supported.");

        if (skill.Source is null)
            throw new InvalidOperationException($"Skill '{skill.Name}' is missing its source payload.");

        if (!string.Equals(skill.Source.Type, "base64", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Skill '{skill.Name}' uses unsupported source type '{skill.Source.Type}'. Only base64 sources are currently supported.");

        if (!string.Equals(skill.Source.MediaType, "application/zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Skill '{skill.Name}' uses unsupported media type '{skill.Source.MediaType}'. Expected application/zip.");

        byte[] zipBytes;

        try
        {
            zipBytes = Convert.FromBase64String(skill.Source.Data);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"Skill '{skill.Name}' contains invalid base64 data.", ex);
        }

        using var archiveStream = new MemoryStream(zipBytes, writable: false);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false);

        var fileEntries = archive.Entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
            .Select(entry => new
            {
                Entry = entry,
                Path = NormalizeArchivePath(entry.FullName)
            })
            .ToArray();

        if (fileEntries.Length == 0)
            throw new InvalidOperationException($"Skill '{skill.Name}' zip bundle is empty.");

        var rootDirectories = fileEntries
            .Select(entry => entry.Path.Split('/', 2, StringSplitOptions.RemoveEmptyEntries)[0])
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (rootDirectories.Length != 1)
            throw new InvalidOperationException($"Skill '{skill.Name}' zip bundle must contain exactly one root folder.");

        var rootDirectory = rootDirectories[0];
        var skillMarkdownEntry = fileEntries.FirstOrDefault(entry => string.Equals(entry.Path, $"{rootDirectory}/SKILL.md", StringComparison.Ordinal));

        if (skillMarkdownEntry is null)
            throw new InvalidOperationException($"Skill '{skill.Name}' zip bundle must contain '{rootDirectory}/SKILL.md'.");

        var markdown = ReadAllText(skillMarkdownEntry.Entry);
        var parsedMarkdown = ParseSkillMarkdown(markdown);

        ValidateSkillName(parsedMarkdown.SkillId, rootDirectory);

        var resources = new Dictionary<string, LoadedAgentSkillResource>(StringComparer.OrdinalIgnoreCase);
        foreach (var fileEntry in fileEntries)
        {
            var relativePath = fileEntry.Path[(rootDirectory.Length + 1)..];
            if (string.Equals(relativePath, "SKILL.md", StringComparison.Ordinal))
                continue;

            using var entryStream = fileEntry.Entry.Open();
            using var ms = new MemoryStream();
            entryStream.CopyTo(ms);

            resources[relativePath] = new LoadedAgentSkillResource(
                relativePath,
                ms.ToArray(),
                GetMimeType(relativePath),
                IsTextResource(relativePath));
        }

        var displayName = string.IsNullOrWhiteSpace(skill.Name) ? parsedMarkdown.SkillId : skill.Name.Trim();
        var description = string.IsNullOrWhiteSpace(parsedMarkdown.Description)
            ? skill.Description?.Trim() ?? string.Empty
            : parsedMarkdown.Description.Trim();

        return new LoadedAgentSkill(
            parsedMarkdown.SkillId,
            displayName,
            description,
            parsedMarkdown.Body,
            rootDirectory,
            resources);
    }

    public static string NormalizeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var parts = new List<string>();
        foreach (var segment in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (segment == ".")
                continue;

            if (segment == "..")
                throw new InvalidOperationException("Skill resource paths must stay inside the skill directory.");

            parts.Add(segment);
        }

        return string.Join('/', parts);
    }

    private static string NormalizeArchivePath(string path)
    {
        var normalized = NormalizeRelativePath(path);
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidOperationException("Skill zip bundle contains an empty path entry.");

        return normalized;
    }

    private static string ReadAllText(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static ParsedSkillMarkdown ParseSkillMarkdown(string markdown)
    {
        using var reader = new StringReader(markdown ?? string.Empty);
        var firstLine = reader.ReadLine();
        if (!string.Equals(firstLine, "---", StringComparison.Ordinal))
            throw new InvalidOperationException("SKILL.md must start with YAML frontmatter delimited by ---.");

        var frontmatterLines = new List<string>();
        string? line;
        var closed = false;

        while ((line = reader.ReadLine()) is not null)
        {
            if (string.Equals(line, "---", StringComparison.Ordinal))
            {
                closed = true;
                break;
            }

            frontmatterLines.Add(line);
        }

        if (!closed)
            throw new InvalidOperationException("SKILL.md frontmatter is missing its closing --- delimiter.");

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? currentKey = null;

        foreach (var frontmatterLine in frontmatterLines)
        {
            if (string.IsNullOrWhiteSpace(frontmatterLine) || frontmatterLine.TrimStart().StartsWith('#'))
                continue;

            if ((frontmatterLine.StartsWith(' ') || frontmatterLine.StartsWith('\t')) && currentKey is not null)
            {
                values[currentKey] = string.Join('\n', values[currentKey], frontmatterLine.Trim());
                continue;
            }

            var separatorIndex = frontmatterLine.IndexOf(':');
            if (separatorIndex <= 0)
                continue;

            currentKey = frontmatterLine[..separatorIndex].Trim();
            var value = frontmatterLine[(separatorIndex + 1)..].Trim();
            values[currentKey] = TrimYamlScalar(value);
        }

        if (!values.TryGetValue("name", out var skillId) || string.IsNullOrWhiteSpace(skillId))
            throw new InvalidOperationException("SKILL.md frontmatter must contain a non-empty 'name' field.");

        if (!values.TryGetValue("description", out var description) || string.IsNullOrWhiteSpace(description))
            throw new InvalidOperationException("SKILL.md frontmatter must contain a non-empty 'description' field.");

        var body = reader.ReadToEnd().Trim();

        return new ParsedSkillMarkdown(skillId.Trim(), description.Trim(), body);
    }

    private static string TrimYamlScalar(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var trimmed = value.Trim();
        if (trimmed.Length >= 2)
        {
            if ((trimmed.StartsWith('"') && trimmed.EndsWith('"')) || (trimmed.StartsWith('\'') && trimmed.EndsWith('\'')))
                return trimmed[1..^1];
        }

        return trimmed;
    }

    private static void ValidateSkillName(string skillId, string rootDirectory)
    {
        if (skillId.Length is < 1 or > 64)
            throw new InvalidOperationException($"Skill '{skillId}' has an invalid name length. Expected 1-64 characters.");

        if (!SkillNameRegex.IsMatch(skillId))
            throw new InvalidOperationException($"Skill '{skillId}' has an invalid name. Use lowercase letters, numbers, and hyphens only.");

        if (!string.Equals(skillId, rootDirectory, StringComparison.Ordinal))
            throw new InvalidOperationException($"Skill '{skillId}' must be packaged in a root folder with the same name.");
    }

    private static bool IsTextResource(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".yml", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".xml", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".html", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".csv", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".tsv", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".js", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ts", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jsx", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".tsx", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".css", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".scss", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".py", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".sh", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".sql", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".toml", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ini", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".cfg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".env", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".gitignore", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".dockerfile", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetMimeType(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();

        return extension switch
        {
            ".md" => "text/markdown",
            ".txt" => "text/plain",
            ".json" => "application/json",
            ".yaml" or ".yml" => "application/yaml",
            ".xml" => "application/xml",
            ".html" or ".htm" => "text/html",
            ".csv" => "text/csv",
            ".tsv" => "text/tab-separated-values",
            ".js" => "text/javascript",
            ".ts" => "text/typescript",
            ".jsx" => "text/jsx",
            ".tsx" => "text/tsx",
            ".css" => "text/css",
            ".scss" => "text/x-scss",
            ".cs" => "text/plain",
            ".py" => "text/x-python",
            ".sh" => "text/x-shellscript",
            ".ps1" => "text/plain",
            ".sql" => "application/sql",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".pdf" => "application/pdf",
            _ => "application/octet-stream"
        };
    }
}
