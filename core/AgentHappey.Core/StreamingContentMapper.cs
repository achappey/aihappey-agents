
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;
using System.Net.Mime;
using AgentHappey.Common.Extensions;
using AIHappey.Vercel.Models;

namespace AgentHappey.Core;

public interface IStreamingContentMapper
{
    IAsyncEnumerable<UIMessagePart> MapAsync(
        IAsyncEnumerable<AgentResponseUpdate> updates,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<UIMessagePart> MapAsync(
        IAsyncEnumerable<WorkflowEvent> updates,
        CancellationToken cancellationToken = default);
}

public static class StreamWriterExtensions
{
    public static async Task WritePartsAsync(
        this HttpResponse response,
        IAsyncEnumerable<UIMessagePart> parts,
        CancellationToken ct = default)
    {
        await foreach (var p in parts.WithCancellation(ct))
        {
            string json = JsonSerializer.Serialize(p, JsonSerializerOptions.Web);
            await response.WriteAsync($"data: {json}\n\n", ct);
            await response.Body.FlushAsync(ct);
        }
    }
}

public sealed class StreamingContentMapper : IStreamingContentMapper
{
    private static readonly JsonSerializerOptions JsonWeb = JsonSerializerOptions.Web;

    public async IAsyncEnumerable<UIMessagePart> MapAsync(
        IAsyncEnumerable<AgentResponseUpdate> updates,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var text = new TextStreamState();
        var reasoning = new ReasoningStreamState();
        var pendingCalls = new Dictionary<string, ToolCallPart>(StringComparer.Ordinal);
        string? authorName = null;

        await foreach (var update in updates.WithCancellation(cancellationToken))
        {
            if (!string.IsNullOrWhiteSpace(update.AuthorName))
                authorName = update.AuthorName;

            foreach (var content in update.Contents)
                foreach (var part in MapContent(content, update.MessageId, pendingCalls, text, reasoning, includeFileParts: true))
                    yield return part;
        }

        foreach (var part in CloseAndFinish(text, authorName))
            yield return part;
    }

    public async IAsyncEnumerable<UIMessagePart> MapAsync(
        IAsyncEnumerable<WorkflowEvent> updates,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var text = new TextStreamState();
        var reasoning = new ReasoningStreamState();
        var pendingCalls = new Dictionary<string, ToolCallPart>(StringComparer.Ordinal);
        HashSet<string> authorNames = [];

        await foreach (var update in updates.WithCancellation(cancellationToken))
        {
            if (update is AgentResponseUpdateEvent agentRunUpdateEvent)
            {
                var u = agentRunUpdateEvent.Update;

                if (!string.IsNullOrWhiteSpace(u.AuthorName))
                    authorNames.Add(u.AuthorName);

                foreach (var content in u.Contents)
                    foreach (var part in MapContent(content, u.MessageId, pendingCalls, text, reasoning, includeFileParts: false))
                        yield return part;
            }
            else if (update is WorkflowOutputEvent)
            {
                foreach (var part in CloseAllTextStreams(text))
                    yield return part;
            }
            else if (update is WorkflowErrorEvent || update is ExecutorFailedEvent)
            {
                if (update.Data is Exception exception)
                    yield return new ErrorUIPart()
                    {
                        ErrorText = exception.Message
                    };
            }
        }

        foreach (var part in CloseAllTextStreams(text))
            yield return part;

        yield return new FinishUIPart
        {
            FinishReason = "stop",
            MessageMetadata = new Dictionary<string, object>
            {
                { "timestamp", DateTime.UtcNow }
            }
        };
    }

    private static IEnumerable<UIMessagePart> MapContent(
        object content,
        string? messageId,
        Dictionary<string, ToolCallPart> pendingCalls,
        TextStreamState text,
        ReasoningStreamState reasoning,
        bool includeFileParts)
    {
        switch (content)
        {
            case UsageContent usageContent:
                yield return new MessageMetadataUIPart
                {
                    MessageMetadata = new Dictionary<string, object>
                    {
                        { "totalTokens", usageContent.Details.TotalTokenCount ?? 0 },
                        { "inputTokens", usageContent.Details.InputTokenCount ?? 0},
                        { "outputTokens", usageContent.Details.OutputTokenCount ?? 0}
                    }
                };

                break;
            case UriContent uriContent:
                yield return new SourceUIPart
                {
                    Url = uriContent.Uri.ToString(),
                    SourceId = uriContent.Uri.ToString(),
                    Title = uriContent.Uri.ToString(),
                };
                break;
            case ErrorContent errorContent:
                yield return new ErrorUIPart
                {
                    ErrorText = errorContent.Message
                };
                break;
            case TextContent t:
                {
                    if (!string.IsNullOrEmpty(t.Text)
                        && t.Annotations?.Any() != true
                        && !string.IsNullOrEmpty(messageId))
                    {
                        foreach (var part in EnsureTextStream(messageId!, text))
                            yield return part;

                        yield return new TextDeltaUIMessageStreamPart { Id = messageId!, Delta = t.Text };
                    }

                    foreach (var ann in t.Annotations?.OfType<CitationAnnotation>() ?? [])
                    {
                        var url = ann.Url?.ToString();
                        if (string.IsNullOrEmpty(url)) continue;

                        yield return new SourceUIPart
                        {
                            SourceId = t.Text, // as in your original code
                            Url = url,
                            Title = ann.Title
                        };
                    }

                    break;
                }

            case DataContent d when includeFileParts:
                {
                    if (d.MediaType.StartsWith("image/") && !string.IsNullOrEmpty(d.Uri))
                        yield return new FileUIPart { MediaType = d.MediaType, Url = d.Uri };

                    if (d.MediaType.Equals(MediaTypeNames.Application.Json, StringComparison.InvariantCultureIgnoreCase))
                        yield return d.ToDataUIPart();

                    break;
                }

            case TextReasoningContent textReasoningContent:
                {
                    if (!string.IsNullOrEmpty(textReasoningContent.Text) && !string.IsNullOrEmpty(messageId))
                    {
                        foreach (var part in EnsureReasoningStream(messageId!, reasoning))
                            yield return part;

                        yield return new ReasoningDeltaUIPart { Id = messageId!, Delta = textReasoningContent.Text };
                    }

                    break;
                }

            case FunctionCallContent fc:
                {
                    if (string.IsNullOrEmpty(fc.CallId)) yield break;

                    var call = CreateToolCallPart(fc);
                    pendingCalls[fc.CallId!] = call;
                    yield return call;
                    break;
                }

            case FunctionResultContent fr:
                {
                    if (string.IsNullOrEmpty(fr.CallId)) yield break;
                    if (!pendingCalls.TryGetValue(fr.CallId!, out _)) yield break;

                    var output = fr.Result is AIContent aiContent
                        && aiContent.RawRepresentation is ContentBlock contentBlock
                        ? new CallToolResult()
                        {
                            Content = [contentBlock],
                        } : fr.Result ?? new { };

                    yield return new ToolOutputAvailablePart
                    {
                        ToolCallId = fr.CallId,
                        ProviderExecuted = true,
                        Output = output
                    };

                    var callToolResult = TryDeserializeCallToolResult(output);
                    if (callToolResult is null) yield break;

                    foreach (var part in MapCallToolResultSources(callToolResult))
                        yield return part;

                    break;
                }
            default:
                break;
        }
    }

    /*
        public static AIContent? MapContent(
            this object content)
        {
            switch (content)
            {
                case AIContent usageContent:
                    return usageContent;

                default:
                    return null;
            }
        }
    */
    private sealed class TextStreamState
    {
        public HashSet<string> OpenSet { get; } = new(StringComparer.Ordinal);
        public List<string> OpenOrder { get; } = []; // stable end-order
    }

    private sealed class ReasoningStreamState
    {
        public HashSet<string> OpenSet { get; } = new(StringComparer.Ordinal);
        public List<string> OpenOrder { get; } = []; // stable end-order
    }

    private static IEnumerable<UIMessagePart> EnsureTextStream(string messageId, TextStreamState text)
    {
        if (text.OpenSet.Add(messageId))
        {
            text.OpenOrder.Add(messageId);
            yield return new TextStartUIMessageStreamPart { Id = messageId };
        }
    }

    private static IEnumerable<UIMessagePart> EnsureReasoningStream(string messageId, ReasoningStreamState text)
    {
        if (text.OpenSet.Add(messageId))
        {
            text.OpenOrder.Add(messageId);
            yield return new ReasoningStartUIPart { Id = messageId };
        }
    }

    private static IEnumerable<UIMessagePart> CloseAllReasoningStreams(ReasoningStreamState text)
    {
        foreach (var id in text.OpenOrder)
            yield return new ReasoningEndUIPart { Id = id };

        text.OpenSet.Clear();
        text.OpenOrder.Clear();
    }


    private static IEnumerable<UIMessagePart> CloseAllTextStreams(TextStreamState text)
    {
        foreach (var id in text.OpenOrder)
            yield return new TextEndUIMessageStreamPart { Id = id };

        text.OpenSet.Clear();
        text.OpenOrder.Clear();
    }

    private static IEnumerable<UIMessagePart> CloseAndFinish(TextStreamState text, string? author)
    {
        foreach (var part in CloseAllTextStreams(text))
            yield return part;

        yield return new FinishUIPart
        {
            FinishReason = "stop",
            MessageMetadata = new Dictionary<string, object>
                {
                    { "timestamp", DateTime.UtcNow },
                    { "author", author ?? string.Empty }
                }
        };
    }
    /*
        private static ToolCallPart CreateToolCallPart(McpServerToolCallContent fc)
        {
            var inputPayload = fc.Arguments ?? new Dictionary<string, object?>();

            var normalizedInput = JsonSerializer.Deserialize<object>(
                JsonSerializer.Serialize(inputPayload, JsonWeb))!;

            return new ToolCallPart
            {
                ToolCallId = fc.CallId!,
                ProviderExecuted = true,
                ToolName = fc.ToolName,
                //    Type = $"tool-{fc.Name}",
                Input = normalizedInput
            };
        }*/


    private static ToolCallPart CreateToolCallPart(FunctionCallContent fc)
    {
        var inputPayload = fc.Arguments ?? new Dictionary<string, object?>();

        var normalizedInput = JsonSerializer.Deserialize<object>(
            JsonSerializer.Serialize(inputPayload, JsonWeb))!;

        return new ToolCallPart
        {
            ToolCallId = fc.CallId!,
            ProviderExecuted = true,
            ToolName = fc.Name,
            //    Type = $"tool-{fc.Name}",
            Input = normalizedInput
        };
    }

    private static CallToolResult? TryDeserializeCallToolResult(object? result)
    {
        if (result is null) return null;

        try
        {
            return JsonSerializer.Deserialize<CallToolResult>(
                JsonSerializer.Serialize(result, JsonWeb));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IEnumerable<UIMessagePart> MapCallToolResultSources(CallToolResult callToolResult)
    {
        foreach (var item in callToolResult.Content)
        {
            if (item is EmbeddedResourceBlock erb &&
                erb.Resource is TextResourceContents trc &&
                !string.IsNullOrEmpty(trc.Uri))
            {
                yield return new SourceUIPart { SourceId = trc.Uri, Url = trc.Uri };
            }
            else if (item is ResourceLinkBlock rlb && !string.IsNullOrEmpty(rlb.Uri))
            {
                yield return new SourceUIPart { SourceId = rlb.Uri, Url = rlb.Uri, Title = rlb.Name };
            }
        }
    }
}