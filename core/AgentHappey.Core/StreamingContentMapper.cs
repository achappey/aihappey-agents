
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Linq;
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

        List<UsageContent> usageContents = [];

        await foreach (var update in updates.WithCancellation(cancellationToken))
        {
            if (!string.IsNullOrWhiteSpace(update.AuthorName))
                authorName = update.AuthorName;

            foreach (var content in update.Contents)
            {
                if (content is UsageContent usageContent)
                    usageContents.Add(usageContent);
                else
                    foreach (var part in MapContent(content, update.MessageId, update.AuthorName, pendingCalls, text, reasoning, includeFileParts: true))
                        yield return part;
            }
        }

        foreach (var part in CloseAndFinish(text, reasoning, usageContents, authorName))
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
                    foreach (var part in MapContent(content, u.MessageId, u.AuthorName, pendingCalls, text, reasoning, includeFileParts: false))
                        yield return part;
            }
            else if (update is WorkflowOutputEvent)
            {
                foreach (var part in CloseAllReasoningStreams(reasoning))
                    yield return part;

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

        foreach (var part in CloseAllReasoningStreams(reasoning))
            yield return part;

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
        string? authorName,
        Dictionary<string, ToolCallPart> pendingCalls,
        TextStreamState text,
        ReasoningStreamState reasoning,
        bool includeFileParts)
    {
        switch (content)
        {
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
                            SourceId = ann.Title ?? url, // as in your original code
                            Url = url,
                            Title = ann.Title
                        };
                    }

                    break;
                }

            case DataContent d when includeFileParts:
                {
                    if (!string.IsNullOrEmpty(d.Uri))
                        yield return new FileUIPart
                        {
                            MediaType = d.MediaType,
                            Url = d.Uri,
                            ProviderMetadata = !string.IsNullOrEmpty(authorName) &&
                              !string.IsNullOrEmpty(d.Name) ? new Dictionary<string, Dictionary<string, object>?>()
                            {
                                {authorName, new Dictionary<string, object>()
                                {
                                    {"filename", d.Name}
                                }
                                }
                            } : null
                        };

                    if (d.MediaType.Equals(MediaTypeNames.Application.Json, StringComparison.InvariantCultureIgnoreCase))
                        yield return d.ToDataUIPart();

                    break;
                }

            case TextReasoningContent textReasoningContent:
                {
                    if (string.IsNullOrEmpty(messageId))
                        break;

                    foreach (var part in EnsureReasoningStream(messageId!, reasoning))
                        yield return part;

                    if (!string.IsNullOrEmpty(textReasoningContent.Text))
                    {
                        yield return new ReasoningDeltaUIPart { Id = messageId!, Delta = textReasoningContent.Text };
                    }

                    if (string.IsNullOrEmpty(textReasoningContent.Text))
                    {
                        foreach (var part in CloseReasoningStream(messageId!, reasoning, textReasoningContent.ProtectedData, authorName))
                            yield return part;
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
                    if (!pendingCalls.TryGetValue(fr.CallId!, out ToolCallPart? toolCallPart)) yield break;

                    var output = fr.Result is AIContent aiContent
                        && aiContent.RawRepresentation is ContentBlock contentBlock
                        ? new CallToolResult()
                        {
                            Content = [contentBlock],
                        } : fr.Result ?? new { };

                    if (fr.CallId.StartsWith("ws_") &&
                        toolCallPart.ToolName == "search" &&
                        fr.Result is Dictionary<string, JsonElement> dict &&
                        dict.TryGetValue("sources", out var sources) &&
                        sources.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in sources.EnumerateArray())
                        {
                            if (item.ValueKind != JsonValueKind.Object) continue;

                            if (!item.TryGetProperty("url", out var urlEl)) continue;
                            if (urlEl.ValueKind != JsonValueKind.String) continue;

                            var url = urlEl.GetString();
                            if (string.IsNullOrEmpty(url)) continue;

                            yield return new SourceUIPart
                            {
                                SourceId = url,
                                Url = url
                            };
                        }

                        var structured = JsonSerializer.SerializeToElement(new
                        {
                            sources
                        });

                        output = new CallToolResult()
                        {
                            StructuredContent = structured
                        };
                    }

                    if (fr.CallId.StartsWith("ci_") &&
                     toolCallPart.ToolName == "code_interpreter" &&
                     fr.Result is Dictionary<string, JsonElement> dictCi &&
                     dictCi.TryGetValue("outputs", out var outputs) &&
                     outputs.ValueKind == JsonValueKind.Array)
                    {
                        var structured = JsonSerializer.SerializeToElement(new
                        {
                            outputs
                        });

                        output = new CallToolResult()
                        {
                            StructuredContent = structured
                        };
                    }

                    var providerExecuted = true;
                    bool? preliminary = null;
                    Dictionary<string, Dictionary<string, object>?>? providerMetadata = null;

                    if (TryUnwrapToolOutputEnvelope(output, out var unwrappedOutput, out var envelopePreliminary, out var envelopeProviderExecuted, out var envelopeProviderMetadata))
                    {
                        output = unwrappedOutput ?? new { };
                        preliminary = envelopePreliminary;
                        providerExecuted = envelopeProviderExecuted;
                        providerMetadata = envelopeProviderMetadata;
                    }

                    yield return new ToolOutputAvailablePart
                    {
                        ToolCallId = fr.CallId,
                        ProviderExecuted = providerExecuted,
                        Preliminary = preliminary,
                        ProviderMetadata = providerMetadata,
                        Output = output
                    };

                    if (providerExecuted
                        && IsDownloadFileToolOutput(toolCallPart.ToolName, providerMetadata, authorName)
                        && TryCreateDownloadFilePart(output, providerMetadata, authorName, out var filePart))
                    {
                        yield return filePart;
                    }

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

    private static IEnumerable<UIMessagePart> CloseReasoningStream(
        string messageId,
        ReasoningStreamState text,
        string? protectedData,
        string? authorName)
    {
        if (!text.OpenSet.Remove(messageId))
            yield break;

        text.OpenOrder.Remove(messageId);
        var agentScopedProviderMetadata = ToAgentScopedProviderMetadata(protectedData, authorName);

        yield return new ReasoningEndUIPart
        {
            Id = messageId,
            ProviderMetadata = agentScopedProviderMetadata
        };
    }

    private static Dictionary<string, Dictionary<string, object>>? ToAgentScopedProviderMetadata(
        string? protectedData,
        string? authorName)
    {
        if (string.IsNullOrWhiteSpace(protectedData) || string.IsNullOrWhiteSpace(authorName))
            return null;

        return new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal)
        {
            [authorName] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["encrypted_content"] = protectedData
            }
        };
    }


    private static IEnumerable<UIMessagePart> CloseAllTextStreams(TextStreamState text)
    {
        foreach (var id in text.OpenOrder)
            yield return new TextEndUIMessageStreamPart { Id = id };

        text.OpenSet.Clear();
        text.OpenOrder.Clear();
    }

    private static IEnumerable<UIMessagePart> CloseAndFinish(
        TextStreamState text,
        ReasoningStreamState reasoning,
        List<UsageContent> usages,
        string? author)
    {
        foreach (var part in CloseAllReasoningStreams(reasoning))
            yield return part;

        foreach (var part in CloseAllTextStreams(text))
            yield return part;

        int totalTokens = (int)Math.Min(
            usages.Sum(a => a.Details.TotalTokenCount ?? 0L),
            int.MaxValue);

        int inputTokens = (int)Math.Min(
            usages.Sum(a => a.Details.InputTokenCount ?? 0L),
            int.MaxValue);

        int outputTokens = (int)Math.Min(
            usages.Sum(a => a.Details.OutputTokenCount ?? 0L),
            int.MaxValue);

        yield return new FinishUIPart
        {
            FinishReason = "stop",
            MessageMetadata = new Dictionary<string, object>
                {
                    { "timestamp", DateTime.UtcNow },
                    {"usage", new Usage()
                        {
                            TotalTokens = totalTokens,
                            CompletionTokens = outputTokens,
                            PromptTokens = inputTokens
                        }
                    },
                    { "author", author ?? string.Empty },
                    { "model", author ?? string.Empty }
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

        var raw = TryGetFunctionCallRawRepresentation(fc);
        var providerMetadata = raw is not null && raw.TryGetValue("provider_metadata", out var metadata)
            ? ExtractProviderMetadata(metadata)
            : null;
        var title = raw is not null && raw.TryGetValue("title", out var titleValue)
            ? titleValue?.ToString()
            : null;

        return new ToolCallPart
        {
            ToolCallId = fc.CallId!,
            ProviderExecuted = true,
            ToolName = fc.Name,
            Title = title,
            Input = normalizedInput,
            ProviderMetadata = providerMetadata
        };
    }

    private static Dictionary<string, object?>? TryGetFunctionCallRawRepresentation(FunctionCallContent fc)
    {
        if (fc.RawRepresentation is Dictionary<string, object?> nullableDictionary)
            return nullableDictionary;

        if (fc.RawRepresentation is Dictionary<string, object> dictionary)
            return dictionary.ToDictionary(entry => entry.Key, entry => (object?)entry.Value, StringComparer.Ordinal);

        if (fc.RawRepresentation is null)
            return null;

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(
                JsonSerializer.Serialize(fc.RawRepresentation, JsonWeb),
                JsonWeb);
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, Dictionary<string, object>?>? ExtractProviderMetadata(object? value)
    {
        if (value is null)
            return null;

        if (value is Dictionary<string, Dictionary<string, object>?> typed)
            return typed;

        if (value is Dictionary<string, Dictionary<string, object>> nonNullable)
            return nonNullable.ToDictionary(
                entry => entry.Key,
                entry => (Dictionary<string, object>?)entry.Value,
                StringComparer.Ordinal);

        try
        {
            return value is JsonElement json
                ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, object>?>>(json.GetRawText(), JsonWeb)
                : JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, object>?>>(
                    JsonSerializer.Serialize(value, JsonWeb),
                    JsonWeb);
        }
        catch
        {
            return null;
        }
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

    private static bool TryUnwrapToolOutputEnvelope(
        object? result,
        out object? output,
        out bool? preliminary,
        out bool providerExecuted,
        out Dictionary<string, Dictionary<string, object>?>? providerMetadata)
    {
        output = result;
        preliminary = null;
        providerExecuted = true;
        providerMetadata = null;

        JsonElement envelope;
        try
        {
            envelope = result switch
            {
                JsonElement jsonElement => jsonElement,
                null => default,
                _ => JsonSerializer.SerializeToElement(result, JsonWeb)
            };
        }
        catch
        {
            return false;
        }

        if (envelope.ValueKind != JsonValueKind.Object
            || !envelope.TryGetProperty("__aihappey_tool_output", out var marker)
            || marker.ValueKind != JsonValueKind.True)
        {
            return false;
        }

        output = envelope.TryGetProperty("output", out var outputElement)
            ? outputElement.Clone()
            : new { };

        if (envelope.TryGetProperty("preliminary", out var preliminaryElement)
            && preliminaryElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            preliminary = preliminaryElement.GetBoolean();
        }

        if (envelope.TryGetProperty("provider_executed", out var providerExecutedElement)
            && providerExecutedElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            providerExecuted = providerExecutedElement.GetBoolean();
        }

        if (envelope.TryGetProperty("provider_metadata", out var providerMetadataElement)
            && providerMetadataElement.ValueKind == JsonValueKind.Object)
        {
            providerMetadata = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, object>?>>(
                providerMetadataElement.GetRawText(),
                JsonWeb);
        }

        return true;
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

    private static bool IsDownloadFileToolOutput(
        string? toolName,
        Dictionary<string, Dictionary<string, object>?>? providerMetadata,
        string? providerId)
    {
        if (string.Equals(toolName, "download_file", StringComparison.OrdinalIgnoreCase))
            return true;

        if (providerMetadata is null || providerMetadata.Count == 0)
            return false;

        if (!string.IsNullOrWhiteSpace(providerId)
            && providerMetadata.TryGetValue(providerId, out var scoped)
            && IsDownloadFileProviderMetadata(scoped))
        {
            return true;
        }

        return providerMetadata.Values.Any(IsDownloadFileProviderMetadata);
    }

    private static bool IsDownloadFileProviderMetadata(Dictionary<string, object>? metadata)
        => metadata is not null
           && (HasMetadataValue(metadata, "name", "download_file")
               || HasMetadataValue(metadata, "tool_name", "download_file")
               || HasMetadataValue(metadata, "download_tool", true));

    private static bool TryCreateDownloadFilePart(
        object? output,
        Dictionary<string, Dictionary<string, object>?>? providerMetadata,
        string? providerId,
        out FileUIPart filePart)
    {
        filePart = default!;

        if (!TryExtractDownloadFilePayload(output, out var url, out var mediaType, out var filename, out var fileId)
            || string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        filePart = new FileUIPart
        {
            MediaType = mediaType ?? MediaTypeNames.Application.Octet,
            Url = url,
            ProviderMetadata = EnsureDownloadFileProviderMetadata(providerMetadata, providerId, filename, mediaType, fileId)
        };

        return true;
    }

    private static bool TryExtractDownloadFilePayload(
        object? output,
        out string? url,
        out string? mediaType,
        out string? filename,
        out string? fileId)
    {
        url = null;
        mediaType = null;
        filename = null;
        fileId = null;

        var callToolResult = TryDeserializeCallToolResult(output);
        JsonElement payload;
        if (callToolResult?.IsError != true
            && callToolResult?.StructuredContent is JsonElement structuredContent
            && structuredContent.ValueKind == JsonValueKind.Object)
        {
            payload = structuredContent;
        }
        else
        {
            try
            {
                payload = output switch
                {
                    JsonElement jsonElement => jsonElement,
                    null => default,
                    _ => JsonSerializer.SerializeToElement(output, JsonWeb)
                };
            }
            catch
            {
                return false;
            }
        }

        if (payload.ValueKind != JsonValueKind.Object)
            return false;

        url = GetJsonString(payload, "data_url")
            ?? GetJsonString(payload, "dataUrl")
            ?? GetJsonString(payload, "url");
        mediaType = GetJsonString(payload, "media_type")
            ?? GetJsonString(payload, "mediaType")
            ?? MediaTypeNames.Application.Octet;
        filename = GetJsonString(payload, "filename")
            ?? GetJsonString(payload, "file_name")
            ?? GetJsonString(payload, "fileId")
            ?? GetJsonString(payload, "file_id");
        fileId = GetJsonString(payload, "file_id")
            ?? GetJsonString(payload, "fileId");

        return !string.IsNullOrWhiteSpace(url);
    }

    private static Dictionary<string, Dictionary<string, object>?>? EnsureDownloadFileProviderMetadata(
        Dictionary<string, Dictionary<string, object>?>? providerMetadata,
        string? providerId,
        string? filename,
        string? mediaType,
        string? fileId)
    {
        var normalized = providerMetadata is null
            ? new Dictionary<string, Dictionary<string, object>?>(StringComparer.Ordinal)
            : providerMetadata.ToDictionary(
                entry => entry.Key,
                entry => entry.Value is null
                    ? null
                    : new Dictionary<string, object>(entry.Value, StringComparer.Ordinal),
                StringComparer.Ordinal);

        var targetProviderId = !string.IsNullOrWhiteSpace(providerId)
            ? providerId
            : normalized.Keys.FirstOrDefault(key => !string.IsNullOrWhiteSpace(key)) ?? "provider";

        if (!normalized.TryGetValue(targetProviderId, out var scoped) || scoped is null)
        {
            scoped = new Dictionary<string, object>(StringComparer.Ordinal);
            normalized[targetProviderId] = scoped;
        }

        if (!string.IsNullOrWhiteSpace(filename) && !scoped.ContainsKey("filename"))
            scoped["filename"] = filename;

        if (!string.IsNullOrWhiteSpace(mediaType) && !scoped.ContainsKey("media_type"))
            scoped["media_type"] = mediaType;

        if (!string.IsNullOrWhiteSpace(fileId) && !scoped.ContainsKey("file_id"))
            scoped["file_id"] = fileId;

        return normalized.Count == 0 ? null : normalized;
    }

    private static bool HasMetadataValue<T>(Dictionary<string, object> metadata, string key, T expected)
    {
        if (!metadata.TryGetValue(key, out var value) || value is null)
            return false;

        if (value is JsonElement json)
        {
            if (expected is string expectedText && json.ValueKind == JsonValueKind.String)
                return string.Equals(json.GetString(), expectedText, StringComparison.OrdinalIgnoreCase);

            if (expected is bool expectedBool && json.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return json.GetBoolean() == expectedBool;
        }

        if (expected is string text)
            return string.Equals(value.ToString(), text, StringComparison.OrdinalIgnoreCase);

        return value.Equals(expected);
    }

    private static string? GetJsonString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.ToString();
    }

}
