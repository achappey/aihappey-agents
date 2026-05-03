using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Responses;
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
        var capture = ResolveBackendCaptureRequest();

        var state = new StreamingResponseState(GetStreamingModelId(), agent.Name);

        await foreach (var part in http.GetResponsesUpdates(request, capture: capture, ct: cancellationToken).WithCancellation(cancellationToken))
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
            case ResponseCreated created:
                state.RegisterModel(created.Response?.Model);
                yield break;

            case ResponseInProgress inProgress:
                state.RegisterModel(inProgress.Response?.Model);
                yield break;

            case ResponseOutputTextAnnotationAdded e:
                {
                    var ann = e.Annotation;
                    var props = ann?.AdditionalProperties;

                    string? title = null;
                    string? urlStr = null;
                    int? start = null;
                    int? end = null;

                    if (props is not null)
                    {
                        if (props.TryGetValue("title", out var t) && t.ValueKind == JsonValueKind.String)
                            title = t.GetString();

                        if (props.TryGetValue("url", out var u) && u.ValueKind == JsonValueKind.String)
                            urlStr = u.GetString();

                        if (props.TryGetValue("start_index", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out var si))
                            start = si;

                        if (props.TryGetValue("end_index", out var en) && en.ValueKind == JsonValueKind.Number && en.TryGetInt32(out var ei))
                            end = ei;
                    }

                    Uri? uri = null;
                    if (!string.IsNullOrWhiteSpace(urlStr) && Uri.TryCreate(urlStr, UriKind.Absolute, out var parsed))
                        uri = parsed;

                    var citation = new CitationAnnotation
                    {
                        RawRepresentation = ann,
                        Title = title,
                        Url = uri
                    };

                    if (start.HasValue && end.HasValue)
                    {
                        citation.AnnotatedRegions =
                        [
                            new TextSpanAnnotatedRegion
                            {
                                StartIndex = start.Value,
                                EndIndex = end.Value
                            }
                        ];
                    }

                    var annotated = new TextContent(string.Empty);
                    annotated.Annotations ??= [];
                    annotated.Annotations.Add(citation);

                    yield return CreateStreamingUpdate(
                        ChatRole.Assistant,
                        [annotated],
                        e.ItemId);

                    yield break;
                }

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
                var reasoningSummaryDeltaId = reasoningSummaryDelta.ItemId + reasoningSummaryDelta.SummaryIndex.ToString();
                state.MarkReasoningDelta(reasoningSummaryDeltaId, reasoningSummaryDelta.ContentIndex);
                yield return CreateStreamingUpdate(
                    ChatRole.Assistant,
                    [new TextReasoningContent(reasoningSummaryDelta.Delta)],
                    reasoningSummaryDeltaId);
                yield break;

            case ResponseReasoningSummaryTextDone reasoningSummaryDone when !state.HasReasoningDelta(reasoningSummaryDone.ItemId, reasoningSummaryDone.ContentIndex)
                                                                        && !string.IsNullOrWhiteSpace(reasoningSummaryDone.Text):
                var reasoningSummaryDoneId = reasoningSummaryDone.ItemId + reasoningSummaryDone.SummaryIndex.ToString();

                yield return CreateStreamingUpdate(
                    ChatRole.Assistant,
                    [new TextReasoningContent(reasoningSummaryDone.Text)],
                    reasoningSummaryDoneId);
                yield break;

            case ResponseOutputItemAdded added:
                state.RegisterOutputItem(added.Item, added.OutputIndex);
                yield break;

            case ResponseShellCallCommandAdded shellCommandAdded:
                state.AddShellCommand(shellCommandAdded.OutputIndex, shellCommandAdded.CommandIndex, shellCommandAdded.Command);
                yield break;

            case ResponseShellCallCommandDelta shellCommandDelta:
                state.AppendShellCommand(shellCommandDelta.OutputIndex, shellCommandDelta.CommandIndex, shellCommandDelta.Delta);
                yield break;

            case ResponseShellCallCommandDone shellCommandDone:
                state.CompleteShellCommand(shellCommandDone.OutputIndex, shellCommandDone.CommandIndex, shellCommandDone.Command);
                yield break;

            case ResponseFunctionCallArgumentsDelta functionArgumentsDelta:
                state.AppendArguments(functionArgumentsDelta.ItemId, functionArgumentsDelta.Delta, providerExecuted: false);
                yield break;
            case ResponseCustomToolCallInputDelta customToolCallInputDelta:
                state.AppendArguments(customToolCallInputDelta.ItemId, customToolCallInputDelta.Delta, providerExecuted: false);
                yield break;


            case ResponseMcpCallArgumentsDelta mcpArgumentsDelta:
                state.AppendArguments(mcpArgumentsDelta.ItemId, mcpArgumentsDelta.Delta, providerExecuted: true);
                yield break;

            case ResponseFunctionCallArgumentsDone functionArgumentsDone:
                state.SetArguments(functionArgumentsDone.ItemId, functionArgumentsDone.Arguments, providerExecuted: false);
                if (state.TryCreateFunctionCallUpdate(functionArgumentsDone.ItemId, out ChatResponseUpdate functionCallUpdate))
                    yield return functionCallUpdate;
                yield break;

            case ResponseCustomToolCallInputDone customToolCallInputDone:
                state.SetArguments(customToolCallInputDone.ItemId, customToolCallInputDone.Input, providerExecuted: true);
                if (state.TryCreateCustomToolCallInputUpdate(customToolCallInputDone.ItemId, out ChatResponseUpdate customInputUpdate))
                    yield return customInputUpdate;
                yield break;

            case ResponseMcpCallArgumentsDone mcpArgumentsDone:
                state.SetArguments(mcpArgumentsDone.ItemId, mcpArgumentsDone.Arguments, providerExecuted: true);
                if (state.TryCreateMcpInputUpdate(mcpArgumentsDone.ItemId, out ChatResponseUpdate mcpInputUpdate))
                    yield return mcpInputUpdate;
                yield break;
            case ResponseImageGenerationCallGenerating responseImageGenerationCallGenerating:
                yield return new ChatResponseUpdate(
                      ChatRole.Assistant,
                      [new FunctionCallContent(responseImageGenerationCallGenerating.ItemId, "image_generation", new Dictionary<string, object?>()
                            {
                            })
                            {
                                InformationalOnly = true
                            }])
                {
                    MessageId = responseImageGenerationCallGenerating.ItemId,
                };

                yield break;

            case ResponseImageGenerationCallPartialImage responseImageGenerationCallPartialImage:
                var mimeType = $"image/{responseImageGenerationCallPartialImage.OutputFormat}";

                yield return new ChatResponseUpdate(
                      ChatRole.Assistant,
                      [new DataContent(responseImageGenerationCallPartialImage.PartialImageB64.ToDataUri(mimeType), mimeType)
                            {
                            }])
                {
                    MessageId = responseImageGenerationCallPartialImage.ItemId,
                };

                yield break;
            case ResponseOutputItemDone done:
                state.RegisterOutputItem(done.Item, done.OutputIndex);

                if (string.Equals(done.Item.Type, "shell_call", StringComparison.OrdinalIgnoreCase)
                    && state.TryCreateShellInputUpdate(done.Item, done.OutputIndex, out ChatResponseUpdate shellInputUpdate))
                {
                    yield return shellInputUpdate;
                    yield break;
                }

                if (string.Equals(done.Item.Type, "shell_call_output", StringComparison.OrdinalIgnoreCase)
                    && state.TryCreateShellOutputFinalUpdate(done.Item, done.OutputIndex, out ChatResponseUpdate shellOutputUpdate))
                {
                    yield return shellOutputUpdate;
                    yield break;
                }

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

                if (string.Equals(done.Item.Type, "custom_tool_call", StringComparison.OrdinalIgnoreCase)
                    && state.HasToolOutputEmitted(done.Item.Id))
                {
                    yield break;
                }

                foreach (var otherDone in ToResponseOutputItemDoneUpdates(done, state))
                    yield return otherDone;

                yield break;

            case ResponseCompleted completed:
                state.RegisterModel(completed.Response.Model);
                yield return CreateCompletionUpdate(completed.Response, "stop");
                yield break;

            case ResponseFailed failed:
                yield return CreateErrorUpdate(Guid.NewGuid().ToString(), failed.Response?.Error?.Message ?? "Responses stream failed.");
                yield break;

            case ResponseError error:
                yield return CreateErrorUpdate(Guid.NewGuid().ToString(), error?.Message ?? "Responses stream failed.");
                yield break;

            case ResponseUnknownEvent unknown
                when string.Equals(unknown.Type, "response.shell_call_output_content.delta", StringComparison.OrdinalIgnoreCase):
                if (state.TryCreateShellOutputPreliminaryUpdate(unknown, isCompletedChunk: false, out ChatResponseUpdate shellOutputDeltaUpdate))
                    yield return shellOutputDeltaUpdate;
                yield break;

            case ResponseUnknownEvent unknown
                when string.Equals(unknown.Type, "response.shell_call_output_content.done", StringComparison.OrdinalIgnoreCase):
                if (state.TryCreateShellOutputPreliminaryUpdate(unknown, isCompletedChunk: true, out ChatResponseUpdate shellOutputDoneChunkUpdate))
                    yield return shellOutputDoneChunkUpdate;
                yield break;

            case ResponseUnknownEvent unknown
                when string.Equals(unknown.Type, "response.custom_tool_call.input", StringComparison.OrdinalIgnoreCase):
                if (state.TryCreateCustomToolCallInputUpdate(unknown, out ChatResponseUpdate customToolInputUnknownUpdate))
                    yield return customToolInputUnknownUpdate;
                yield break;

            case ResponseUnknownEvent unknown
                when string.Equals(unknown.Type, "response.custom_tool_call.output", StringComparison.OrdinalIgnoreCase):
                if (state.TryCreateCustomToolCallOutputUpdate(unknown, out ChatResponseUpdate customToolOutputUnknownUpdate))
                    yield return customToolOutputUnknownUpdate;
                yield break;

            case ResponseUnknownEvent unknown
                when string.Equals(unknown.Type, "response.output_file.done", StringComparison.OrdinalIgnoreCase):
                if (TryCreateOutputFileUpdate(unknown, out ChatResponseUpdate outputFileUpdate))
                    yield return outputFileUpdate;
                yield break;

            default:
                yield break;
        }
    }

    private bool TryCreateOutputFileUpdate(ResponseUnknownEvent unknown, out ChatResponseUpdate update)
    {
        update = null!;

        var mediaType = GetUnknownEventString(unknown, "media_type") ?? MediaTypeNames.Application.Octet;
        var url = GetUnknownEventString(unknown, "url");
        if (string.IsNullOrWhiteSpace(url))
            return false;

        update = CreateStreamingUpdate(
            ChatRole.Assistant,
            [new DataContent(url, mediaType)
            {
                Name = GetUnknownEventString(unknown, "filename")
            }],
            GetUnknownEventString(unknown, "item_id") ?? Guid.NewGuid().ToString("N"));

        return true;
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

        if (response.Metadata is not null)
        {
            parts.Add(new DataContent(
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response.Metadata, JsonSerializerOptions.Web)),
                MediaTypeNames.Application.Json)
            {
                Name = "finish_metadata"
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
        => new(role, [.. contents])
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

    private static string? GetUnknownEventString(ResponseUnknownEvent unknown, string propertyName)
    {
        if (unknown.Data?.TryGetValue(propertyName, out var property) != true)
            return null;

        return property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : property.GetRawText();
    }

    private static JsonElement? GetUnknownEventProperty(ResponseUnknownEvent unknown, string propertyName)
    {
        if (unknown.Data?.TryGetValue(propertyName, out var property) != true)
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
        private readonly Dictionary<int, StreamingShellInputState> shellInputsByOutputIndex = [];
        private readonly Dictionary<string, StreamingShellInputState> shellInputsByCallId = new(StringComparer.Ordinal);
        private readonly Dictionary<int, StreamingShellOutputState> shellOutputsByOutputIndex = [];
        private readonly Dictionary<string, string> shellToolItemIdsByCallId = new(StringComparer.Ordinal);
        private readonly HashSet<string> emittedToolOutputs = new(StringComparer.Ordinal);
        private readonly HashSet<string> streamedTextKeys = new(StringComparer.Ordinal);
        private readonly HashSet<string> streamedReasoningKeys = new(StringComparer.Ordinal);

        public string ModelId { get; } = modelId;
        public string AuthorName { get; } = authorName;
        public string? ProviderId { get; private set; }

        public void RegisterModel(string? model)
        {
            if (!string.IsNullOrWhiteSpace(ProviderId) || string.IsNullOrWhiteSpace(model))
                return;

            var separatorIndex = model.IndexOf('/');
            ProviderId = separatorIndex > 0 ? model[..separatorIndex] : model;
        }

        public void RegisterOutputItem(ResponseStreamItem item, int? outputIndex = null)
        {
            var itemId = item.Id;
            if (string.IsNullOrWhiteSpace(itemId))
                return;

            if (!string.Equals(item.Type, "function_call", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(item.Type, "mcp_call", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(item.Type, "custom_tool_call", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(item.Type, "shell_call", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(item.Type, "shell_call_output", StringComparison.OrdinalIgnoreCase))
                return;

            if (string.Equals(item.Type, "shell_call", StringComparison.OrdinalIgnoreCase))
            {
                RegisterShellCall(item, outputIndex ?? -1);
                return;
            }

            if (string.Equals(item.Type, "shell_call_output", StringComparison.OrdinalIgnoreCase))
            {
                RegisterShellCallOutput(item, outputIndex ?? -1);
                return;
            }

            var state = GetOrCreateToolCall(itemId,
                string.Equals(item.Type, "function_call", StringComparison.OrdinalIgnoreCase) != true);
            state.Name = string.IsNullOrWhiteSpace(item.Name) ? state.Name : item.Name;
            state.CallId = GetAdditionalPropertyString(item.AdditionalProperties, "call_id") ?? state.CallId ?? itemId;
            state.ProviderMetadata = GetAdditionalPropertyProviderMetadata(item.AdditionalProperties) ?? state.ProviderMetadata;
            state.ToolTitle = state.IsProviderExecuted
                ? BuildMcpToolTitle(item)
                : state.Name;

            if (!string.IsNullOrWhiteSpace(item.Arguments))
            {
                state.Arguments.Clear();
                state.Arguments.Append(item.Arguments);
            }
        }

        public bool HasToolOutputEmitted(string? itemId)
            => !string.IsNullOrWhiteSpace(itemId) && emittedToolOutputs.Contains(itemId);

        public void MarkToolOutputEmitted(string? itemId)
        {
            if (!string.IsNullOrWhiteSpace(itemId))
                emittedToolOutputs.Add(itemId);
        }

        public void AddShellCommand(int outputIndex, int commandIndex, string? command)
        {
            var input = GetOrCreateShellInput(outputIndex);
            var builder = GetShellCommandBuilder(input, commandIndex);
            builder.Clear();
            builder.Append(command ?? string.Empty);
        }

        public void AppendShellCommand(int outputIndex, int commandIndex, string? delta)
        {
            if (string.IsNullOrEmpty(delta))
                return;

            GetShellCommandBuilder(GetOrCreateShellInput(outputIndex), commandIndex).Append(delta);
        }

        public void CompleteShellCommand(int outputIndex, int commandIndex, string? command)
        {
            var builder = GetShellCommandBuilder(GetOrCreateShellInput(outputIndex), commandIndex);
            var completed = command ?? string.Empty;
            if (completed.Length == 0 || string.Equals(builder.ToString(), completed, StringComparison.Ordinal))
                return;

            builder.Clear();
            builder.Append(completed);
        }

        public bool TryCreateShellInputUpdate(ResponseStreamItem item, int outputIndex, out ChatResponseUpdate update)
        {
            update = null!;

            var input = ResolveShellInput(outputIndex, item.CallId)
                ?? GetOrCreateShellInput(outputIndex);

            input.ItemId = item.Id ?? input.ItemId;
            input.OutputIndex = outputIndex;
            input.CallId = item.CallId ?? input.CallId;

            var completedCommands = ExtractShellCommands(item);
            if (completedCommands.Count > 0)
                SyncShellCommands(input, completedCommands);

            if (input.InputEmitted || string.IsNullOrWhiteSpace(input.ItemId))
                return false;

            var commands = input.CommandBuilders.Select(builder => builder.ToString()).ToArray();
            var arguments = new Dictionary<string, object?>
            {
                ["commands"] = commands
            };

            update = new ChatResponseUpdate(
                ChatRole.Assistant,
                [new FunctionCallContent(input.ItemId, "shell_call", arguments)
                {
                    InformationalOnly = true
                }])
            {
                MessageId = input.ItemId,
                AuthorName = AuthorName,
                ModelId = ModelId
            };

            input.InputEmitted = true;

            if (!string.IsNullOrWhiteSpace(input.CallId))
            {
                shellInputsByCallId[input.CallId] = input;
                shellToolItemIdsByCallId[input.CallId] = input.ItemId;
            }

            return true;
        }

        public bool TryCreateShellOutputPreliminaryUpdate(ResponseUnknownEvent unknown, bool isCompletedChunk, out ChatResponseUpdate update)
        {
            update = null!;

            var outputIndexElement = GetUnknownEventProperty(unknown, "output_index");
            if (outputIndexElement is null || !TryGetInt32(outputIndexElement.Value, out var outputIndex))
                return false;

            var output = GetOrCreateShellOutput(outputIndex);
            output.OutputItemId = GetUnknownEventString(unknown, "item_id") ?? output.OutputItemId;

            var commandIndexElement = GetUnknownEventProperty(unknown, "command_index");
            if (commandIndexElement is not null && TryGetInt32(commandIndexElement.Value, out var commandIndex))
            {
                if (!isCompletedChunk)
                {
                    var delta = GetUnknownEventProperty(unknown, "delta");
                    if (delta is not null && delta.Value.ValueKind == JsonValueKind.Object)
                        AppendShellOutputChunkDelta(GetShellOutputChunk(output, commandIndex), delta.Value);
                }
                else
                {
                    var completedOutput = GetUnknownEventProperty(unknown, "output");
                    if (completedOutput is not null && completedOutput.Value.ValueKind == JsonValueKind.Array)
                        ApplyShellOutputChunks(output, completedOutput.Value, commandIndex);
                }
            }

            return TryCreateShellOutputUpdate(output, preliminary: true, out update);
        }

        public bool TryCreateShellOutputFinalUpdate(ResponseStreamItem item, int outputIndex, out ChatResponseUpdate update)
        {
            update = null!;

            var output = ResolveShellOutput(outputIndex) ?? GetOrCreateShellOutput(outputIndex);
            output.OutputIndex = outputIndex;
            output.OutputItemId = item.Id ?? output.OutputItemId;
            output.CallId = item.CallId ?? output.CallId;
            output.Status = item.Status ?? output.Status;
            output.MaxOutputLength = item.MaxOutputLength ?? output.MaxOutputLength;

            ResolveShellToolItemId(output);

            if (item.AdditionalProperties?.TryGetValue("output", out var completedOutput) == true
                && completedOutput.ValueKind == JsonValueKind.Array)
            {
                ApplyShellOutputChunks(output, completedOutput);
            }

            if (output.FinalOutputEmitted)
                return false;

            output.FinalOutputEmitted = true;
            return TryCreateShellOutputUpdate(output, preliminary: false, out update);
        }

        public bool TryCreateCustomToolCallInputUpdate(ResponseUnknownEvent unknown, out ChatResponseUpdate update)
        {
            update = null!;

            var itemId = GetUnknownEventString(unknown, "item_id");
            if (string.IsNullOrWhiteSpace(itemId))
                return false;

            var toolName = GetUnknownEventString(unknown, "tool_name")
                ?? GetUnknownEventString(unknown, "title")
                ?? "custom_tool";

            var input = GetUnknownEventProperty(unknown, "input")
                ?? JsonSerializer.SerializeToElement(new { }, JsonSerializerOptions.Web);

            var arguments = DeserializeArguments(input.GetRawText());

            var call = new FunctionCallContent(itemId, toolName, arguments)
            {
                InformationalOnly = IsProviderExecuted(unknown),
                RawRepresentation = new Dictionary<string, object?>
                {
                    ["provider_metadata"] = GetProviderMetadata(unknown),
                    ["title"] = GetUnknownEventString(unknown, "title")
                }
            };

            update = new ChatResponseUpdate(
                ChatRole.Assistant,
                [call])
            {
                MessageId = itemId,
                AuthorName = AuthorName,
                ModelId = ModelId
            };

            return true;
        }

        public bool TryCreateCustomToolCallOutputUpdate(ResponseUnknownEvent unknown, out ChatResponseUpdate update)
        {
            update = null!;

            var itemId = GetUnknownEventString(unknown, "item_id");
            if (string.IsNullOrWhiteSpace(itemId))
                return false;

            var output = GetUnknownEventProperty(unknown, "output")
                ?? JsonSerializer.SerializeToElement(new { }, JsonSerializerOptions.Web);

            update = new ChatResponseUpdate(
                ChatRole.Tool,
                [new FunctionResultContent(itemId, CreateToolOutputEnvelope(
                    output,
                    preliminary: false,
                    providerExecuted: IsProviderExecuted(unknown),
                    providerMetadata: GetProviderMetadata(unknown)))])
            {
                MessageId = itemId,
                AuthorName = AuthorName,
                ModelId = ModelId
            };

            MarkToolOutputEmitted(itemId);
            return true;
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

        public bool TryCreateCustomToolCallInputUpdate(string? itemId,
            out ChatResponseUpdate update)
        {
            update = null!;

            if (string.IsNullOrWhiteSpace(itemId)
                || !toolCalls.TryGetValue(itemId, out var state)
                || !state.IsProviderExecuted
                || state.InputEmitted)
            {
                return false;
            }
            var call = new FunctionCallContent(
                state.CallId ?? itemId,
                state.Name ?? "custom_tool_call",
                DeserializeArguments(state.ArgumentsText))
            {
                InformationalOnly = true,
                RawRepresentation = new Dictionary<string, object?>
                {
                    ["provider_metadata"] = state.ProviderMetadata,
                    ["title"] = state.ToolTitle
                }
            };

            update = new ChatResponseUpdate(
                           ChatRole.Assistant,
                           [call])
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

        private StreamingShellInputState GetOrCreateShellInput(int outputIndex)
        {
            if (shellInputsByOutputIndex.TryGetValue(outputIndex, out var existing))
                return existing;

            var created = new StreamingShellInputState
            {
                OutputIndex = outputIndex
            };

            shellInputsByOutputIndex[outputIndex] = created;
            return created;
        }

        private StreamingShellInputState? ResolveShellInput(int outputIndex, string? callId)
            => shellInputsByOutputIndex.TryGetValue(outputIndex, out var byIndex)
                ? byIndex
                : !string.IsNullOrWhiteSpace(callId) && shellInputsByCallId.TryGetValue(callId, out var byCallId)
                    ? byCallId
                    : null;

        private void RegisterShellCall(ResponseStreamItem item, int outputIndex)
        {
            var input = outputIndex >= 0 ? GetOrCreateShellInput(outputIndex) : new StreamingShellInputState { OutputIndex = outputIndex };
            input.ItemId = item.Id ?? input.ItemId;
            input.CallId = item.CallId ?? input.CallId;

            var commands = ExtractShellCommands(item);
            if (commands.Count > 0)
                SyncShellCommands(input, commands);

            if (!string.IsNullOrWhiteSpace(input.CallId))
            {
                shellInputsByCallId[input.CallId] = input;
                if (!string.IsNullOrWhiteSpace(input.ItemId))
                    shellToolItemIdsByCallId[input.CallId] = input.ItemId;
            }
        }

        private void RegisterShellCallOutput(ResponseStreamItem item, int outputIndex)
        {
            if (outputIndex < 0)
                return;

            var output = GetOrCreateShellOutput(outputIndex);
            output.OutputItemId = item.Id ?? output.OutputItemId;
            output.CallId = item.CallId ?? output.CallId;
            output.Status = item.Status ?? output.Status;
            output.MaxOutputLength = item.MaxOutputLength ?? output.MaxOutputLength;
            ResolveShellToolItemId(output);
        }

        private StreamingShellOutputState GetOrCreateShellOutput(int outputIndex)
        {
            if (shellOutputsByOutputIndex.TryGetValue(outputIndex, out var existing))
                return existing;

            var created = new StreamingShellOutputState { OutputIndex = outputIndex };
            shellOutputsByOutputIndex[outputIndex] = created;
            return created;
        }

        private StreamingShellOutputState? ResolveShellOutput(int outputIndex)
            => shellOutputsByOutputIndex.TryGetValue(outputIndex, out var output) ? output : null;

        private bool TryCreateShellOutputUpdate(StreamingShellOutputState output, bool preliminary, out ChatResponseUpdate update)
        {
            update = null!;
            ResolveShellToolItemId(output);

            if (string.IsNullOrWhiteSpace(output.ToolItemId))
                return false;

            update = new ChatResponseUpdate(
                ChatRole.Tool,
                [new FunctionResultContent(output.ToolItemId, CreateToolOutputEnvelope(
                    CreateShellCallToolResult(output),
                    preliminary,
                    providerExecuted: true,
                    providerMetadata: CreateShellProviderMetadata(output)))])
            {
                MessageId = output.OutputItemId ?? output.ToolItemId,
                AuthorName = AuthorName,
                ModelId = ModelId
            };

            if (!preliminary)
                MarkToolOutputEmitted(output.ToolItemId);

            return true;
        }

        private void ResolveShellToolItemId(StreamingShellOutputState output)
        {
            if (!string.IsNullOrWhiteSpace(output.ToolItemId))
                return;

            if (!string.IsNullOrWhiteSpace(output.CallId)
                && shellInputsByCallId.TryGetValue(output.CallId, out var input)
                && !string.IsNullOrWhiteSpace(input.ItemId))
            {
                output.ToolItemId = input.ItemId;
                return;
            }

            if (!string.IsNullOrWhiteSpace(output.CallId)
                && shellToolItemIdsByCallId.TryGetValue(output.CallId, out var toolItemId))
            {
                output.ToolItemId = toolItemId;
            }
        }

        private static StringBuilder GetShellCommandBuilder(StreamingShellInputState input, int commandIndex)
        {
            while (input.CommandBuilders.Count <= commandIndex)
                input.CommandBuilders.Add(new StringBuilder());

            return input.CommandBuilders[commandIndex];
        }

        private static StreamingShellOutputChunkState GetShellOutputChunk(StreamingShellOutputState output, int commandIndex)
        {
            if (output.Chunks.TryGetValue(commandIndex, out var existing))
                return existing;

            var created = new StreamingShellOutputChunkState();
            output.Chunks[commandIndex] = created;
            return created;
        }

        private static void AppendShellOutputChunkDelta(StreamingShellOutputChunkState chunk, JsonElement delta)
        {
            if (delta.TryGetProperty("stdout", out var stdout))
                chunk.Stdout.Append(stdout.GetString() ?? stdout.ToString());

            if (delta.TryGetProperty("stderr", out var stderr))
                chunk.Stderr.Append(stderr.GetString() ?? stderr.ToString());

            if (delta.TryGetProperty("created_by", out var createdBy))
                chunk.CreatedBy = createdBy.GetString() ?? createdBy.ToString();
        }

        private static void ApplyShellOutputChunks(StreamingShellOutputState output, JsonElement outputArray, int? startIndex = null)
        {
            if (outputArray.ValueKind != JsonValueKind.Array)
                return;

            var index = 0;
            foreach (var item in outputArray.EnumerateArray())
            {
                var commandIndex = startIndex.HasValue && outputArray.GetArrayLength() == 1
                    ? startIndex.Value
                    : index;
                var chunk = GetShellOutputChunk(output, commandIndex);
                chunk.Stdout.Clear();
                chunk.Stderr.Clear();

                if (item.TryGetProperty("stdout", out var stdout))
                    chunk.Stdout.Append(stdout.GetString() ?? stdout.ToString());

                if (item.TryGetProperty("stderr", out var stderr))
                    chunk.Stderr.Append(stderr.GetString() ?? stderr.ToString());

                if (item.TryGetProperty("created_by", out var createdBy))
                    chunk.CreatedBy = createdBy.GetString() ?? createdBy.ToString();

                chunk.Outcome = item.TryGetProperty("outcome", out var outcome) ? outcome.Clone() : null;
                index++;
            }
        }

        private static object CreateShellCallToolResult(StreamingShellOutputState output)
            => new Dictionary<string, object?>
            {
                ["call_id"] = output.CallId,
                ["status"] = output.Status,
                ["max_output_length"] = output.MaxOutputLength,
                ["output"] = output.Chunks
                    .OrderBy(chunk => chunk.Key)
                    .Select(chunk => new Dictionary<string, object?>
                    {
                        ["stdout"] = chunk.Value.Stdout.ToString(),
                        ["stderr"] = chunk.Value.Stderr.ToString(),
                        ["created_by"] = chunk.Value.CreatedBy,
                        ["outcome"] = chunk.Value.Outcome
                    })
                    .ToList()
            };

        private static Dictionary<string, Dictionary<string, object>?> CreateShellProviderMetadata(StreamingShellOutputState output)
            => new(StringComparer.Ordinal)
            {
                ["openai"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["type"] = "tool_result",
                    ["tool_name"] = "shell_call",
                    ["title"] = "shell_call",
                    ["tool_use_id"] = output.ToolItemId ?? string.Empty,
                    ["call_id"] = output.CallId ?? string.Empty,
                    ["output_item_id"] = output.OutputItemId ?? string.Empty
                }
            };

        private static List<string> ExtractShellCommands(ResponseStreamItem item)
        {
            if (item.AdditionalProperties?.TryGetValue("action", out var action) != true
                || action.ValueKind != JsonValueKind.Object
                || !action.TryGetProperty("commands", out var commands)
                || commands.ValueKind != JsonValueKind.Array)
                return [];

            return [.. commands.EnumerateArray().Select(command => command.GetString() ?? command.ToString())];
        }

        private static void SyncShellCommands(StreamingShellInputState input, IReadOnlyList<string> commands)
        {
            for (var i = 0; i < commands.Count; i++)
            {
                var builder = GetShellCommandBuilder(input, i);
                builder.Clear();
                builder.Append(commands[i]);
            }
        }

        private static int? TryGetItemInt(ResponseStreamItem item, string key)
            => item.AdditionalProperties?.TryGetValue(key, out var value) == true && TryGetInt32(value, out var number)
                ? number
                : null;

        private static bool TryGetInt32(JsonElement value, out int number)
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out number))
                return true;

            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number))
                return true;

            number = 0;
            return false;
        }

        private static bool IsProviderExecuted(ResponseUnknownEvent unknown)
        {
            var providerExecuted = GetUnknownEventProperty(unknown, "provider_executed");
            return providerExecuted is null
                || providerExecuted.Value.ValueKind != JsonValueKind.False;
        }

        private static Dictionary<string, Dictionary<string, object>?>? GetProviderMetadata(ResponseUnknownEvent unknown)
        {
            var providerMetadata = GetUnknownEventProperty(unknown, "provider_metadata");
            if (providerMetadata is null || providerMetadata.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return null;

            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, object>?>>(
                    providerMetadata.Value.GetRawText(),
                    JsonSerializerOptions.Web);
            }
            catch
            {
                return null;
            }
        }

        private static Dictionary<string, Dictionary<string, object>?>? GetAdditionalPropertyProviderMetadata(
            Dictionary<string, JsonElement>? additionalProperties)
        {
            if (additionalProperties is null
                || !additionalProperties.TryGetValue("provider_metadata", out var providerMetadata)
                || providerMetadata.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, object>?>>(
                    providerMetadata.GetRawText(),
                    JsonSerializerOptions.Web);
            }
            catch
            {
                return null;
            }
        }

        private static Dictionary<string, object?> CreateToolOutputEnvelope(
            object? output,
            bool preliminary,
            bool providerExecuted,
            Dictionary<string, Dictionary<string, object>?>? providerMetadata)
            => new(StringComparer.Ordinal)
            {
                ["__aihappey_tool_output"] = true,
                ["output"] = output,
                ["preliminary"] = preliminary,
                ["provider_executed"] = providerExecuted,
                ["provider_metadata"] = providerMetadata
            };
    }

    private sealed class StreamingToolCallState(string itemId, bool isProviderExecuted)
    {
        public string ItemId { get; } = itemId;
        public bool IsProviderExecuted { get; } = isProviderExecuted;
        public string? CallId { get; set; }
        public string? Name { get; set; }
        public string? ToolTitle { get; set; }
        public Dictionary<string, Dictionary<string, object>?>? ProviderMetadata { get; set; }
        public bool InputEmitted { get; set; }
        public StringBuilder Arguments { get; } = new();
        public string ArgumentsText => Arguments.Length == 0 ? "{}" : Arguments.ToString();
    }

    private sealed class StreamingShellInputState
    {
        public string ItemId { get; set; } = string.Empty;
        public int OutputIndex { get; set; }
        public string? CallId { get; set; }
        public bool InputEmitted { get; set; }
        public List<StringBuilder> CommandBuilders { get; } = [];
    }

    private sealed class StreamingShellOutputState
    {
        public int OutputIndex { get; set; }
        public string? OutputItemId { get; set; }
        public string? ToolItemId { get; set; }
        public string? CallId { get; set; }
        public string? Status { get; set; }
        public int? MaxOutputLength { get; set; }
        public bool FinalOutputEmitted { get; set; }
        public SortedDictionary<int, StreamingShellOutputChunkState> Chunks { get; } = [];
    }

    private sealed class StreamingShellOutputChunkState
    {
        public StringBuilder Stdout { get; } = new();
        public StringBuilder Stderr { get; } = new();
        public string? CreatedBy { get; set; }
        public JsonElement? Outcome { get; set; }
    }
}
