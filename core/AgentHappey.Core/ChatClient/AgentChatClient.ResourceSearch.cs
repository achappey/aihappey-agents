using System.ComponentModel;
using System.Text.Json;
using AgentHappey.Common.Extensions;
using AIHappey.Responses;
using ModelContextProtocol.Protocol;

namespace AgentHappey.Core.ChatClient;

public partial class AgentChatClient
{
    internal const string ResourceSearchName = "search_resources";
    internal const string ResourceSearchType = "resource_search";
    private const int MaximumResourceSearchResults = 20;

    internal const string ResourceSearchInstructions =
        "Select the MCP resources and resource templates that best satisfy the supplied query from the supplied server-scoped catalog. Return exactly one JSON object with the shape {\"selectedResourceUris\":[\"exact_resource_uri\"],\"selectedResourceTemplateUriTemplates\":[\"exact_uri_template\"]}. Use only exact values present in the catalog, preserve relevance order, include no duplicates, select at most 20 entries in each array, and include no markdown or text outside the JSON object.";

    [DisplayName(ResourceSearchName)]
    [Description("Search the resources and resource templates advertised by one connected MCP server. Results preserve their MCP shapes and can be read with read_resource.")]
    private async Task<CallToolResult> SearchResourcesAsync(
        [Description("Exact URL of the connected MCP server whose resources should be searched.")]
        string serverUrl,
        [Description("Concise description or keywords for the resources to find.")]
        string query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out _))
            return CreateResourceSearchError("Missing or invalid serverUrl.");

        var normalizedServerUrl = serverUrl.Trim().ToLowerInvariant();
        if (!McpClients.ContainsKey(normalizedServerUrl))
        {
            return CreateResourceSearchError(
                $"Invalid url. Connected servers: {string.Join("\n", McpClients.Keys.Order(StringComparer.Ordinal))}");
        }

        var catalog = BuildResourceSearchCatalog(normalizedServerUrl);
        if (string.IsNullOrWhiteSpace(query)
            || (catalog.Resources.Count == 0 && catalog.ResourceTemplates.Count == 0))
        {
            return CreateResourceSearchResult([], []);
        }

        try
        {
            var input = JsonSerializer.Serialize(new
            {
                query = query.Trim(),
                serverUrl,
                resources = catalog.Resources,
                resourceTemplates = catalog.ResourceTemplates
            }, JsonSerializerOptions.Web);

            var request = new ResponseRequest
            {
                Model = agent.Model.Id,
                Instructions = GetComposedInstructions(),
                Input = new ResponseInput(
                [
                    new ResponseInputMessage
                    {
                        Role = ResponseRole.User,
                        Content = new ResponseMessageContent(
                        [
                            new InputTextPart(ResourceSearchInstructions),
                            new InputTextPart(input)
                        ])
                    }
                ]),
                Tools = [],
                ToolChoice = "none",
                ParallelToolCalls = false,
                Stream = false,
                Temperature = agent.Model.Options?.Temperature ?? 1,
                Metadata = BuildSideInferenceProviderMetadata()
            };

            EnsureHeaders();
            var response = await http.GetResponses(
                request,
                capture: ResolveBackendCaptureRequest(),
                providerHeaders: agent.Model.ProviderHeaders,
                ct: cancellationToken);

            var selected = ParseResourceSearchSelection(ExtractResponseText(response), catalog);
            return CreateResourceSearchResult(selected.Resources, selected.ResourceTemplates);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return CreateResourceSearchResult([], []);
        }
    }

    private ResourceSearchCatalog BuildResourceSearchCatalog(string serverUrl)
    {
        McpServerResources.TryGetValue(serverUrl, out var resources);
        McpServerResourceTemplates.TryGetValue(serverUrl, out var resourceTemplates);

        return new ResourceSearchCatalog(
            ToAssistantVisibleJsonObjects(resources),
            ToAssistantVisibleJsonObjects(resourceTemplates));
    }

    private static List<JsonElement> ToAssistantVisibleJsonObjects(IEnumerable<object>? values)
        => (values ?? [])
            .Select(ToResourceSearchJson)
            .Where(value => value is { ValueKind: JsonValueKind.Object } && IsResourceVisibleToAssistant(value.Value))
            .Select(value => value!.Value)
            .ToList();

    private static JsonElement? ToResourceSearchJson(object value)
    {
        try
        {
            return value is JsonElement element
                ? element.Clone()
                : JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsResourceVisibleToAssistant(JsonElement value)
    {
        if (!value.TryGetProperty("annotations", out var annotations)
            || annotations.ValueKind != JsonValueKind.Object
            || !annotations.TryGetProperty("audience", out var audience)
            || audience.ValueKind != JsonValueKind.Array)
        {
            return true;
        }

        var audiences = audience.EnumerateArray().ToList();
        return audiences.Count == 0
            || audiences.Any(entry => entry.ValueKind == JsonValueKind.String
                && string.Equals(entry.GetString(), "assistant", StringComparison.Ordinal));
    }

    private static ResourceSearchCatalog ParseResourceSearchSelection(
        string? text,
        ResourceSearchCatalog catalog)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new([], []);

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.EnumerateObject().Any(property => property.Name is not
                    ("selectedResourceUris" or "selectedResourceTemplateUriTemplates")))
            {
                return new([], []);
            }

            var resources = SelectCatalogEntries(
                root,
                "selectedResourceUris",
                catalog.Resources,
                "uri");
            var resourceTemplates = SelectCatalogEntries(
                root,
                "selectedResourceTemplateUriTemplates",
                catalog.ResourceTemplates,
                "uriTemplate");

            return new(resources, resourceTemplates);
        }
        catch (JsonException)
        {
            return new([], []);
        }
    }

    private static List<JsonElement> SelectCatalogEntries(
        JsonElement root,
        string selectionProperty,
        IReadOnlyList<JsonElement> catalog,
        string catalogKey)
    {
        if (!root.TryGetProperty(selectionProperty, out var selectedValues)
            || selectedValues.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var byValue = catalog
            .Where(entry => entry.TryGetProperty(catalogKey, out var value)
                && value.ValueKind == JsonValueKind.String)
            .GroupBy(entry => entry.GetProperty(catalogKey).GetString()!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var selected = new List<JsonElement>();

        foreach (var value in selectedValues.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String)
                continue;

            var key = value.GetString();
            if (key is null || !seen.Add(key) || !byValue.TryGetValue(key, out var entry))
                continue;

            selected.Add(entry);
            if (selected.Count == MaximumResourceSearchResults)
                break;
        }

        return selected;
    }

    private static CallToolResult CreateResourceSearchResult(
        IReadOnlyList<JsonElement> resources,
        IReadOnlyList<JsonElement> resourceTemplates)
        => new()
        {
            IsError = false,
            Content =
            [
                $"Found {resources.Count} resource(s) and {resourceTemplates.Count} resource template(s)."
                    .ToContentBlock()
            ],
            StructuredContent = JsonSerializer.SerializeToElement(
                new { resources, resourceTemplates },
                JsonSerializerOptions.Web)
        };

    private static CallToolResult CreateResourceSearchError(string message)
        => new()
        {
            IsError = true,
            Content = [message.ToContentBlock()]
        };

    private sealed record ResourceSearchCatalog(
        List<JsonElement> Resources,
        List<JsonElement> ResourceTemplates);
}
