using System.ComponentModel;
using System.Text.Json;
using AgentHappey.Common.Extensions;
using AIHappey.Responses;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;

namespace AgentHappey.Core.ChatClient;

public partial class AgentChatClient
{
    internal const string ClientToolSearchName = "client_tool_search";
    internal const string ToolSearchType = "tool_search";
    private const int MaximumToolSearchResults = 10;

    internal const string ClientToolSearchInstructions =
        "Select the tools that best satisfy the supplied search goal from the supplied tool catalog. Return exactly one JSON object with the shape {\"selectedToolNames\":[\"exact_tool_name\"]}. Use only exact names present in the catalog, preserve relevance order, include no duplicates, select at most 10 tools, and include no markdown or text outside the JSON object.";

    private bool HasAgentTool(string type)
        => agent.Tools?.Any(tool => string.Equals(tool.Type, type, StringComparison.Ordinal)) == true;

    [DisplayName(ClientToolSearchName)]
    [Description("Search the available tool catalog and load the tools that best satisfy a goal.")]
    private async Task<CallToolResult> ClientToolSearchAsync(
        [Description("A concise description of the capability or task to find tools for.")]
        string goal,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var catalog = BuildCanonicalToolCatalog();
        if (string.IsNullOrWhiteSpace(goal) || catalog.Count == 0)
            return CreateToolSearchResult([]);

        try
        {
            var input = JsonSerializer.Serialize(new
            {
                goal = goal.Trim(),
                tools = catalog
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
                            new InputTextPart(ClientToolSearchInstructions),
                            new InputTextPart(input)
                        ])
                    }
                ]),
                Tools = [],
                ToolChoice = "none",
                ParallelToolCalls = false,
                Stream = false,
                Temperature = agent.Model.Options?.Temperature ?? 1,
                Metadata = BuildResponsesProviderMetadata()
            };

            EnsureHeaders();
            var response = await http.GetResponses(
                request,
                capture: ResolveBackendCaptureRequest(),
                providerHeaders: agent.Model.ProviderHeaders,
                ct: cancellationToken);

            var selected = ParseToolSearchSelection(ExtractResponseText(response), catalog);
            return CreateToolSearchResult(selected);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return CreateToolSearchResult([]);
        }
    }

    private List<CanonicalToolDefinition> BuildCanonicalToolCatalog()
        => Tools.Values
            .OfType<AIFunctionDeclaration>()
            .Where(tool => !string.Equals(tool.Name, ClientToolSearchName, StringComparison.Ordinal)
                && !string.Equals(tool.Name, ToolSearchType, StringComparison.Ordinal))
            .DistinctBy(tool => tool.Name, StringComparer.Ordinal)
            .Select(tool => new CanonicalToolDefinition(
                tool.Name,
                string.IsNullOrWhiteSpace(tool.Description) ? null : tool.Description,
                tool.JsonSchema.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                    ? JsonSerializer.SerializeToElement(new { type = "object", properties = new { } })
                    : tool.JsonSchema.Clone(),
                tool.ReturnJsonSchema is { 
                    ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } outputSchema
                    ? outputSchema.Clone()
                    : null))
            .ToList();

    private static List<CanonicalToolDefinition> ParseToolSearchSelection(
        string? text,
        IReadOnlyList<CanonicalToolDefinition> catalog)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.EnumerateObject().Count() != 1
                || !root.TryGetProperty("selectedToolNames", out var names)
                || names.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var byName = catalog.ToDictionary(tool => tool.Name, StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var selected = new List<CanonicalToolDefinition>();

            foreach (var value in names.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.String)
                    continue;

                var name = value.GetString();
                if (name is null || !seen.Add(name) || !byName.TryGetValue(name, out var tool))
                    continue;

                selected.Add(tool);
                if (selected.Count == MaximumToolSearchResults)
                    break;
            }

            return selected;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static CallToolResult CreateToolSearchResult(IReadOnlyList<CanonicalToolDefinition> selectedTools)
        => new()
        {
            IsError = false,
            Content =
            [
                (selectedTools.Count > 0
                    ? $"Selected {selectedTools.Count} tool(s): {string.Join(", ", selectedTools.Select(tool => tool.Name))}"
                    : "No matching tools were selected.").ToContentBlock()
            ],
            StructuredContent = JsonSerializer.SerializeToElement(new { selectedTools }, JsonSerializerOptions.Web)
        };

    private static string? ExtractResponseText(ResponseResult response)
    {
        if (!string.IsNullOrWhiteSpace(response.OutputText))
            return response.OutputText.Trim();

        foreach (var item in response.Output ?? [])
        {
            var json = item is JsonElement element
                ? element
                : JsonSerializer.SerializeToElement(item, ResponseJson.Default);
            if (json.ValueKind != JsonValueKind.Object
                || !json.TryGetProperty("type", out var type)
                || type.GetString() != "message"
                || !json.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var text = string.Join("\n\n", content.EnumerateArray()
                .Where(part => part.TryGetProperty("type", out var partType)
                    && partType.GetString() is "output_text" or "text")
                .Select(part => part.TryGetProperty("text", out var value) ? value.GetString() : null)
                .Where(value => !string.IsNullOrWhiteSpace(value)));
            if (!string.IsNullOrWhiteSpace(text))
                return text.Trim();
        }

        return null;
    }

    private sealed record CanonicalToolDefinition(
        string Name,
        string? Description,
        JsonElement InputSchema,
        JsonElement? OutputSchema);
}
