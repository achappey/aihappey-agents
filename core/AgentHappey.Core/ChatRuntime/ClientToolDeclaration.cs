using System.Text.Json;
using AgentHappey.Common.Extensions;
using Microsoft.Extensions.AI;
using Tool = AIHappey.Vercel.Models.Tool;

namespace AgentHappey.Core.ChatRuntime;

internal sealed class ClientToolDeclaration : AIFunctionDeclaration
{
    internal const string OwnershipKey = "agenthappey_client_tool";

    private readonly string name;
    private readonly string description;
    private readonly JsonElement schema;
    private readonly JsonElement? returnSchema;

    private ClientToolDeclaration(Tool tool)
    {
        name = tool.Name;
        description = tool.Description ?? string.Empty;
        schema = tool.InputSchema is null
            ? JsonSerializer.SerializeToElement(new { type = "object", properties = new { } })
            : JsonSerializer.SerializeToElement(tool.InputSchema, JsonSerializerOptions.Web);
        returnSchema = tool.OutputSchema is null
            ? null
            : JsonSerializer.SerializeToElement(tool.OutputSchema, JsonSerializerOptions.Web);
    }

    public override string Name => name;
    public override string Description => description;
    public override JsonElement JsonSchema => schema;
    public override JsonElement? ReturnJsonSchema => returnSchema;

    internal static void AddTo(List<AITool> serverTools, IReadOnlyList<Tool>? clientTools)
    {
        if (clientTools is not { Count: > 0 })
            return;

        var names = serverTools.Select(tool => tool.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // These names also control history replay or native provider tool routing.
        names.UnionWith(["ai_input_required", "approval-request", "connect_mcp",
            "client_tool_search", "resource_search", "program", "web_search",
            "code_interpreter", "image_generation", "shell_call"]);

        var declarations = new List<AITool>();
        foreach (var tool in clientTools)
        {
            if (tool is null || string.IsNullOrWhiteSpace(tool.Name))
                throw new ArgumentException("Client tools must have a non-empty name.");
            if (VercelHelpers.IsContinuationStateTool(tool.Name) || !names.Add(tool.Name))
                throw new ArgumentException($"Client tool name '{tool.Name}' is duplicate or reserved.");

            declarations.Add(new ClientToolDeclaration(tool));
        }

        serverTools.AddRange(declarations);
    }

    internal static void MarkCalls(IEnumerable<AIContent> contents, ChatOptions? options)
    {
        var names = options?.Tools?.OfType<ClientToolDeclaration>()
            .Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        if (names is not { Count: > 0 })
            return;

        foreach (var call in contents.OfType<FunctionCallContent>())
        {
            if (names.Contains(call.Name))
            {
                call.AdditionalProperties ??= new();
                call.AdditionalProperties[OwnershipKey] = true;
            }
        }
    }
}
