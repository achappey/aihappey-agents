using System.Text.Json;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.AI;

namespace AgentHappey.Common.Extensions;

public static class VercelHelpers
{
    public static bool IsContinuationStateTool(string? name) =>
        string.Equals(name, "google_antigravity_state", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "google_custom_agent_state", StringComparison.OrdinalIgnoreCase);

    private static string? ReadMetadataToolName(Dictionary<string, Dictionary<string, object>?>? metadata)
        => metadata?.Values.Where(value => value is not null)
            .Select(value => value!.TryGetValue("tool_name", out var name) ? name?.ToString()
                : value.TryGetValue("name", out name) ? name?.ToString() : null)
            .FirstOrDefault(IsContinuationStateTool);

    private static string? ReadOutputToolName(object? output)
    {
        if (output is null) return null;
        try
        {
            var json = JsonSerializer.SerializeToElement(output, JsonSerializerOptions.Web);
            if (json.ValueKind != JsonValueKind.Object) return null;
            if (json.TryGetProperty("structuredContent", out var structured)
                && structured.ValueKind == JsonValueKind.Object)
                json = structured;
            return json.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                ? type.GetString() : null;
        }
        catch { return null; }
    }

    private static string? ResolveContinuationName(string? name, object? output,
        Dictionary<string, Dictionary<string, object>?>? callMetadata,
        Dictionary<string, Dictionary<string, object>?>? resultMetadata)
        => IsContinuationStateTool(name) ? name
            : ReadMetadataToolName(resultMetadata) ?? ReadMetadataToolName(callMetadata)
                ?? (IsContinuationStateTool(ReadOutputToolName(output)) ? ReadOutputToolName(output) : null);

    private static bool BelongsToAgent(UIMessage message, string agentName, HashSet<string> activeNames,
        Dictionary<string, Dictionary<string, object>?>? callMetadata,
        Dictionary<string, Dictionary<string, object>?>? resultMetadata)
    {
        if (message.Metadata?.TryGetValue("model", out var owner) == true && owner is not null)
            return string.Equals(owner.ToString(), agentName, StringComparison.Ordinal);
        if (callMetadata?.ContainsKey(agentName) == true || resultMetadata?.ContainsKey(agentName) == true)
            return true;
        // Old histories lack an owner. Replaying them into multiple agents would leak
        // one provider session into another, so only accept them for a single agent.
        return activeNames.Count == 1;
    }

    private static Dictionary<string, Dictionary<string, object>?> ScopeMetadata(
        Dictionary<string, Dictionary<string, object>?>? metadata, string? owner)
    {
        var scoped = metadata is null
            ? new Dictionary<string, Dictionary<string, object>?>(StringComparer.Ordinal)
            : new Dictionary<string, Dictionary<string, object>?>(metadata, StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(owner))
            scoped[owner] = new Dictionary<string, object> { ["agent_name"] = owner };
        return scoped;
    }
        
    private static string NormalizeToolName(string? type) =>
        type?.StartsWith("tool-", StringComparison.OrdinalIgnoreCase) == true
            ? type["tool-".Length..]
            : (type ?? "unknown");

    private static bool HasConcreteOutput(object? output)
    {
        if (output is null)
            return false;

        if (output is JsonElement je)
            return je.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;

        return true;
    }

    private static bool IsApprovalControlPart(ToolInvocationPart ti, string toolName) =>
        string.Equals(ti.Type, "tool-approval-request", StringComparison.OrdinalIgnoreCase)
        || string.Equals(toolName, "approval-request", StringComparison.OrdinalIgnoreCase);

    private static bool IsConnectMcpControlPart(ToolInvocationPart ti, string toolName) =>
        string.Equals(ti.Type, "tool-connect_mcp", StringComparison.OrdinalIgnoreCase)
        || string.Equals(toolName, "connect_mcp", StringComparison.OrdinalIgnoreCase);

    private static bool IsToolOutputPart(UIMessagePart part, string toolCallId) =>
        part is ToolOutputAvailablePart outputAvailable
            && string.Equals(outputAvailable.ToolCallId, toolCallId, StringComparison.Ordinal)
        || part is ToolOutputErrorPart outputError
            && string.Equals(outputError.ToolCallId, toolCallId, StringComparison.Ordinal);

    private static object? GetToolOutput(UIMessagePart part) => part switch
    {
        ToolOutputAvailablePart outputAvailable => outputAvailable.Output,
        ToolOutputErrorPart outputError => new { error = outputError.ErrorText },
        _ => null
    };

    private static Dictionary<string, object?> CreateToolCallRawRepresentation(ToolCallPart part)
        => new(StringComparer.Ordinal)
        {
            ["provider_metadata"] = part.ProviderMetadata,
            ["title"] = part.Title,
            ["responses_type"] = ReadResponsesType(part.ProviderMetadata),
            ["responses_item"] = ReadResponsesItem(part.ProviderMetadata)
        };

    private static Dictionary<string, object?> CreateToolOutputEnvelope(ToolOutputAvailablePart part)
        => new(StringComparer.Ordinal)
        {
            ["output"] = part.Output,
            ["preliminary"] = part.Preliminary,
            ["provider_executed"] = part.ProviderExecuted ?? false,
            ["provider_metadata"] = part.ProviderMetadata,
            ["responses_item"] = ReadResponsesItem(part.ProviderMetadata)
        };

    private static object? ReadResponsesItem(Dictionary<string, Dictionary<string, object>?>? metadata)
        => metadata?.Values
            .Where(value => value is not null)
            .Select(value => value!.TryGetValue("responses_item", out var item) ? item : null)
            .FirstOrDefault(item => item is not null);

    private static string? ReadResponsesType(Dictionary<string, Dictionary<string, object>?>? metadata)
        => metadata?.Values
            .Where(value => value is not null)
            .Select(value => value!.TryGetValue("type", out var type) ? type?.ToString() : null)
            .FirstOrDefault(type => !string.IsNullOrWhiteSpace(type));

    public static AIContent? ToUserMessagePart(this UIMessagePart message)
    {
        return message switch
        {
            TextUIPart textUIPart => new TextContent(textUIPart.Text ?? ""),
            FileUIPart fileUIPart => new DataContent(fileUIPart.Url, fileUIPart.MediaType)
            {
                Name = fileUIPart.Filename
            },
            _ => null,
        };
    }

    public static IEnumerable<AIContent> ToUserMessageParts(this IEnumerable<UIMessagePart> messages)
        => [..messages?
            .Select(p => p.ToUserMessagePart())
            .OfType<AIContent>()
            .ToList() ?? []];

    public static IEnumerable<ChatMessage> ToMessages(
        this IEnumerable<UIMessage> messages,
        IEnumerable<string>? activeAgentNames = null)
    {
        var activeAgentNameSet = activeAgentNames?
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.Ordinal)
            ?? [];

        foreach (var ui in messages)
        {
            var owner = ui.Metadata?.GetValueOrDefault("model")?.ToString()
                ?? (activeAgentNameSet.Count == 1 ? activeAgentNameSet.Single() : null);
            var role = ui.Role switch
            {
                Role.user => ChatRole.User,
                Role.system => ChatRole.System,
                _ => ChatRole.Assistant
            };

            // Non-assistant roles: just map text parts into one ChatMessage
            if (role != ChatRole.Assistant)
            {
                List<AIContent> contents = [..ui.Parts?
                    .ToUserMessageParts() ?? []];

                yield return new ChatMessage(role, contents) { MessageId = ui.Id };
                continue;
            }

            // Assistant UIMessage: build assistant message content, and emit tool messages after when needed.
            // Agent Framework expects tool results as ChatRole.Tool messages, not assistant content.
            var mappedMessages = new List<ChatMessage>();
            var assistantContents = new List<AIContent>();

            void FlushAssistantContents()
            {
                if (assistantContents.Count == 0)
                    return;

                mappedMessages.Add(new ChatMessage(ChatRole.Assistant, [.. assistantContents]) { MessageId = ui.Id });
                assistantContents.Clear();
            }

            var parts = ui.Parts?.ToList() ?? [];

            for (var partIndex = 0; partIndex < parts.Count; partIndex++)
            {
                var part = parts[partIndex];

                switch (part)
                {
                    // normal assistant text
                    case TextUIPart t:
                        assistantContents.Add(new TextContent(t.Text ?? ""));
                        break;

                    case ReasoningUIPart reasoningEnd:
                        {
                            foreach (var kvp in reasoningEnd.ProviderMetadata ?? [])
                            {
                                var key = kvp.Key;

                                if (!activeAgentNameSet.Contains(key))
                                    continue;

                                if (kvp.Value is JsonElement json &&
                                    json.TryGetProperty("encrypted_content", out var encryptedProp))
                                {
                                    var encryptedContent = encryptedProp.GetString();

                                    assistantContents.Add(new TextReasoningContent(reasoningEnd.Text)
                                    {
                                        ProtectedData = encryptedContent
                                    });
                                }
                            }
                            break;
                        }

                    // Explicit approval envelope/control messages are transport-only,
                    // never executable tools in the agents runtime.
                    case ToolApprovalRequestUIPart:
                        break;

                    // If your UI has ToolCallPart separately (optional):
                    case ToolCallPart tc:
                        {
                            if (string.Equals(tc.ToolName, "approval-request", StringComparison.OrdinalIgnoreCase))
                                break;

                            var args = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                                JsonSerializer.Serialize(tc.Input)
                            ) ?? [];

                            var pairedOutput = partIndex + 1 < parts.Count && parts[partIndex + 1] is ToolOutputAvailablePart availableOutput
                                && string.Equals(availableOutput.ToolCallId, tc.ToolCallId, StringComparison.Ordinal)
                                ? availableOutput : null;
                            var continuationName = ResolveContinuationName(tc.ToolName, pairedOutput?.Output,
                                tc.ProviderMetadata, pairedOutput?.ProviderMetadata);
                            if (tc.ProviderExecuted == true && continuationName is not null
                                && activeAgentNameSet.Count > 0
                                && !activeAgentNameSet.Any(name => BelongsToAgent(ui, name,
                                    activeAgentNameSet, tc.ProviderMetadata, pairedOutput?.ProviderMetadata)))
                                break;

                            assistantContents.Add(new FunctionCallContent(tc.ToolCallId, continuationName ?? tc.ToolName, args)
                            {
                                InformationalOnly = tc.ProviderExecuted == true,
                                RawRepresentation = new Dictionary<string, object?>(CreateToolCallRawRepresentation(tc))
                                {
                                    ["agent_name"] = owner
                                }
                            });

                            if (partIndex + 1 < parts.Count && IsToolOutputPart(parts[partIndex + 1], tc.ToolCallId))
                            {
                                FlushAssistantContents();

                                mappedMessages.Add(new ChatMessage(
                                    ChatRole.Tool,
                                    [new FunctionResultContent(
                                        tc.ToolCallId,
                                        parts[partIndex + 1] is ToolOutputAvailablePart available
                                            ? new Dictionary<string, object?>(CreateToolOutputEnvelope(available))
                                            {
                                                ["agent_name"] = owner
                                            }
                                            : GetToolOutput(parts[partIndex + 1]) ?? new { })])
                                {
                                    MessageId = tc.ToolCallId
                                });

                                partIndex++;
                            }

                            break;
                        }

                    // The important one: ToolInvocationPart => assistant call + tool result
                    case ToolInvocationPart ti:
                        {
                            var toolName = NormalizeToolName(ti.Type);
                            var continuationName = ResolveContinuationName(toolName, ti.Output,
                                ti.CallProviderMetadata, ti.ResultProviderMetadata);

                            // Approval control parts belong to the UI approval handshake.
                            // Agents auto-approve and never execute these as functions.
                            if (IsApprovalControlPart(ti, toolName)
                                || IsConnectMcpControlPart(ti, toolName))
                                break;

                            // Dynamic provider-executed UI parts are descriptive
                            // artifacts, never client function calls. Their exact
                            // native identity is not recoverable unless the standard
                            // ToolCallPart path carried a Responses item.
                            if (ti.ProviderExecuted == true && continuationName is null)
                                break;

                            if (ti.ProviderExecuted == true && continuationName is not null
                                && activeAgentNameSet.Count > 0
                                && !activeAgentNameSet.Any(name => BelongsToAgent(ui, name, activeAgentNameSet,
                                    ti.CallProviderMetadata, ti.ResultProviderMetadata)))
                                break;

                            var args = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                                JsonSerializer.Serialize(ti.Input)
                            ) ?? [];

                            // 1) assistant function call


                            // 2) tool function result as separate tool-role message only when concrete output exists.
                            // If output is not present yet (approval-requested/approval-responded flow),
                            // let the agents runtime execute the tool call.
                            if (HasConcreteOutput(ti.Output)
                                || string.Equals(ti.State, "output-available", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(ti.State, "output-error", StringComparison.OrdinalIgnoreCase))
                            {
                                assistantContents.Add(new FunctionCallContent(ti.ToolCallId, continuationName ?? toolName, args)
                                {
                                    InformationalOnly = ti.ProviderExecuted == true,
                                    RawRepresentation = new Dictionary<string, object?>
                                    {
                                        ["provider_metadata"] = ScopeMetadata(ti.CallProviderMetadata, owner),
                                        ["title"] = ti.Title,
                                        ["agent_name"] = owner
                                    }
                                });

                                FlushAssistantContents();

                                mappedMessages.Add(new ChatMessage(
                                    ChatRole.Tool,
                                    [new FunctionResultContent(ti.ToolCallId,
                                        ti.ProviderExecuted == true
                                            ? new Dictionary<string, object?>
                                            {
                                                ["output"] = ti.Output ?? new { },
                                                ["provider_executed"] = true,
                                                ["provider_metadata"] = ScopeMetadata(ti.ResultProviderMetadata, owner),
                                                ["agent_name"] = owner
                                            }
                                            : ti.Output ?? new { })])
                                {
                                    MessageId = ti.ToolCallId
                                });
                            }

                            break;
                        }
                }
            }

            FlushAssistantContents();

            foreach (var mappedMessage in mappedMessages)
                yield return mappedMessage;
        }
    }

}
