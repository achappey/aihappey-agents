using System.Text;
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

    public static AIContent? ToUserMessagePart(this UIMessagePart message)
    {
        return message switch
        {
            TextUIPart textUIPart => new TextContent(textUIPart.Text ?? ""),
            FileUIPart fileUIPart => new DataContent(fileUIPart.Url, fileUIPart.MediaType),
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

            // Assistant UIMessage: build assistant message content, and emit tool messages after when needed
            var assistantContents = new List<AIContent>();
            var toolMessages = new List<ChatMessage>();
            var reasoningById = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);

            foreach (var part in ui.Parts ?? [])
            {
                switch (part)
                {
                    // normal assistant text
                    case TextUIPart t:
                        assistantContents.Add(new TextContent(t.Text ?? ""));
                        break;

                    // streaming delta treated as assistant text
                    case TextDeltaUIMessageStreamPart td:
                        assistantContents.Add(new TextContent(td.Delta ?? ""));
                        break;

                    case ReasoningDeltaUIPart reasoningDelta when !string.IsNullOrWhiteSpace(reasoningDelta.Id):
                        {
                            if (!reasoningById.TryGetValue(reasoningDelta.Id!, out var builder))
                            {
                                builder = new StringBuilder();
                                reasoningById[reasoningDelta.Id!] = builder;
                            }

                            builder.Append(reasoningDelta.Delta ?? string.Empty);
                            break;
                        }

                    case ReasoningEndUIPart reasoningEnd:
                        {
                            if (!TryGetMatchingReasoningMetadata(reasoningEnd, activeAgentNameSet, out var metadata))
                                break;

                            var reasoningText = !string.IsNullOrWhiteSpace(reasoningEnd.Id)
                                && reasoningById.TryGetValue(reasoningEnd.Id!, out var builder)
                                    ? builder.ToString()
                                    : string.Empty;

                            metadata.TryGetValue("encrypted_content", out var protectedDataValue);
                            var protectedData = protectedDataValue as string;

                            if (string.IsNullOrWhiteSpace(reasoningText) && string.IsNullOrWhiteSpace(protectedData))
                                break;

                            assistantContents.Add(new TextReasoningContent(reasoningText)
                            {
                                ProtectedData = protectedData
                            });
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
                            break;
                        }

                    // The important one: ToolInvocationPart => assistant call + tool result
                    case ToolInvocationPart ti:
                        {
                            var toolName = NormalizeToolName(ti.Type);

                            // Approval control parts belong to the UI approval handshake.
                            // Agents auto-approve and never execute these as functions.
                            if (IsApprovalControlPart(ti, toolName))
                                break;

                            var args = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                                JsonSerializer.Serialize(ti.Input)
                            ) ?? [];

                            // 1) assistant function call
                            assistantContents.Add(new FunctionCallContent(ti.ToolCallId, toolName, args));

                            // 2) tool function result (separate message) only when concrete output exists.
                            // If output is not present yet (approval-requested/approval-responded flow),
                            // let the agents runtime execute the tool call.
                            if (HasConcreteOutput(ti.Output)
                                || string.Equals(ti.State, "output-available", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(ti.State, "output-error", StringComparison.OrdinalIgnoreCase))
                            {
                                toolMessages.Add(new ChatMessage(ChatRole.Tool,
                                [
                                    new FunctionResultContent(ti.ToolCallId, ti.Output ?? new { })
                                ]));
                            }

                            break;
                        }
                }
            }

            // emit assistant message first (even if only tool calls)
            yield return new ChatMessage(ChatRole.Assistant, assistantContents) { MessageId = ui.Id };

            // then emit tool results (in-order)
            foreach (var tm in toolMessages)
                yield return tm;
        }
    }

    private static bool TryGetMatchingReasoningMetadata(
        ReasoningEndUIPart reasoningEnd,
        HashSet<string> activeAgentNames,
        out Dictionary<string, object> metadata)
    {
        metadata = [];

        if (activeAgentNames.Count == 0 || reasoningEnd.ProviderMetadata is not { Count: > 0 } providerMetadata)
            return false;

        foreach (var agentName in activeAgentNames)
        {
            if (providerMetadata.TryGetValue(agentName, out var value) && value is { Count: > 0 })
            {
                metadata = value;
                return true;
            }
        }

        return false;
    }

}
