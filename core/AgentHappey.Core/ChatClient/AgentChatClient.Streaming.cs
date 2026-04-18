using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Responses;
using AIHappey.Responses.Extensions;
using AIHappey.Responses.Streaming;
using AgentHappey.Common.Extensions;
using Microsoft.Extensions.AI;
using System.Net.Mime;

namespace AgentHappey.Core.ChatClient;


public partial class AgentChatClient
{
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureHeaders();
        SetHistory(messages);

        var request = BuildResponseRequest(messages, options);
        request.Stream = true;

        var state = new StreamingResponseState(GetStreamingModelId(), agent.Name);

        await foreach (var part in http.GetResponsesUpdates(request, ct: cancellationToken).WithCancellation(cancellationToken))
        {
            foreach (var update in ToStreamingChatResponseUpdates(part, state))
                yield return update;
        }
    }

    private IEnumerable<ChatResponseUpdate> ToStreamingChatResponseUpdates(
        ResponseStreamPart part,
        StreamingResponseState state)
    {
        switch (part)
        {
            case ResponseOutputTextDelta textDelta when !string.IsNullOrWhiteSpace(textDelta.Delta):
                state.MarkTextDelta(textDelta.ItemId, textDelta.ContentIndex);
                yield return CreateStreamingUpdate(
                    ChatRole.Assistant,
                    [new TextContent(textDelta.Delta)],
                    textDelta.ItemId);
                yield break;

            case ResponseOutputTextDone textDone when !state.HasTextDelta(textDone.ItemId, textDone.ContentIndex)
                                                     && !string.IsNullOrWhiteSpace(textDone.Text):
                yield return CreateStreamingUpdate(
                    ChatRole.Assistant,
                    [new TextContent(textDone.Text)],
                    textDone.ItemId);
                yield break;

            case ResponseReasoningTextDelta reasoningDelta when !string.IsNullOrWhiteSpace(reasoningDelta.Delta):
                state.MarkReasoningDelta(reasoningDelta.ItemId, reasoningDelta.ContentIndex);
                yield return CreateStreamingUpdate(
                    ChatRole.Assistant,
                    [new TextReasoningContent(reasoningDelta.Delta)],
                    reasoningDelta.ItemId);
                yield break;

            case ResponseReasoningTextDone reasoningDone when !state.HasReasoningDelta(reasoningDone.ItemId, reasoningDone.ContentIndex)
                                                            && !string.IsNullOrWhiteSpace(reasoningDone.Text):
                yield return CreateStreamingUpdate(
                    ChatRole.Assistant,
                    [new TextReasoningContent(reasoningDone.Text)],
                    reasoningDone.ItemId);
                yield break;

            case ResponseReasoningSummaryTextDelta reasoningSummaryDelta when !string.IsNullOrWhiteSpace(reasoningSummaryDelta.Delta):
                state.MarkReasoningDelta(reasoningSummaryDelta.ItemId, reasoningSummaryDelta.ContentIndex);
                yield return CreateStreamingUpdate(
                    ChatRole.Assistant,
                    [new TextReasoningContent(reasoningSummaryDelta.Delta)],
                    reasoningSummaryDelta.ItemId);
                yield break;

            case ResponseReasoningSummaryTextDone reasoningSummaryDone when !state.HasReasoningDelta(reasoningSummaryDone.ItemId, reasoningSummaryDone.ContentIndex)
                                                                        && !string.IsNullOrWhiteSpace(reasoningSummaryDone.Text):
                yield return CreateStreamingUpdate(
                    ChatRole.Assistant,
                    [new TextReasoningContent(reasoningSummaryDone.Text)],
                    reasoningSummaryDone.ItemId);
                yield break;

            case ResponseOutputItemAdded added:
                state.RegisterOutputItem(added.Item);
                yield break;

            case ResponseFunctionCallArgumentsDelta functionArgumentsDelta:
                state.AppendArguments(functionArgumentsDelta.ItemId, functionArgumentsDelta.Delta, providerExecuted: false);
                yield break;

            case ResponseMcpCallArgumentsDelta mcpArgumentsDelta:
                state.AppendArguments(mcpArgumentsDelta.ItemId, mcpArgumentsDelta.Delta, providerExecuted: true);
                yield break;

            case ResponseFunctionCallArgumentsDone functionArgumentsDone:
                state.SetArguments(functionArgumentsDone.ItemId, functionArgumentsDone.Arguments, providerExecuted: false);
                if (state.TryCreateFunctionCallUpdate(functionArgumentsDone.ItemId, out ChatResponseUpdate functionCallUpdate))
                    yield return functionCallUpdate;
                yield break;

            case ResponseMcpCallArgumentsDone mcpArgumentsDone:
                state.SetArguments(mcpArgumentsDone.ItemId, mcpArgumentsDone.Arguments, providerExecuted: true);
                if (state.TryCreateMcpInputUpdate(mcpArgumentsDone.ItemId, out ChatResponseUpdate mcpInputUpdate))
                    yield return mcpInputUpdate;
                yield break;

            case ResponseOutputItemDone done:
                state.RegisterOutputItem(done.Item);

                if (string.Equals(done.Item.Type, "function_call", StringComparison.OrdinalIgnoreCase)
                    && state.TryCreateFunctionCallUpdate(done.Item.Id, out ChatResponseUpdate functionCallDoneUpdate))
                {
                    yield return functionCallDoneUpdate;
                    yield break;
                }

                if (string.Equals(done.Item.Type, "function_call_output", StringComparison.OrdinalIgnoreCase)
                    && TryCreateFunctionResultUpdate(done.Item, out ChatResponseUpdate functionResultUpdate))
                {
                    yield return functionResultUpdate;
                    yield break;
                }

                if (string.Equals(done.Item.Type, "mcp_call", StringComparison.OrdinalIgnoreCase))
                {
                    if (state.TryCreateMcpInputUpdate(done.Item.Id, out ChatResponseUpdate mcpInputDoneUpdate))
                        yield return mcpInputDoneUpdate;

                    if (TryCreateMcpOutputUpdate(done.Item, out ChatResponseUpdate mcpOutputUpdate))
                        yield return mcpOutputUpdate;

                    yield break;
                }

                yield break;

            case ResponseCompleted completed:
                yield return CreateCompletionUpdate(completed.Response, "stop");
                yield break;

            case ResponseFailed failed:
                yield return CreateErrorUpdate(Guid.NewGuid().ToString(), failed.Response?.Error?.Message ?? "Responses stream failed.");
                yield break;

            case ResponseError error:
                yield return CreateErrorUpdate(Guid.NewGuid().ToString(), error?.Message ?? "Responses stream failed.");
                yield break;
            default:
                yield break;
        }
    }

    private ChatResponseUpdate CreateCompletionUpdate(ResponseResult response, string finishReason)
    {
        List<AIContent> parts = [];

        var usage = ToUsageDetails(response.Usage);
        if (usage != null)
        {
            parts.Add(new UsageContent
            {
                Details = usage
            });
        }

        if (agent.OutputSchema != null)
        {
            var structuredText = GetStructuredOutputText(response);
            if (!string.IsNullOrWhiteSpace(structuredText))
            {
                parts.Add(new DataContent(Encoding.UTF8.GetBytes(structuredText), MediaTypeNames.Application.Json)
                {
                    Name = agent.GetOutputName()
                });
            }
        }

        return CreateStreamingUpdate(
            ChatRole.Assistant,
            parts,
            response.Id ?? Guid.NewGuid().ToString("N"),
            new ChatFinishReason(finishReason));
    }

    private ChatResponseUpdate CreateErrorUpdate(string id, string error)
    {
        List<AIContent> parts = [];

        parts.Add(new ErrorContent(error)
        {
        });

        return CreateStreamingUpdate(
            ChatRole.Assistant,
            parts,
            id ?? Guid.NewGuid().ToString("N"),
            new ChatFinishReason("error"));
    }

    private bool TryCreateFunctionResultUpdate(ResponseStreamItem item, out ChatResponseUpdate update)
    {
        update = null!;

        var callId = GetAdditionalPropertyString(item.AdditionalProperties, "call_id")
            ?? item.Id;

        if (string.IsNullOrWhiteSpace(callId))
            return false;

        var output = GetAdditionalPropertyValue(item.AdditionalProperties, "output");

        update = CreateStreamingUpdate(
            ChatRole.Tool,
            [new FunctionResultContent(callId, ToFunctionResult(output))],
            item.Id ?? callId);

        return true;
    }

    private bool TryCreateMcpOutputUpdate(ResponseStreamItem item, out ChatResponseUpdate update)
    {
        update = null!;

        var output = GetAdditionalPropertyValue(item.AdditionalProperties, "output");
        if (output is null)
            return false;

        var payload = new Dictionary<string, object?>
        {
            ["type"] = "mcp_call",
            ["item_id"] = item.Id,
            ["call_id"] = GetAdditionalPropertyString(item.AdditionalProperties, "call_id") ?? item.Id,
            ["name"] = item.Name,
            ["output"] = output,
            ["provider_executed"] = true,
            ["status"] = item.Status
        };

        update = CreateProviderExecutedUpdate(payload, item.Id, "mcp_call_output");
        return true;
    }

    private ChatResponseUpdate CreateProviderExecutedUpdate(object payload, string? messageId, string dataName)
        => CreateStreamingUpdate(
            ChatRole.Assistant,
            [new DataContent(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web)), MediaTypeNames.Application.Json)
            {
                Name = dataName
            }],
            messageId);

    private ChatResponseUpdate CreateErrorUpdate(Exception exception)
        => CreateStreamingUpdate(
            ChatRole.Assistant,
            [new ErrorContent(GetExceptionMessage(exception))],
            Guid.NewGuid().ToString("N"));

    private ChatResponseUpdate CreateStreamingUpdate(
        ChatRole role,
        IReadOnlyList<AIContent> contents,
        string? messageId = null,
        ChatFinishReason? finishReason = null)
        => new(role, contents.ToList())
        {
            MessageId = messageId ?? Guid.NewGuid().ToString("N"),
            FinishReason = finishReason,
            AuthorName = agent.Name,
            ModelId = GetStreamingModelId()
        };

    private static async IAsyncEnumerable<string> ReadSseLines(
        HttpResponseMessage response,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
                yield break;

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var payload = line["data:".Length..].Trim();
                if (payload.Length > 0)
                    yield return payload;
            }
        }
    }

    private string GetStreamingModelId()
    {
        var segments = agent.Model.Id.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 1
            ? string.Join('/', segments.Skip(1))
            : agent.Model.Id;
    }

    private string? GetStructuredOutputText(ResponseResult response)
    {
        List<AIContent> parts = [];

        foreach (var item in response.Output ?? [])
            AppendResponseOutput(parts, item);

        return parts.OfType<TextContent>()
                   .Select(content => content.Text)
                   .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text))
               ?? SerializeStructuredOutput(response.Text);
    }

    private static string? GetAdditionalPropertyString(
        Dictionary<string, JsonElement>? additionalProperties,
        string propertyName)
    {
        if (additionalProperties?.TryGetValue(propertyName, out var property) != true)
            return null;

        return property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : property.GetRawText();
    }

    private static object? GetAdditionalPropertyValue(
        Dictionary<string, JsonElement>? additionalProperties,
        string propertyName)
    {
        if (additionalProperties?.TryGetValue(propertyName, out var property) != true)
            return null;

        return property.Clone();
    }

    private static object ToFunctionResult(object? output)
    {
        if (output is JsonElement element)
            return element;

        return output ?? new { };
    }

    private static string GetExceptionMessage(Exception exception)
    {
        if (exception is HttpRequestException httpException)
        {
            var parsed = TryGetProviderErrorMessage(httpException.Message);
            if (!string.IsNullOrWhiteSpace(parsed))
                return parsed!;
        }

        return exception.Message;
    }

    private static string? TryGetProviderErrorMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        var jsonStart = message.IndexOf('{');
        if (jsonStart < 0)
            return message;

        try
        {
            using var document = JsonDocument.Parse(message[jsonStart..]);
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                var errorMessage = error.TryGetProperty("message", out var errorMessageProperty)
                    ? errorMessageProperty.GetString()
                    : null;

                var errorCode = error.TryGetProperty("code", out var errorCodeProperty)
                    ? errorCodeProperty.GetString()
                    : null;

                var parameter = error.TryGetProperty("param", out var parameterProperty)
                    ? parameterProperty.GetString()
                    : null;

                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(errorMessage))
                    parts.Add(errorMessage!);
                if (!string.IsNullOrWhiteSpace(parameter))
                    parts.Add($"param: {parameter}");
                if (!string.IsNullOrWhiteSpace(errorCode))
                    parts.Add($"code: {errorCode}");

                if (parts.Count > 0)
                    return string.Join(" | ", parts);
            }
        }
        catch
        {
        }

        return message;
    }

    private sealed class StreamingResponseState(string modelId, string authorName)
    {
        private readonly Dictionary<string, StreamingToolCallState> toolCalls = new(StringComparer.Ordinal);
        private readonly HashSet<string> streamedTextKeys = new(StringComparer.Ordinal);
        private readonly HashSet<string> streamedReasoningKeys = new(StringComparer.Ordinal);

        public string ModelId { get; } = modelId;
        public string AuthorName { get; } = authorName;

        public void RegisterOutputItem(ResponseStreamItem item)
        {
            var itemId = item.Id;
            if (string.IsNullOrWhiteSpace(itemId))
                return;

            if (!string.Equals(item.Type, "function_call", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(item.Type, "mcp_call", StringComparison.OrdinalIgnoreCase))
                return;

            var state = GetOrCreateToolCall(itemId, string.Equals(item.Type, "mcp_call", StringComparison.OrdinalIgnoreCase));
            state.Name = string.IsNullOrWhiteSpace(item.Name) ? state.Name : item.Name;
            state.CallId = GetAdditionalPropertyString(item.AdditionalProperties, "call_id") ?? state.CallId ?? itemId;
            state.ToolTitle = state.IsProviderExecuted
                ? BuildMcpToolTitle(item)
                : state.Name;

            if (!string.IsNullOrWhiteSpace(item.Arguments))
            {
                state.Arguments.Clear();
                state.Arguments.Append(item.Arguments);
            }
        }

        public void AppendArguments(string? itemId, string? delta, bool providerExecuted)
        {
            if (string.IsNullOrWhiteSpace(itemId) || string.IsNullOrEmpty(delta))
                return;

            var state = GetOrCreateToolCall(itemId, providerExecuted);
            state.Arguments.Append(delta);
        }

        public void SetArguments(string? itemId, string? arguments, bool providerExecuted)
        {
            if (string.IsNullOrWhiteSpace(itemId))
                return;

            var state = GetOrCreateToolCall(itemId, providerExecuted);
            state.Arguments.Clear();
            state.Arguments.Append(arguments ?? "{}");
        }

        public bool TryCreateFunctionCallUpdate(string? itemId, out ChatResponseUpdate update)
        {
            update = null!;

            if (string.IsNullOrWhiteSpace(itemId)
                || !toolCalls.TryGetValue(itemId, out var state)
                || state.IsProviderExecuted
                || state.InputEmitted
                || string.IsNullOrWhiteSpace(state.Name))
            {
                return false;
            }

            var callId = state.CallId ?? itemId;
            var arguments = DeserializeArguments(state.ArgumentsText);

            update = new ChatResponseUpdate(
                ChatRole.Assistant,
                [new FunctionCallContent(callId, state.Name, arguments)])
            {
                MessageId = itemId,
                AuthorName = AuthorName,
                ModelId = ModelId
            };

            state.InputEmitted = true;
            return true;
        }

        public bool TryCreateMcpInputUpdate(string? itemId, out ChatResponseUpdate update)
        {
            update = null!;

            if (string.IsNullOrWhiteSpace(itemId)
                || !toolCalls.TryGetValue(itemId, out var state)
                || !state.IsProviderExecuted
                || state.InputEmitted)
            {
                return false;
            }

            var payload = new Dictionary<string, object?>
            {
                ["type"] = "mcp_call",
                ["item_id"] = itemId,
                ["call_id"] = state.CallId ?? itemId,
                ["name"] = state.Name,
                ["tool_name"] = state.ToolTitle ?? state.Name,
                ["arguments"] = ParseArguments(state.ArgumentsText),
                ["provider_executed"] = true
            };

            update = new ChatResponseUpdate(
                ChatRole.Assistant,
                [new DataContent(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web)), MediaTypeNames.Application.Json)
                {
                    Name = "mcp_call"
                }])
            {
                MessageId = itemId,
                AuthorName = AuthorName,
                ModelId = ModelId
            };

            state.InputEmitted = true;
            return true;
        }

        public void MarkTextDelta(string? itemId, int contentIndex)
        {
            if (!string.IsNullOrWhiteSpace(itemId))
                streamedTextKeys.Add(BuildContentKey(itemId, contentIndex));
        }

        public bool HasTextDelta(string? itemId, int contentIndex)
            => !string.IsNullOrWhiteSpace(itemId)
               && streamedTextKeys.Contains(BuildContentKey(itemId, contentIndex));

        public void MarkReasoningDelta(string? itemId, int contentIndex)
        {
            if (!string.IsNullOrWhiteSpace(itemId))
                streamedReasoningKeys.Add(BuildContentKey(itemId, contentIndex));
        }

        public bool HasReasoningDelta(string? itemId, int contentIndex)
            => !string.IsNullOrWhiteSpace(itemId)
               && streamedReasoningKeys.Contains(BuildContentKey(itemId, contentIndex));

        private StreamingToolCallState GetOrCreateToolCall(string itemId, bool providerExecuted)
        {
            if (toolCalls.TryGetValue(itemId, out var existing))
                return existing;

            var created = new StreamingToolCallState(itemId, providerExecuted);
            toolCalls[itemId] = created;
            return created;
        }

        private static string BuildContentKey(string itemId, int contentIndex)
            => $"{itemId}:{contentIndex}";

        private static string? BuildMcpToolTitle(ResponseStreamItem item)
        {
            var serverLabel = GetAdditionalPropertyString(item.AdditionalProperties, "server_label");
            return $"{serverLabel} {item.Name}".Trim();
        }

        private static object ParseArguments(string? arguments)
        {
            if (string.IsNullOrWhiteSpace(arguments))
                return JsonSerializer.SerializeToElement(new { }, JsonSerializerOptions.Web);

            try
            {
                return JsonSerializer.Deserialize<JsonElement>(arguments);
            }
            catch
            {
                return JsonSerializer.SerializeToElement(new Dictionary<string, object?>
                {
                    ["arguments"] = arguments
                }, JsonSerializerOptions.Web);
            }
        }
    }

    private sealed class StreamingToolCallState(string itemId, bool isProviderExecuted)
    {
        public string ItemId { get; } = itemId;
        public bool IsProviderExecuted { get; } = isProviderExecuted;
        public string? CallId { get; set; }
        public string? Name { get; set; }
        public string? ToolTitle { get; set; }
        public bool InputEmitted { get; set; }
        public StringBuilder Arguments { get; } = new();
        public string ArgumentsText => Arguments.Length == 0 ? "{}" : Arguments.ToString();
    }
}
