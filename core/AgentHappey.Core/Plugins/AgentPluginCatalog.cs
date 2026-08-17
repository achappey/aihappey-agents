using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentHappey.Common.Models;
using AgentHappey.Core.Skills;

namespace AgentHappey.Core.Plugins;

public sealed record AgentPluginDiagnostic(
    string Severity,
    string Boundary,
    string Code,
    string Message,
    string? Path = null,
    string? Entry = null);

public sealed class LoadedAgentPluginFile(string path, byte[] bytes, string mimeType, bool isText)
{
    public string Path { get; } = path;
    public byte[] Bytes { get; } = bytes;
    public string MimeType { get; } = mimeType;
    public bool IsText { get; } = isText;
    public string ReadText() => Encoding.UTF8.GetString(Bytes);
}

public sealed class LoadedAgentPlugin(
    string name,
    string description,
    string? version,
    IReadOnlyList<LoadedAgentSkill> skills,
    IReadOnlyDictionary<string, McpServer> mcpServers,
    IReadOnlyDictionary<string, LoadedAgentPluginFile> files,
    IReadOnlyList<AgentPluginDiagnostic> diagnostics)
{
    public string Name { get; } = name;
    public string Description { get; } = description;
    public string? Version { get; } = version;
    public IReadOnlyList<LoadedAgentSkill> Skills { get; } = skills;
    public IReadOnlyDictionary<string, McpServer> McpServers { get; } = mcpServers;
    public IReadOnlyDictionary<string, LoadedAgentPluginFile> Files { get; } = files;
    public IReadOnlyList<AgentPluginDiagnostic> Diagnostics { get; } = diagnostics;
}

public sealed record AgentPluginLoadResult(
    IReadOnlyList<LoadedAgentPlugin> Plugins,
    IReadOnlyList<AgentPluginDiagnostic> Diagnostics);

public static partial class AgentPluginCatalog
{
    public const string PluginSchema = "https://agent-plugins.org/schemas/1.0.0/plugin.schema.json";
    public const string McpSchema = "https://agent-plugins.org/schemas/1.0.0/mcp.schema.json";

    private const long MaximumExpandedBytes = 128L * 1024L * 1024L;
    private const int MaximumEntries = 4096;

    private static readonly HashSet<string> ManifestFields =
    [
        "$schema", "name", "version", "description", "author", "homepage",
        "repository", "license", "keywords", "extensions"
    ];

    private static readonly HashSet<string> McpTopLevelFields = ["$schema", "mcpServers"];
    private static readonly HashSet<string> HttpServerFields = ["type", "url", "headers"];

    [GeneratedRegex("^(?!.*(?:--|\\.\\.))[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?$")]
    private static partial Regex PluginNameRegex();

    [GeneratedRegex("^[!#$%&'*+.^_`|~0-9A-Za-z-]+$")]
    private static partial Regex HeaderNameRegex();

    public static AgentPluginLoadResult Load(
        IEnumerable<AIPluginFile>? pluginFiles,
        string? extensionNamespace = null)
    {
        var plugins = new List<LoadedAgentPlugin>();
        var diagnostics = new List<AgentPluginDiagnostic>();
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var payload in pluginFiles ?? [])
        {
            try
            {
                var loaded = Load(payload, extensionNamespace);
                diagnostics.AddRange(loaded.Diagnostics);
                if (!names.Add(loaded.Name))
                {
                    diagnostics.Add(Error("plugin", "plugin-duplicate-name",
                        $"Duplicate plugin '{loaded.Name}' was skipped."));
                    continue;
                }

                plugins.Add(loaded);
            }
            catch (Exception exception) when (exception is InvalidDataException
                or InvalidOperationException
                or JsonException
                or FormatException)
            {
                diagnostics.Add(Error("plugin", "plugin-invalid", exception.Message));
            }
        }

        return new AgentPluginLoadResult(plugins, diagnostics);
    }

    public static LoadedAgentPlugin Load(AIPluginFile payload, string? extensionNamespace = null)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!string.Equals(payload.Type, "base64", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unsupported plugin source type '{payload.Type}'. Expected base64.");
        if (!string.Equals(payload.MediaType, "application/zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unsupported plugin media type '{payload.MediaType}'. Expected application/zip.");

        var bytes = Convert.FromBase64String(payload.Data);
        using var stream = new MemoryStream(bytes, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count > MaximumEntries)
            throw new InvalidDataException($"Plugin archive contains more than {MaximumEntries} entries.");
        if (archive.Entries.Sum(entry => entry.Length) > MaximumExpandedBytes)
            throw new InvalidDataException("Plugin archive exceeds the expanded size limit.");

        var entries = ReadEntries(archive);
        var root = FindPluginRoot(entries.Keys);
        var package = entries
            .Where(item => item.Key.StartsWith(root, StringComparison.Ordinal))
            .ToDictionary(
                item => NormalizePath(item.Key[root.Length..]),
                item => item.Value,
                StringComparer.Ordinal);

        if (!package.TryGetValue("plugin.json", out var manifestBytes))
            throw new InvalidDataException("plugin.json is required at the plugin root.");

        var diagnostics = new List<AgentPluginDiagnostic>();
        using var manifestDocument = JsonDocument.Parse(manifestBytes);
        var manifest = ValidateManifest(manifestDocument.RootElement, diagnostics);
        var skills = LoadSkills(package, manifest.Name, diagnostics);
        var servers = LoadMcpServers(package, manifest, extensionNamespace, diagnostics);
        var files = package
            .Where(item => item.Key is not "plugin.json" and not "mcp.json"
                && !item.Key.StartsWith("skills/", StringComparison.Ordinal))
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(
                item => item.Key,
                item => new LoadedAgentPluginFile(
                    item.Key,
                    item.Value,
                    GetMimeType(item.Key),
                    IsTextFile(item.Key)),
                StringComparer.Ordinal);

        return new LoadedAgentPlugin(
            manifest.Name,
            manifest.Description ?? string.Empty,
            manifest.Version,
            skills,
            servers,
            files,
            diagnostics);
    }

    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path.Contains('\\')
            || path.Contains('\0')
            || path.StartsWith('/'))
            throw new InvalidDataException($"Unsafe plugin package path '{path}'.");

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(part => part is "." or ".."))
            throw new InvalidDataException($"Unsafe plugin package path '{path}'.");
        return string.Join('/', parts);
    }

    private static Dictionary<string, byte[]> ReadEntries(ZipArchive archive)
    {
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var caseInsensitive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries.Where(entry => !string.IsNullOrWhiteSpace(entry.Name)))
        {
            var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixMode == 0xA000)
                throw new InvalidDataException($"Plugin archive symlink '{entry.FullName}' is not allowed.");

            var path = NormalizePath(entry.FullName);
            if (!caseInsensitive.Add(path))
                throw new InvalidDataException($"Duplicate plugin package path '{path}'.");

            using var input = entry.Open();
            using var output = new MemoryStream();
            input.CopyTo(output);
            entries.Add(path, output.ToArray());
        }

        return entries;
    }

    private static string FindPluginRoot(IEnumerable<string> paths)
    {
        var values = paths.ToArray();
        if (values.Contains("plugin.json", StringComparer.Ordinal)) return string.Empty;

        var roots = values
            .Where(path => path.EndsWith("/plugin.json", StringComparison.Ordinal))
            .Select(path => path[..^"plugin.json".Length])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (roots.Length != 1)
            throw new InvalidDataException("Plugin ZIP must contain exactly one plugin.json package root.");
        return roots[0];
    }

    private static PluginManifest ValidateManifest(
        JsonElement root,
        List<AgentPluginDiagnostic> diagnostics)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("plugin.json must contain a JSON object.");

        foreach (var property in root.EnumerateObject())
        {
            if (!ManifestFields.Contains(property.Name))
                diagnostics.Add(Warning("manifest", "manifest-unknown-field",
                    $"Unknown plugin.json field '{property.Name}' was ignored.", "plugin.json"));
        }

        var schema = RequiredString(root, "$schema", "plugin.json");
        if (!string.Equals(schema, PluginSchema, StringComparison.Ordinal))
            throw new InvalidDataException($"Unsupported Agent Plugins manifest schema '{schema}'.");

        var name = RequiredString(root, "name", "plugin.json");
        if (name.Length is < 1 or > 64 || !PluginNameRegex().IsMatch(name))
            throw new InvalidDataException($"Plugin name '{name}' does not satisfy Agent Plugins v1 constraints.");

        ValidateOptionalString(root, "version");
        ValidateOptionalString(root, "description");
        ValidateOptionalString(root, "homepage");
        ValidateOptionalString(root, "repository");
        ValidateOptionalString(root, "license");
        ValidateAuthor(root);
        ValidateKeywords(root);

        JsonElement? extensions = null;
        if (root.TryGetProperty("extensions", out var extensionValue))
        {
            if (extensionValue.ValueKind == JsonValueKind.Object)
                extensions = extensionValue.Clone();
            else
                diagnostics.Add(Warning("extension", "manifest-invalid-extensions",
                    "The non-object extensions field was ignored.", "plugin.json"));
        }

        return new PluginManifest(
            name,
            OptionalString(root, "description"),
            OptionalString(root, "version"),
            extensions);
    }

    private static IReadOnlyList<LoadedAgentSkill> LoadSkills(
        IReadOnlyDictionary<string, byte[]> package,
        string pluginName,
        List<AgentPluginDiagnostic> diagnostics)
    {
        if (package.ContainsKey("skills"))
        {
            diagnostics.Add(Error("skills", "skills-not-directory",
                "The skills fixed location is not a directory.", "skills"));
            return [];
        }

        var results = new List<LoadedAgentSkill>();
        var skillEntries = package.Keys
            .Where(path => Regex.IsMatch(path, "^skills/[^/]+/SKILL\\.md$", RegexOptions.CultureInvariant))
            .OrderBy(path => path, StringComparer.Ordinal);

        foreach (var skillEntry in skillEntries)
        {
            var directory = skillEntry.Split('/')[1];
            try
            {
                using var bundleStream = new MemoryStream();
                using (var bundle = new ZipArchive(bundleStream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var prefix = $"skills/{directory}/";
                    foreach (var item in package.Where(item => item.Key.StartsWith(prefix, StringComparison.Ordinal)))
                    {
                        var relative = item.Key["skills/".Length..];
                        var target = bundle.CreateEntry(relative, CompressionLevel.Fastest);
                        using var targetStream = target.Open();
                        targetStream.Write(item.Value);
                    }
                }

                var loaded = AgentSkillCatalog.LoadBundle(bundleStream.ToArray());
                var skillId = $"plugin/{Uri.EscapeDataString(pluginName.ToLowerInvariant())}/{Uri.EscapeDataString(directory)}";
                results.Add(new LoadedAgentSkill(
                    skillId,
                    loaded.Name,
                    loaded.Description,
                    loaded.Body,
                    loaded.RootDirectoryName,
                    loaded.Resources));
            }
            catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
            {
                diagnostics.Add(Error("skill", "skill-invalid",
                    $"Plugin skill '{directory}' was skipped: {exception.Message}", skillEntry, directory));
            }
        }

        return results;
    }

    private static IReadOnlyDictionary<string, McpServer> LoadMcpServers(
        IReadOnlyDictionary<string, byte[]> package,
        PluginManifest manifest,
        string? extensionNamespace,
        List<AgentPluginDiagnostic> diagnostics)
    {
        if (!package.TryGetValue("mcp.json", out var bytes)) return new Dictionary<string, McpServer>();

        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.EnumerateObject().Any(property => !McpTopLevelFields.Contains(property.Name))
                || RequiredString(root, "$schema", "mcp.json") != McpSchema
                || !root.TryGetProperty("mcpServers", out var servers)
                || servers.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(Error("mcp", "mcp-invalid-document",
                    "mcp.json is invalid or targets an unsupported schema; MCP was disabled for this plugin.", "mcp.json"));
                return new Dictionary<string, McpServer>();
            }

            var extensionServers = ReadExtensionServers(manifest.Extensions, extensionNamespace);
            var result = new Dictionary<string, McpServer>(StringComparer.Ordinal);
            foreach (var property in servers.EnumerateObject())
            {
                if (!TryReadHttpServer(property.Name, property.Value, diagnostics, out var server))
                    continue;

                if (extensionServers.TryGetValue(property.Name, out var extension))
                {
                    server.AllowedCallers = extension.AllowedCallers;
                    server.DeferLoading = extension.DeferLoading;
                    server.Namespace = extension.Namespace;
                }

                var key = $"agent-plugin/{Uri.EscapeDataString(manifest.Name.ToLowerInvariant())}/{Uri.EscapeDataString(property.Name.ToLowerInvariant())}";
                result[key] = server;
            }

            return result;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            diagnostics.Add(Error("mcp", "mcp-invalid-document",
                $"mcp.json was disabled: {exception.Message}", "mcp.json"));
            return new Dictionary<string, McpServer>();
        }
    }

    private static bool TryReadHttpServer(
        string name,
        JsonElement value,
        List<AgentPluginDiagnostic> diagnostics,
        out McpServer server)
    {
        server = null!;
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("type", out var typeValue)
            || typeValue.ValueKind != JsonValueKind.String)
        {
            diagnostics.Add(Error("server", "server-invalid",
                $"MCP server '{name}' must be an object with a type.", "mcp.json", name));
            return false;
        }

        var type = typeValue.GetString();
        if (type is "stdio" or "sse")
        {
            diagnostics.Add(new AgentPluginDiagnostic("info", "server", "server-unsupported-transport",
                $"MCP server '{name}' uses unsupported transport '{type}' and was skipped.", "mcp.json", name));
            return false;
        }

        if (type != "streamable-http"
            || value.EnumerateObject().Any(property => !HttpServerFields.Contains(property.Name))
            || !value.TryGetProperty("url", out var urlValue)
            || urlValue.ValueKind != JsonValueKind.String
            || !TryValidateRemoteUrl(urlValue.GetString(), out var url)
            || !TryReadHeaders(value, out var headers))
        {
            diagnostics.Add(Error("server", "server-invalid-http",
                $"Streamable HTTP MCP server '{name}' has invalid fields, URL, or headers and was skipped.", "mcp.json", name));
            return false;
        }

        server = new McpServer
        {
            Type = "http",
            Url = url!,
            Headers = headers?.ToDictionary(item => item.Key, item => (object)item.Value, StringComparer.OrdinalIgnoreCase)
        };
        return true;
    }

    private static bool TryReadHeaders(JsonElement server, out Dictionary<string, string>? headers)
    {
        headers = null;
        if (!server.TryGetProperty("headers", out var value)) return true;
        if (value.ValueKind != JsonValueKind.Object) return false;

        headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject())
        {
            if (!HeaderNameRegex().IsMatch(property.Name)
                || property.Value.ValueKind != JsonValueKind.String
                || property.Value.GetString() is not { } headerValue
                || headerValue.Contains('\r')
                || headerValue.Contains('\n')
                || !headers.TryAdd(property.Name, headerValue))
                return false;
        }

        return true;
    }

    private static bool TryValidateRemoteUrl(string? value, out string? normalized)
    {
        normalized = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment))
            return false;

        var loopback = string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address);
        if (uri.Scheme == "http" && !loopback) return false;
        normalized = uri.AbsoluteUri;
        return true;
    }

    private static Dictionary<string, PluginServerExtension> ReadExtensionServers(
        JsonElement? extensions,
        string? extensionNamespace)
    {
        var result = new Dictionary<string, PluginServerExtension>(StringComparer.Ordinal);
        if (extensions is not { ValueKind: JsonValueKind.Object }
            || string.IsNullOrWhiteSpace(extensionNamespace)
            || !extensions.Value.TryGetProperty(extensionNamespace, out var extension)
            || extension.ValueKind != JsonValueKind.Object
            || !extension.TryGetProperty("mcpServers", out var servers)
            || servers.ValueKind != JsonValueKind.Object)
            return result;

        foreach (var property in servers.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object) continue;
            var callers = property.Value.TryGetProperty("allowed_callers", out var callerValue)
                && callerValue.ValueKind == JsonValueKind.Array
                    ? callerValue.EnumerateArray()
                        .Where(item => item.ValueKind == JsonValueKind.String
                            && item.GetString() is "direct" or "programmatic")
                        .Select(item => item.GetString()!)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray()
                    : null;
            bool? deferLoading = property.Value.TryGetProperty("defer_loading", out var deferValue)
                && deferValue.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? deferValue.GetBoolean()
                    : null;
            bool? useNamespace = property.Value.TryGetProperty("namespace", out var namespaceValue)
                && namespaceValue.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? namespaceValue.GetBoolean()
                    : null;
            result[property.Name] = new PluginServerExtension(callers, deferLoading, useNamespace);
        }

        return result;
    }

    private static void ValidateAuthor(JsonElement root)
    {
        if (!root.TryGetProperty("author", out var value)) return;
        if (value.ValueKind != JsonValueKind.Object
            || value.EnumerateObject().Any(property => property.Name is not ("name" or "email" or "url")
                || property.Value.ValueKind != JsonValueKind.String))
            throw new InvalidDataException("plugin.json author must contain only string name, email, and url fields.");
    }

    private static void ValidateKeywords(JsonElement root)
    {
        if (!root.TryGetProperty("keywords", out var value)) return;
        if (value.ValueKind != JsonValueKind.Array
            || value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
            throw new InvalidDataException("plugin.json keywords must be an array of strings.");
    }

    private static void ValidateOptionalString(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"plugin.json field '{name}' must be a string.");
    }

    private static string RequiredString(JsonElement root, string name, string path)
    {
        if (!root.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"{path} field '{name}' must be a non-empty string.");
        return value.GetString()!;
    }

    private static string? OptionalString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IsTextFile(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".md" or ".txt" or ".json" or ".yaml" or ".yml"
            or ".xml" or ".html" or ".htm" or ".csv" or ".tsv" or ".js" or ".ts" or ".jsx"
            or ".tsx" or ".css" or ".scss" or ".cs" or ".py" or ".sh" or ".ps1" or ".sql"
            or ".toml" or ".ini" or ".cfg" or ".env";

    private static string GetMimeType(string path)
        => Path.GetExtension(path).ToLowerInvariant() switch
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
            ".css" => "text/css",
            ".py" => "text/x-python",
            ".sh" => "text/x-shellscript",
            ".sql" => "application/sql",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".pdf" => "application/pdf",
            _ => "application/octet-stream"
        };

    private static AgentPluginDiagnostic Error(
        string boundary,
        string code,
        string message,
        string? path = null,
        string? entry = null)
        => new("error", boundary, code, message, path, entry);

    private static AgentPluginDiagnostic Warning(
        string boundary,
        string code,
        string message,
        string? path = null,
        string? entry = null)
        => new("warning", boundary, code, message, path, entry);

    private sealed record PluginManifest(
        string Name,
        string? Description,
        string? Version,
        JsonElement? Extensions);

    private sealed record PluginServerExtension(
        IReadOnlyList<string>? AllowedCallers,
        bool? DeferLoading,
        bool? Namespace);
}
