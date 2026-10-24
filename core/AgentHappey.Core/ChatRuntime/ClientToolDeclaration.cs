using System.Text.Json;
using System.Runtime.CompilerServices;
using AgentHappey.Common.Extensions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Tool = AIHappey.Vercel.Models.Tool;

namespace AgentHappey.Core.ChatRuntime;

internal sealed class ClientToolDeclaration : AIFunction
{
    internal const string OwnershipKey = "agenthappey_client_tool";
    private static readonly IReadOnlyDictionary<string, object?> Ownership =
        new Dictionary<string, object?> { [OwnershipKey] = true };

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
    public override IReadOnlyDictionary<string, object?> AdditionalProperties => Ownership;

    internal static bool IsClientTool(AITool tool) =>
        tool.AdditionalProperties.TryGetValue(OwnershipKey, out var owner) && owner is true;

    // Declaration-only functions stop the SDK before it executes *any* tool in a
    // mixed batch. A private suspension marker lets its normal loop execute the
    // server functions too, without executing client code or inventing a result.
    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        => ValueTask.FromResult<object?>(new PendingResult());

    internal sealed class PendingResult { }

    internal static bool ShouldSuspendAfterBatch(FunctionInvocationContext invocation)
    {
        // Terminate also skips subsequent calls in the current SDK batch. Wait
        // until its last invocation so pending client calls cannot suppress server
        // execution. The SDK marks processed calls informational after the batch.
        // Only inspect its current call count: older unresolved client history may
        // intentionally be omitted from replay and must not suspend a new batch.
        return invocation.FunctionCallIndex == invocation.FunctionCount - 1
            && invocation.Messages.SelectMany(message => message.Contents)
                .OfType<FunctionCallContent>()
                .Where(call => !call.InformationalOnly)
                .TakeLast(invocation.FunctionCount)
                .Any(call => call.AdditionalProperties?.TryGetValue(OwnershipKey, out var owner) == true
                    && owner is true);
    }

    internal static AIAgent WrapAgent(AIAgent agent) => agent.AsBuilder().Use(
        async (messages, session, options, inner, ct) =>
        {
            var response = await inner.RunAsync(messages, session, options, ct);
            foreach (var message in response.Messages)
                message.Contents = RemovePendingResults(message.Contents);
            return response;
        },
        (messages, session, options, inner, ct) =>
            FilterUpdates(inner.RunStreamingAsync(messages, session, options, ct), ct)).Build();

    private static async IAsyncEnumerable<AgentResponseUpdate> FilterUpdates(
        IAsyncEnumerable<AgentResponseUpdate> updates,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var update in updates.WithCancellation(cancellationToken))
        {
            update.Contents = RemovePendingResults(update.Contents);
            yield return update;
        }
    }

    private static IList<AIContent> RemovePendingResults(IList<AIContent> contents)
    {
        var filtered = new List<AIContent>(contents.Count);
        foreach (var content in contents)
        {
            if (content is FunctionResultContent { Result: PendingResult })
                continue;
            if (content is FunctionCallContent call
                && call.AdditionalProperties?.TryGetValue(OwnershipKey, out var clientOwned) == true
                && clientOwned is true)
            {
                // The SDK later mutates its call to InformationalOnly after
                // invocation; keep the externally executable call independent.
                filtered.Add(new FunctionCallContent(call.CallId, call.Name, call.Arguments)
                {
                    RawRepresentation = call.RawRepresentation,
                    AdditionalProperties = call.AdditionalProperties,
                    Annotations = call.Annotations,
                    Exception = call.Exception,
                    InformationalOnly = false
                });
            }
            else
                filtered.Add(content);
        }
        return filtered;
    }

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
        var names = options?.Tools?.Where(IsClientTool)
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
