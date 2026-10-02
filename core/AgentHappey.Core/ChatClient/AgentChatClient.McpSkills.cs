using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentHappey.Core.Skills;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using YamlDotNet.Serialization;

namespace AgentHappey.Core.ChatClient;

public partial class AgentChatClient
{
    private const string SkillsExtension = "io.modelcontextprotocol/skills";

    private static bool SupportsMcpSkills(McpClient client)
        => client.ServerCapabilities.Resources is not null &&
           client.ServerCapabilities.Extensions?.ContainsKey(SkillsExtension) == true;

    private static async Task<IReadOnlyList<JsonObject>> ListConnectedSkillsAsync(
        McpClient client, string serverUrl, CancellationToken cancellationToken)
    {
        var entries = new List<JsonObject>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var parameters = new JsonObject();
            if (cursor is not null) parameters["cursor"] = cursor;
            var response = await client.SendRequestAsync(new JsonRpcRequest
            {
                Method = "skills/list", Params = parameters
            }, cancellationToken);
            if (response.Result is not JsonObject result || result["skills"] is not JsonArray skills)
                throw new InvalidDataException($"Invalid MCP skills/list response from {serverUrl}.");

            foreach (var node in skills)
            {
                if (node is JsonObject entry && ValidMcpSkillEntry(entry))
                    entries.Add(entry);
            }

            cursor = result["nextCursor"]?.GetValue<string>();
            if (cursor is not null && !cursors.Add(cursor))
                throw new InvalidDataException($"MCP skills/list repeated a cursor on {serverUrl}.");
        } while (cursor is not null);
        return entries;
    }

    private static bool ValidMcpSkillEntry(JsonObject entry)
    {
        try
        {
            var uri = entry["uri"]?.GetValue<string>();
            var frontmatter = entry["frontmatter"] as JsonObject;
            var name = frontmatter?["name"]?.GetValue<string>();
            var description = frontmatter?["description"]?.GetValue<string>();
            if (uri is null || !uri.EndsWith("/SKILL.md", StringComparison.Ordinal) ||
                !Uri.TryCreate(uri, UriKind.Absolute, out _) ||
                string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(description) ||
                uri[..^"/SKILL.md".Length].Split('/')[^1] != name ||
                entry["resources"] is not JsonArray resources || resources.Count is < 1 or > 512)
                return false;

            var root = uri[..^"SKILL.md".Length];
            var seen = new HashSet<string>(StringComparer.Ordinal);
            long total = 0;
            foreach (var node in resources)
            {
                if (node is not JsonObject file) return false;
                var fileUri = file["uri"]?.GetValue<string>();
                var digest = file["digest"]?.GetValue<string>();
                var size = file["size"]?.GetValue<long>() ?? -1;
                if (fileUri is null || !fileUri.StartsWith(root, StringComparison.Ordinal) ||
                    !seen.Add(fileUri) || size < 0 || (total += size) > 16 * 1024 * 1024 ||
                    digest is null || digest.Length != 71 || !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
                    digest[7..].Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
                    fileUri[root.Length..].Split('/').Any(p => p is "" or "." or ".." ||
                        p.Contains("%2f", StringComparison.OrdinalIgnoreCase) || p.Contains("%5c", StringComparison.OrdinalIgnoreCase)))
                    return false;
            }
            return seen.Contains(uri);
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    private void AddConnectedSkills(IReadOnlyList<JsonObject> entries, string serverUrl)
    {
        foreach (var entry in entries)
        {
            var uri = entry["uri"]!.GetValue<string>();
            var frontmatter = (JsonObject)entry["frontmatter"]!;
            var name = frontmatter["name"]!.GetValue<string>();
            var root = uri[..^"SKILL.md".Length];
            var paths = ((JsonArray)entry["resources"]!).OfType<JsonObject>()
                .Select(file => file["uri"]!.GetValue<string>())
                .Where(file => file != uri).Select(file => file[root.Length..]).ToArray();

            // Existing local and plugin IDs retain precedence. A remote skill needs an
            // origin-qualified ID only when its name is already used (including same-URI servers).
            var id = name;
            if (GetEnabledSkills().Any(skill => skill.SkillId == id))
                id = $"mcp/{Uri.EscapeDataString(serverUrl)}/{Uri.EscapeDataString(uri)}";
            if (GetEnabledSkills().Any(skill => skill.SkillId == id)) continue;

            var source = serverUrl;
            var manifest = (JsonObject)entry.DeepClone();
            connectedSkills.Add(new LoadedAgentSkill(id, name, frontmatter["description"]!.GetValue<string>(),
                string.Empty, name, new Dictionary<string, LoadedAgentSkillResource>(StringComparer.Ordinal),
                paths,
                async ct =>
                {
                    var bytes = await ReadConnectedSkillFileAsync(source, manifest, uri, ct);
                    return ExtractSkillBody(bytes);
                },
                async (path, ct) =>
                {
                    var fileUri = root + path;
                    var bytes = await ReadConnectedSkillFileAsync(source, manifest, fileUri, ct);
                    var mime = Path.GetExtension(path).ToLowerInvariant() switch
                    {
                        ".md" => "text/markdown", ".txt" => "text/plain", ".json" => "application/json",
                        ".yaml" or ".yml" => "application/yaml", ".png" => "image/png",
                        ".jpg" or ".jpeg" => "image/jpeg", ".pdf" => "application/pdf",
                        _ => "application/octet-stream"
                    };
                    var isText = mime.StartsWith("text/", StringComparison.Ordinal) ||
                                 mime is "application/json" or "application/yaml";
                    return new LoadedAgentSkillResource(path, bytes, mime, isText);
                }));
        }
    }

    private async Task<byte[]> ReadConnectedSkillFileAsync(string serverUrl, JsonObject entry,
        string uri, CancellationToken cancellationToken)
    {
        if (!McpClients.TryGetValue(serverUrl, out var client))
            throw new InvalidOperationException($"MCP server {serverUrl} is no longer connected.");

        var expected = ((JsonArray)entry["resources"]!).OfType<JsonObject>()
            .FirstOrDefault(file => file["uri"]!.GetValue<string>() == uri);
        if (expected is null)
            throw new InvalidOperationException("Resource is not in the MCP skill manifest.");

        var response = await client.ReadResourceAsync(new Uri(uri), cancellationToken: cancellationToken);
        var content = response.Contents.FirstOrDefault(item => item.Uri == uri);
        byte[] bytes = content switch
        {
            TextResourceContents text => Encoding.UTF8.GetBytes(text.Text),
            BlobResourceContents blob => blob.Blob.ToArray(),
            _ => throw new InvalidDataException("MCP server did not return the requested skill resource.")
        };
        var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (bytes.LongLength != expected["size"]!.GetValue<long>() ||
            digest != expected["digest"]!.GetValue<string>())
            throw new InvalidDataException("MCP skill file failed integrity verification.");

        if (uri == entry["uri"]!.GetValue<string>())
        {
            var markdown = new UTF8Encoding(false, true).GetString(bytes);
            var yaml = ExtractFrontmatter(markdown);
            var raw = new DeserializerBuilder().Build().Deserialize<object>(yaml);
            var parsed = JsonSerializer.SerializeToNode(NormalizeYaml(raw));
            if (!JsonNode.DeepEquals(parsed, entry["frontmatter"]))
                throw new InvalidDataException("MCP skill frontmatter does not match its manifest.");
        }
        return bytes;
    }

    private static string ExtractFrontmatter(string markdown)
    {
        using var reader = new StringReader(markdown);
        if (reader.ReadLine() != "---") throw new InvalidDataException("MCP SKILL.md has no frontmatter.");
        var lines = new List<string>();
        string? line;
        while ((line = reader.ReadLine()) != null && line != "---") lines.Add(line);
        if (line is null) throw new InvalidDataException("MCP SKILL.md has no closing frontmatter delimiter.");
        return string.Join('\n', lines);
    }

    private static string ExtractSkillBody(byte[] bytes)
    {
        using var reader = new StringReader(new UTF8Encoding(false, true).GetString(bytes));
        reader.ReadLine();
        while (reader.ReadLine() is { } line && line != "---") { }
        return reader.ReadToEnd().Trim();
    }

    private static object? NormalizeYaml(object? value) => value switch
    {
        IDictionary<object, object> map => map.ToDictionary(item => item.Key.ToString()!, item => NormalizeYaml(item.Value)),
        System.Collections.IList list => list.Cast<object>().Select(NormalizeYaml).ToArray(),
        _ => value
    };
}
