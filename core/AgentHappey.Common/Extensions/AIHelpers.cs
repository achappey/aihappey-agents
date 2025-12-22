using System.Text;
using System.Text.Json;
using AIHappey.Common.Model;
using Microsoft.Extensions.AI;

namespace AgentHappey.Common.Extensions;

public static class AIHelpers
{
    public static string GetDataUIPartType(this DataContent dataContent) =>
      dataContent.Name != null ? dataContent.Name.StartsWith("data-") ?
                               dataContent.Name.Length > 5 ? dataContent.Name
                               : $"{dataContent.Name}unknown"
                               : $"data-{dataContent.Name}"
                               : "data-unknown";

    public static DataUIPart ToDataUIPart(this DataContent dataContent) =>
        new()
        {
            Data = JsonSerializer.Deserialize<object>(Encoding.UTF8.GetString(dataContent.Data.ToArray()))!,
            Type = dataContent.GetDataUIPartType()
        };

    public static IEnumerable<UIMessage> ToMessages(this IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages)
    {
        UIMessage? pendingAssistant = null;
        List<UIMessagePart> pendingParts = [];
        Dictionary<string, ToolInvocationPart> pendingToolsById = [];

        void EnsurePendingAssistant(string? id = null)
        {
            if (pendingAssistant is not null) return;

            pendingAssistant = new UIMessage
            {
                Role = Role.assistant,
                Id = id ?? Guid.NewGuid().ToString(),
                Parts = [.. Array.Empty<UIMessagePart>()]
            };

            pendingParts = [];
            pendingToolsById = [];
        }

        UIMessage? TakePendingAssistant()
        {
            if (pendingAssistant is null) return null;

            var result = new UIMessage
            {
                Id = pendingAssistant.Id,
                Role = pendingAssistant.Role,
                Parts = [.. pendingParts]
            };

            pendingAssistant = null;
            pendingParts = [];
            pendingToolsById = [];

            return result;
        }

        static object NormalizeArgs(object? args)
        {
            if (args is null) return new { };

            // Keep it robust when args is JsonElement / JsonNode / dictionary / etc.
            try
            {
                return JsonSerializer.Deserialize<object>(
                           JsonSerializer.Serialize(args, JsonSerializerOptions.Web)
                       ) ?? new { };
            }
            catch
            {
                // Worst case: still return something serializable/inspectable.
                return args;
            }
        }

        foreach (var msg in messages)
        {
            if (msg.Contents.Count == 0)
                continue;

            // Fold TOOL messages into the current pending assistant turn.
            if (msg.Role == ChatRole.Tool)
            {
                EnsurePendingAssistant();

                foreach (var fr in msg.Contents.OfType<FunctionResultContent>())
                {
                    var callId = fr.CallId;

                    // If we already saw the call, replace that part in-place (preserve ordering)
                    if (pendingToolsById.TryGetValue(callId, out var existing))
                    {
                        var updated = new ToolInvocationPart
                        {
                            ToolCallId = existing.ToolCallId,
                            Type = existing.Type,
                            Input = existing.Input ?? new { },
                            Output = fr.Result ?? new { },
                            ProviderExecuted = true
                        };

                        var idx = pendingParts.FindIndex(p => p is ToolInvocationPart t && t.ToolCallId == callId);
                        if (idx >= 0) pendingParts[idx] = updated;
                        else pendingParts.Add(updated);

                        pendingToolsById[callId] = updated;
                    }
                }

                // Optional: if tool messages have text parts, fold them into assistant too
                foreach (var p in msg.Contents.Select(c => c.ToUiPart()).OfType<UIMessagePart>())
                    pendingParts.Add(p);

                continue;
            }

            // KEEP assistant open across assistant→assistant messages.
            if (msg.Role == ChatRole.Assistant)
            {
                EnsurePendingAssistant(msg.MessageId);

                foreach (var c in msg.Contents)
                {
                    if (c is FunctionCallContent fc)
                    {
                        // Ideally CallId is always present; if not, we can’t match later tool results anyway,
                        // but we still keep a stable placeholder in the UI.
                        var callId = fc.CallId ?? Guid.NewGuid().ToString();

                        // If duplicates happen, don’t add twice.
                        if (!pendingToolsById.TryGetValue(callId, out var existing))
                        {
                            var tip = new ToolInvocationPart
                            {
                                ToolCallId = callId,
                                Type = $"tool-{fc.Name}",
                                Input = NormalizeArgs(fc.Arguments),
                                Output = new { },              // filled when tool result arrives
                                ProviderExecuted = false       // IMPORTANT: call is not executed yet
                            };

                            pendingToolsById[callId] = tip;
                            pendingParts.Add(tip);
                        }
                        else
                        {
                            // If the assistant repeats the same callId, ignore duplicates (or update input if you prefer).
                            // pendingToolsById[callId] = existing;
                        }

                        continue;
                    }

                    var uiPart = c.ToUiPart();
                    if (uiPart is not null)
                        pendingParts.Add(uiPart);
                }

                continue;
            }

            // USER / SYSTEM boundary: flush the whole pending assistant turn here.
            var flushed = TakePendingAssistant();
            if (flushed is not null)
                yield return flushed;

            yield return new UIMessage
            {
                Role = msg.Role == ChatRole.User ? Role.user : Role.system,
                Id = msg.MessageId ?? Guid.NewGuid().ToString(),
                Parts = [.. msg.Contents.Select(t => t.ToUiPart()).OfType<UIMessagePart>()]
            };
        }

        // end: flush pending assistant (if any)
        var last = TakePendingAssistant();
        if (last is not null)
            yield return last;
    }

    public static UIMessagePart ToTextUIPart(this TextContent textContent) => textContent.Text.ToTextUIPart();

    public static UIMessagePart ToTextUIPart(this string textContent) => new TextUIPart { Text = textContent };

    public static string WithHeader(this string textContent, string header) => $"{header}\n{textContent}";

    public static UIMessagePart ToFileUIPart(this DataContent dataContent) => new FileUIPart
    {
        Url = dataContent.Uri,
        MediaType = dataContent.MediaType
    };

    public static UIMessagePart? ToUiPart(this AIContent aIContent) => aIContent switch
    {
        TextContent t => t.Annotations?.Any() != true ? t.ToTextUIPart() : null,
       // DataContent t => t.ToFileUIPart(),
        _ => null
    };

}
