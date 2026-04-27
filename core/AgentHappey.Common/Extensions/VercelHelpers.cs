using System.Text.Json;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.AI;

namespace AgentHappey.Common.Extensions;

public static class VercelHelpers
{
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

                            assistantContents.Add(new FunctionCallContent(tc.ToolCallId, tc.ToolName, args));

                            if (partIndex + 1 < parts.Count && IsToolOutputPart(parts[partIndex + 1], tc.ToolCallId))
                            {
                                FlushAssistantContents();

                                mappedMessages.Add(new ChatMessage(
                                    ChatRole.Tool,
                                    [new FunctionResultContent(tc.ToolCallId, GetToolOutput(parts[partIndex + 1]) ?? new { })])
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

                            // Approval control parts belong to the UI approval handshake.
                            // Agents auto-approve and never execute these as functions.
                            if (IsApprovalControlPart(ti, toolName)
                                || IsConnectMcpControlPart(ti, toolName))
                                break;

                            var args = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                                JsonSerializer.Serialize(ti.Input)
                            ) ?? [];

                            // 1) assistant function call
                            assistantContents.Add(new FunctionCallContent(ti.ToolCallId, toolName, args));

                            // 2) tool function result as separate tool-role message only when concrete output exists.
                            // If output is not present yet (approval-requested/approval-responded flow),
                            // let the agents runtime execute the tool call.
                            if (HasConcreteOutput(ti.Output)
                                || string.Equals(ti.State, "output-available", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(ti.State, "output-error", StringComparison.OrdinalIgnoreCase))
                            {
                                FlushAssistantContents();

                                mappedMessages.Add(new ChatMessage(
                                    ChatRole.Tool,
                                    [new FunctionResultContent(ti.ToolCallId, ti.Output ?? new { })])
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
