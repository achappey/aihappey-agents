
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Model;
using Microsoft.Extensions.AI;

namespace AgentHappey.Common.Extensions;

public static class VercelHelpers
{
    public static IEnumerable<ChatMessage> ToMessages(this IEnumerable<UIMessage> messages)
    {
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
                    .OfType<TextUIPart>()
                    .Select(p => new TextContent(p.Text ?? ""))
                    .ToList() ?? []];

                yield return new ChatMessage(role, contents) { MessageId = ui.Id };
                continue;
            }

            // Assistant UIMessage: build assistant message content, and emit tool messages after when needed
            var assistantContents = new List<AIContent>();
            var toolMessages = new List<ChatMessage>();

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

                    // If your UI has ToolCallPart separately (optional):
                    case ToolCallPart tc:
                        {
                            var args = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                                JsonSerializer.Serialize(tc.Input)
                            ) ?? [];

                            assistantContents.Add(new FunctionCallContent(tc.ToolCallId, tc.ToolName, args));
                            break;
                        }

                    // The important one: ToolInvocationPart => assistant call + tool result
                    case ToolInvocationPart ti:
                        {
                            var toolName = ti.Type?.StartsWith("tool-") == true
                                ? ti.Type["tool-".Length..]
                                : (ti.Type ?? "unknown");

                            var args = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                                JsonSerializer.Serialize(ti.Input)
                            ) ?? [];

                            // 1) assistant function call
                            assistantContents.Add(new FunctionCallContent(ti.ToolCallId, toolName, args));

                            // 2) tool function result (separate message)
                            toolMessages.Add(new ChatMessage(ChatRole.Tool,
                            [
                                new FunctionResultContent(ti.ToolCallId, ti.Output ?? new { })
                            ]));

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

    public static UsageDetails ToUsageDetails(this Dictionary<string, object> keyValuePairs) =>
          new()
          {
              TotalTokenCount = keyValuePairs.TryGetValue("totalTokens", out object? totalTokens) ?
                          long.Parse(totalTokens.ToString()!) : null,
              InputTokenCount = keyValuePairs.TryGetValue("inputTokens", out object? inputTokens) ?
                          long.Parse(inputTokens.ToString()!) : null,
              OutputTokenCount = keyValuePairs.TryGetValue("outputTokens", out object? outputTokens) ?
                          long.Parse(outputTokens.ToString()!) : null
          };

    public static bool HasMetadata(this FinishUIPart finishUIPart) =>
             finishUIPart.MessageMetadata != null;

    public static ChatFinishReason? GetChatFinishReason(this FinishUIPart finishUIPart) =>
        !string.IsNullOrEmpty(finishUIPart.FinishReason)
            ? new ChatFinishReason(finishUIPart.FinishReason) : null;

    public static UsageContent? ToUsageContent(this FinishUIPart finishUIPart) =>
         finishUIPart.MessageMetadata != null ? new()
         {
             Details = finishUIPart.MessageMetadata.ToUsageDetails()
         } : null;

    public static IEnumerable<ChatResponseUpdate>? ToChatResponseUpdates(this UIMessagePart part,
        string modelId, string authorName)
    {
        if (part is ToolCallDeltaPart)
            return null;

        if (part is DataUIPart dataUIPart)
            return One(new ChatResponseUpdate(ChatRole.Assistant,
                [new DataContent(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(dataUIPart.Data)),
                    MediaTypeNames.Application.Json) {
                    Name = dataUIPart.Type
            }])
            {
                AuthorName = authorName,
                ModelId = modelId
            });


        if (part is SourceUIPart sourceUIPart)
            return One(new ChatResponseUpdate(ChatRole.Assistant,
                [new TextContent(sourceUIPart.SourceId) {
                    Annotations = [new CitationAnnotation() {
                        Title = sourceUIPart.Title,
                        Url = new Uri(sourceUIPart.Url)
                }]
            }])
            {
                AuthorName = authorName,
                ModelId = modelId
            });

        if (part is ReasoningDeltaUIPart reasoningDeltaUIPart)
            return One(new ChatResponseUpdate(ChatRole.Assistant,
                [new TextReasoningContent(reasoningDeltaUIPart.Delta)])
            {
                MessageId = reasoningDeltaUIPart.Id,
                AuthorName = authorName,
                ModelId = modelId
            });

        if (part is TextDeltaUIMessageStreamPart td)
            return One(new ChatResponseUpdate(ChatRole.Assistant,
                 [new TextContent(td.Delta)])
            {
                MessageId = td.Id,
                AuthorName = authorName,
                ModelId = modelId
            });

        if (part is FinishUIPart finishUIPart)
            return One(new ChatResponseUpdate(ChatRole.Assistant, finishUIPart.HasMetadata()
                ? [finishUIPart.ToUsageContent()!] : [])
            {
                MessageId = Guid.NewGuid().ToString(),
                FinishReason = finishUIPart.GetChatFinishReason(),
                AuthorName = authorName,
                ModelId = modelId
            });

        if (part is FileUIPart fileUIPart)
            return One(new ChatResponseUpdate(ChatRole.Assistant,
                [new DataContent(fileUIPart.Url, fileUIPart.MediaType)])
            {
                MessageId = Guid.NewGuid().ToString(),
                AuthorName = authorName,
                ModelId = modelId
            });

        if (part is SourceDocumentPart sourceDocumentPart)
            return One(new ChatResponseUpdate(ChatRole.Assistant,
                [new UriContent(sourceDocumentPart.SourceId, sourceDocumentPart.MediaType)])
            {
                MessageId = Guid.NewGuid().ToString(),
                AuthorName = authorName,
                ModelId = modelId
            });

        if (part is ToolCallPart tc)
        {
            if (tc.ProviderExecuted == true)
                return One(new ChatResponseUpdate(ChatRole.Assistant,
                    [new DataContent(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(tc)), MediaTypeNames.Application.Json)])
                {
                    MessageId = Guid.NewGuid().ToString(),
                    AuthorName = authorName,
                    ModelId = modelId
                });

            var args = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                JsonSerializer.Serialize(tc.Input)
            ) ?? [];

            return One(new ChatResponseUpdate(
                ChatRole.Assistant,
                [new FunctionCallContent(tc.ToolCallId, tc.ToolName, args)]
            )
            {
                MessageId = Guid.NewGuid().ToString(),
                AuthorName = authorName,
                ModelId = modelId
            });
        }

        if (part is ToolOutputAvailablePart toolOutputAvailablePart)
        {
            if (toolOutputAvailablePart.ProviderExecuted == true)
                return One(new ChatResponseUpdate(ChatRole.Assistant,
                      [new DataContent(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(toolOutputAvailablePart)), MediaTypeNames.Application.Json)])
                {
                    MessageId = Guid.NewGuid().ToString(),
                    AuthorName = authorName,
                    ModelId = modelId
                }); ;

            return One(new ChatResponseUpdate(ChatRole.Tool,
                    [new FunctionResultContent(toolOutputAvailablePart.ToolCallId, toolOutputAvailablePart.Output ?? new { })])
            {
                MessageId = Guid.NewGuid().ToString(),
                AuthorName = authorName,
                ModelId = modelId
            });
        }

        if (part is ToolInvocationPart ti)
        {
            var toolName = ti.Type.Replace("tool-", "");

            var args = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                JsonSerializer.Serialize(ti.Input)
            ) ?? [];

            return Two(
                new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent(ti.ToolCallId, toolName, args)])
                {
                    MessageId = Guid.NewGuid().ToString(),
                    AuthorName = authorName,
                    ModelId = modelId
                },
                new ChatResponseUpdate(ChatRole.Tool,
                    [new FunctionResultContent(ti.ToolCallId, ti.Output ?? new { })])
                {
                    MessageId = Guid.NewGuid().ToString(),
                    AuthorName = authorName,
                    ModelId = modelId
                }
            );
        }

        return null;

        static IEnumerable<ChatResponseUpdate> One(ChatResponseUpdate a)
        {
            yield return a;
        }

        static IEnumerable<ChatResponseUpdate> Two(ChatResponseUpdate a, ChatResponseUpdate b)
        {
            yield return a; // call first
            yield return b; // result second
        }
    }
}
