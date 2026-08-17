using System.ComponentModel;
using System.Text.Json;
using AgentHappey.Common.Extensions;
using AgentHappey.Common.Models;
using AgentHappey.Core.Plugins;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;

namespace AgentHappey.Core.ChatClient;

public partial class AgentChatClient
{
    private IReadOnlyList<LoadedAgentPlugin>? loadedPlugins;
    private IReadOnlyList<AgentPluginDiagnostic>? pluginDiagnostics;
    private readonly object loadPluginsLock = new();

    public IReadOnlyList<AgentPluginDiagnostic> GetPluginDiagnostics()
    {
        EnsurePluginsLoaded();
        return pluginDiagnostics ?? [];
    }

    private IReadOnlyList<LoadedAgentPlugin> EnsurePluginsLoaded()
    {
        if (loadedPlugins is not null) return loadedPlugins;

        lock (loadPluginsLock)
        {
            if (loadedPlugins is not null) return loadedPlugins;
            var result = AgentPluginCatalog.Load(agent.Plugins, agentPluginExtensionNamespace);
            pluginDiagnostics = result.Diagnostics;
            return loadedPlugins = result.Plugins;
        }
    }

    private IReadOnlyList<LoadedAgentPlugin> GetPluginsWithReadableFiles()
        => EnsurePluginsLoaded().Where(plugin => plugin.Files.Count > 0).ToArray();

    private IEnumerable<KeyValuePair<string, McpServer>> GetEnabledMcpServers()
    {
        var configured = (agent.McpServers ?? [])
            .Where(item => item.Value.Disabled != true)
            .ToList();
        var urls = configured
            .Select(item => item.Value.Url)
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var item in configured)
            yield return item;

        foreach (var item in EnsurePluginsLoaded().SelectMany(plugin => plugin.McpServers))
        {
            if (item.Value.Disabled == true || !urls.Add(item.Value.Url)) continue;
            yield return item;
        }
    }

    [DisplayName("read_plugin_file")]
    [Description("Reads a file outside the skills directories of an enabled Agent Plugin. Provide the exact plugin name and package-relative path shown in system context.")]
    private Task<CallToolResult> ReadPluginFileAsync(
        [Description("Exact enabled plugin name.")] string plugin_name,
        [Description("Package-relative file path outside skills/.")] string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var plugin = GetPluginsWithReadableFiles().FirstOrDefault(item =>
            string.Equals(item.Name, plugin_name?.Trim(), StringComparison.Ordinal));
        if (plugin is null)
            throw new InvalidOperationException($"Plugin '{plugin_name}' is not enabled.");

        var normalizedPath = AgentPluginCatalog.NormalizePath(path);
        if (normalizedPath.StartsWith("skills/", StringComparison.Ordinal)
            || normalizedPath is "plugin.json" or "mcp.json")
            throw new InvalidOperationException("Use the skill tools for files inside skills/. Plugin metadata files are not readable through this tool.");
        if (!plugin.Files.TryGetValue(normalizedPath, out var file))
            throw new InvalidOperationException($"File '{normalizedPath}' was not found in plugin '{plugin.Name}'.");

        if (file.IsText)
        {
            var text = file.ReadText();
            return Task.FromResult(new CallToolResult
            {
                IsError = false,
                StructuredContent = JsonSerializer.SerializeToElement(new
                {
                    pluginFile = new
                    {
                        pluginName = plugin.Name,
                        path = file.Path,
                        mimeType = file.MimeType,
                        text
                    }
                }, JsonSerializerOptions.Web),
                Content =
                [
                    $"<plugin_file plugin_name=\"{EscapePluginAttribute(plugin.Name)}\" path=\"{EscapePluginAttribute(file.Path)}\" mimeType=\"{EscapePluginAttribute(file.MimeType)}\">\n{text}\n</plugin_file>".ToContentBlock()
                ]
            });
        }

        var base64 = Convert.ToBase64String(file.Bytes);
        return Task.FromResult(new CallToolResult
        {
            IsError = false,
            StructuredContent = JsonSerializer.SerializeToElement(new
            {
                pluginFile = new
                {
                    pluginName = plugin.Name,
                    path = file.Path,
                    mimeType = file.MimeType,
                    encoding = "base64",
                    data = base64
                }
            }, JsonSerializerOptions.Web),
            Content =
            [
                $"Binary plugin file {file.Path} from {plugin.Name}. mimeType={file.MimeType}. Base64 payload is available in structuredContent.pluginFile.data.".ToContentBlock()
            ]
        });
    }

    private static string EscapePluginAttribute(string value)
        => string.IsNullOrEmpty(value)
            ? string.Empty
            : value
                .Replace("&", "&amp;", StringComparison.Ordinal)
                .Replace("\"", "&quot;", StringComparison.Ordinal)
                .Replace("<", "&lt;", StringComparison.Ordinal)
                .Replace(">", "&gt;", StringComparison.Ordinal);
}
