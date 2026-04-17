using System.Text;
using System.Text.Json;
using AgentHappey.Common.Models;
using AgentHappey.Core.ChatRuntime;
using AIHappey.Responses;
using AIHappey.Responses.Mapping;
using AIHappey.Responses.Streaming;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.AI;

namespace AgentHappey.Core.Responses;

public static class ResponsesRuntimeMapper
{
    private const string ProviderId = "agenthappey-agents";

    public static ChatRuntimeRequest ToChatRuntimeRequest(this ResponseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var unified = request.ToUnifiedRequest(ProviderId);
        var messages = ToChatMessages(unified.Input).ToList();
        var models = ReadStringArray(request.AdditionalProperties, "models");
        var agents = ReadAgents(request.Metadata);
        var workflowType = ReadString(request.AdditionalProperties, "workflowType")
            ?? ReadString(request.AdditionalProperties, "workflow_type")
            ?? "sequential";
        var workflowMetadata = ReadObject<WorkflowMetadata>(request.AdditionalProperties, "workflowMetadata")
            ?? ReadObject<WorkflowMetadata>(request.AdditionalProperties, "workflow_metadata");

        return new ChatRuntimeRequest(
            messages,
            request.Model,
            models,
            agents,
            workflowType,
            workflowMetadata);
    }

    private static IEnumerable<ChatMessage> ToChatMessages(AIInput? input)
    {
        if (!string.IsNullOrWhiteSpace(input?.Text))
            yield return new ChatMessage(ChatRole.User, input.Text!);

        foreach (var item in input?.Items ?? [])
            foreach (var message in ToChatMessages(item))
                yield return message;
    }

    private static IEnumerable<ChatMessage> ToChatMessages(AIInputItem item)
    {
        switch (item.Type)
        {
            case "message":
                yield return new ChatMessage(ParseRole(item.Role), ToContents(item).ToList())
                {
                    MessageId = item.Id
                };
                yield break;

            case "function_call":
                {
                    var toolCall = item.Content?.OfType<AIToolCallContentPart>().FirstOrDefault();
                    if (toolCall is null)
                        yield break;

                    yield return new ChatMessage(ChatRole.Assistant,
                    [
                        new FunctionCallContent(
                            toolCall.ToolCallId,
                            toolCall.ToolName ?? toolCall.Title ?? "tool",
                            NormalizeObject(toolCall.Input) as IDictionary<string, object?> ?? new Dictionary<string, object?>())
                    ])
                    {
                        MessageId = item.Id ?? toolCall.ToolCallId
                    };
                    yield break;
                }

            case "function_call_output":
                {
                    var toolCall = item.Content?.OfType<AIToolCallContentPart>().FirstOrDefault();
                    if (toolCall is null)
                        yield break;

                    yield return new ChatMessage(ChatRole.Tool,
                    [
                        new FunctionResultContent(toolCall.ToolCallId, NormalizeObject(toolCall.Output) ?? new { })
                    ])
                    {
                        MessageId = item.Id ?? toolCall.ToolCallId
                    };
                    yield break;
                }

            case "reasoning":
                {
                    var text = string.Join("\n", item.Content?.OfType<AITextContentPart>().Select(part => part.Text) ?? []);
                    if (string.IsNullOrWhiteSpace(text))
                        yield break;

                    yield return new ChatMessage(ChatRole.Assistant,
                    [
                        new TextReasoningContent(text)
                    ])
                    {
                        MessageId = item.Id
                    };
                    yield break;
                }

            case "compaction":
                yield break;

            case "item_reference":
                throw new NotSupportedException("Responses item_reference replay is not supported by the agents runtime.");

            default:
                if (item.Content?.Count > 0)
                {
                    yield return new ChatMessage(ParseRole(item.Role), ToContents(item).ToList())
                    {
                        MessageId = item.Id
                    };
                }

                yield break;
        }
    }

    private static IEnumerable<AIContent> ToContents(AIInputItem item)
    {
        foreach (var part in item.Content ?? [])
        {
            switch (part)
            {
                case AITextContentPart text:
                    yield return new TextContent(text.Text);
                    break;

                case AIReasoningContentPart reasoning when !string.IsNullOrWhiteSpace(reasoning.Text):
                    yield return new TextReasoningContent(reasoning.Text!);
                    break;

                case AIFileContentPart file:
                    {
                        var data = file.Data?.ToString();
                        if (string.IsNullOrWhiteSpace(data))
                            break;

                        var mediaType = file.MediaType ?? "application/octet-stream";
                        yield return new DataContent(data, mediaType)
                        {
                            Name = file.Filename
                        };
                        break;
                    }

                case AIToolCallContentPart toolCall:
                    yield return new FunctionCallContent(
                        toolCall.ToolCallId,
                        toolCall.ToolName ?? toolCall.Title ?? "tool",
                        NormalizeObject(toolCall.Input) as IDictionary<string, object?> ?? new Dictionary<string, object?>());
                    break;
            }
        }
    }

    private static ChatRole ParseRole(string? role) => role?.Trim().ToLowerInvariant() switch
    {
        "assistant" => ChatRole.Assistant,
        "system" => ChatRole.System,
        "developer" => ChatRole.System,
        "tool" => ChatRole.Tool,
        _ => ChatRole.User
    };

    private static IReadOnlyList<string>? ReadStringArray(Dictionary<string, JsonElement>? source, string key)
    {
        if (source is null || !source.TryGetValue(key, out var element) || element.ValueKind != JsonValueKind.Array)
            return null;

        var values = element
            .EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .ToList();

        return values.Count == 0 ? null : values;
    }

    private static string? ReadString(Dictionary<string, JsonElement>? source, string key)
        => source is not null && source.TryGetValue(key, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static T? ReadObject<T>(Dictionary<string, JsonElement>? source, string key)
    {
        if (source is null || !source.TryGetValue(key, out var element))
            return default;

        return JsonSerializer.Deserialize<T>(element.GetRawText(), ResponseJson.Default);
    }

    private static IReadOnlyList<Agent>? ReadAgents(Dictionary<string, object?>? metadata)
    {
        if (metadata is null || !metadata.TryGetValue("agents", out var agents) || agents is null)
            return null;

        var normalized = NormalizeObject(agents);
        var result = JsonSerializer.Deserialize<List<Agent>>(
            JsonSerializer.Serialize(normalized, JsonSerializerOptions.Web),
            JsonSerializerOptions.Web);

        return result is { Count: > 0 } ? result : null;
    }

    private static object? NormalizeObject(object? value)
    {
        if (value is null)
            return null;

        if (value is JsonElement jsonElement)
            return JsonSerializer.Deserialize<object>(jsonElement.GetRawText(), JsonSerializerOptions.Web);

        return JsonSerializer.Deserialize<object>(JsonSerializer.Serialize(value, JsonSerializerOptions.Web), JsonSerializerOptions.Web);
    }
}

public sealed class ResponsesRuntimeStreamSerializer(ResponseRequest request, string? model)
{
    private readonly DateTimeOffset createdAt = DateTimeOffset.UtcNow;
    private readonly string responseId = Guid.NewGuid().ToString("N");
    private readonly ResponseRequest request = request;
    private readonly string model = string.IsNullOrWhiteSpace(model) ? "agent" : model;
    private readonly List<ResponseOutputState> outputs = [];
    private readonly Dictionary<string, MessageOutputState> messages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ReasoningOutputState> reasoningItems = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ToolCallOutputState> toolCalls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ToolResultOutputState> toolResults = new(StringComparer.Ordinal);
    private int sequenceNumber;
    private bool started;
    private bool finished;
    private string? errorMessage;

    public IEnumerable<ResponseStreamPart> Process(UIMessagePart part)
    {
        EnsureStarted();

        switch (part)
        {
            case TextStartUIMessageStreamPart textStart:
                return StartMessage(textStart.Id);

            case TextDeltaUIMessageStreamPart textDelta:
                return AppendMessage(textDelta.Id, textDelta.Delta ?? string.Empty);

            case TextEndUIMessageStreamPart textEnd:
                return FinishMessage(textEnd.Id);

            case ReasoningStartUIPart reasoningStart:
                return StartReasoning(reasoningStart.Id);

            case ReasoningDeltaUIPart reasoningDelta:
                return AppendReasoning(reasoningDelta.Id, reasoningDelta.Delta ?? string.Empty);

            case ReasoningEndUIPart reasoningEnd:
                return FinishReasoning(reasoningEnd.Id);

            case ToolCallPart toolCall:
                return AddToolCall(toolCall);

            case ToolOutputAvailablePart toolOutput:
                return AddToolResult(toolOutput);

            case ErrorUIPart error:
                errorMessage ??= error.ErrorText ?? "Unknown error";
                return [];

            default:
                return [];
        }
    }

    public IEnumerable<ResponseStreamPart> Complete()
    {
        EnsureStarted();

        if (finished)
            return [];

        var parts = new List<ResponseStreamPart>();

        foreach (var message in messages.Values.Where(state => !state.IsClosed))
            parts.AddRange(FinishMessage(message.ItemId));

        foreach (var reasoning in reasoningItems.Values.Where(state => !state.IsClosed))
            parts.AddRange(FinishReasoning(reasoning.ItemId));

        finished = true;
        parts.Add(errorMessage is null
            ? new ResponseCompleted
            {
                SequenceNumber = NextSequence(),
                Response = BuildResult("completed", true)
            }
            : new ResponseFailed
            {
                SequenceNumber = NextSequence(),
                Response = BuildResult("failed", true)
            });

        return parts;
    }

    public ResponseResult BuildResult()
        => BuildResult(errorMessage is null ? "completed" : "failed", true);

    private IEnumerable<ResponseStreamPart> StartMessage(string id)
    {
        var state = GetOrCreateMessage(id);
        if (state.Started)
            return [];

        state.Started = true;

        return
        [
            new ResponseOutputItemAdded
            {
                SequenceNumber = NextSequence(),
                OutputIndex = state.OutputIndex,
                Item = new ResponseStreamItem
                {
                    Id = state.ItemId,
                    Type = "message",
                    Status = "in_progress",
                    Role = "assistant",
                    Content = []
                }
            },
            new ResponseContentPartAdded
            {
                SequenceNumber = NextSequence(),
                OutputIndex = state.OutputIndex,
                ItemId = state.ItemId,
                ContentIndex = 0,
                Part = new ResponseStreamContentPart
                {
                    Type = "output_text",
                    Text = string.Empty,
                    Annotations = []
                }
            }
        ];
    }

    private IEnumerable<ResponseStreamPart> AppendMessage(string id, string delta)
    {
        var state = GetOrCreateMessage(id);
        if (!state.Started)
            foreach (var _ in StartMessage(id)) { }

        state.Text.Append(delta);

        return
        [
            new ResponseOutputTextDelta
            {
                SequenceNumber = NextSequence(),
                Outputindex = state.OutputIndex,
                ItemId = state.ItemId,
                ContentIndex = 0,
                Delta = delta
            }
        ];
    }

    private IEnumerable<ResponseStreamPart> FinishMessage(string id)
    {
        var state = GetOrCreateMessage(id);
        if (state.IsClosed)
            return [];

        state.IsClosed = true;
        var text = state.Text.ToString();

        return
        [
            new ResponseOutputTextDone
            {
                SequenceNumber = NextSequence(),
                Outputindex = state.OutputIndex,
                ItemId = state.ItemId,
                ContentIndex = 0,
                Text = text
            },
            new ResponseContentPartDone
            {
                SequenceNumber = NextSequence(),
                OutputIndex = state.OutputIndex,
                ItemId = state.ItemId,
                ContentIndex = 0,
                Part = new ResponseStreamContentPart
                {
                    Type = "output_text",
                    Text = text,
                    Annotations = []
                }
            },
            new ResponseOutputItemDone
            {
                SequenceNumber = NextSequence(),
                OutputIndex = state.OutputIndex,
                Item = new ResponseStreamItem
                {
                    Id = state.ItemId,
                    Type = "message",
                    Status = "completed",
                    Role = "assistant",
                    Content = [new ResponseStreamContentPart { Type = "output_text", Text = text, Annotations = [] }]
                }
            }
        ];
    }

    private IEnumerable<ResponseStreamPart> StartReasoning(string id)
    {
        var state = GetOrCreateReasoning(id);
        if (state.Started)
            return [];

        state.Started = true;

        return
        [
            new ResponseOutputItemAdded
            {
                SequenceNumber = NextSequence(),
                OutputIndex = state.OutputIndex,
                Item = new ResponseStreamItem
                {
                    Id = state.ItemId,
                    Type = "reasoning",
                    Status = "in_progress",
                    Content = []
                }
            }
        ];
    }

    private IEnumerable<ResponseStreamPart> AppendReasoning(string id, string delta)
    {
        var state = GetOrCreateReasoning(id);
        if (!state.Started)
            foreach (var _ in StartReasoning(id)) { }

        state.Text.Append(delta);

        return
        [
            new ResponseReasoningTextDelta
            {
                SequenceNumber = NextSequence(),
                OutputIndex = state.OutputIndex,
                ItemId = state.ItemId,
                ContentIndex = 0,
                Delta = delta
            }
        ];
    }

    private IEnumerable<ResponseStreamPart> FinishReasoning(string id)
    {
        var state = GetOrCreateReasoning(id);
        if (state.IsClosed)
            return [];

        state.IsClosed = true;
        var text = state.Text.ToString();

        return
        [
            new ResponseReasoningTextDone
            {
                SequenceNumber = NextSequence(),
                OutputIndex = state.OutputIndex,
                ItemId = state.ItemId,
                ContentIndex = 0,
                Text = text
            },
            new ResponseOutputItemDone
            {
                SequenceNumber = NextSequence(),
                OutputIndex = state.OutputIndex,
                Item = new ResponseStreamItem
                {
                    Id = state.ItemId,
                    Type = "reasoning",
                    Status = "completed",
                    Content = [new ResponseStreamContentPart { Type = "summary_text", Text = text }]
                }
            }
        ];
    }

    private IEnumerable<ResponseStreamPart> AddToolCall(ToolCallPart toolCall)
    {
        if (toolCalls.ContainsKey(toolCall.ToolCallId))
            return [];

        var state = new ToolCallOutputState(outputs.Count, toolCall.ToolCallId)
        {
            Name = toolCall.ToolName,
            Arguments = SerializeJson(toolCall.Input)
        };
        outputs.Add(state);
        toolCalls[state.ItemId] = state;

        return
        [
            new ResponseOutputItemAdded
            {
                SequenceNumber = NextSequence(),
                OutputIndex = state.OutputIndex,
                Item = new ResponseStreamItem
                {
                    Id = state.ItemId,
                    Type = "function_call",
                    Status = "completed",
                    Name = state.Name,
                    Arguments = state.Arguments,
                    AdditionalProperties = new Dictionary<string, JsonElement>
                    {
                        ["call_id"] = JsonSerializer.SerializeToElement(state.ItemId)
                    }
                }
            },
            new ResponseFunctionCallArgumentsDone
            {
                SequenceNumber = NextSequence(),
                OutputIndex = state.OutputIndex,
                ItemId = state.ItemId,
                Arguments = state.Arguments
            },
            new ResponseOutputItemDone
            {
                SequenceNumber = NextSequence(),
                OutputIndex = state.OutputIndex,
                Item = new ResponseStreamItem
                {
                    Id = state.ItemId,
                    Type = "function_call",
                    Status = "completed",
                    Name = state.Name,
                    Arguments = state.Arguments,
                    AdditionalProperties = new Dictionary<string, JsonElement>
                    {
                        ["call_id"] = JsonSerializer.SerializeToElement(state.ItemId)
                    }
                }
            }
        ];
    }

    private IEnumerable<ResponseStreamPart> AddToolResult(ToolOutputAvailablePart toolOutput)
    {
        if (toolResults.ContainsKey(toolOutput.ToolCallId))
            return [];

        var state = new ToolResultOutputState(outputs.Count, $"{toolOutput.ToolCallId}:output")
        {
            CallId = toolOutput.ToolCallId,
            Output = SerializeJson(toolOutput.Output)
        };
        outputs.Add(state);
        toolResults[state.ItemId] = state;

        return
        [
            new ResponseOutputItemAdded
            {
                SequenceNumber = NextSequence(),
                OutputIndex = state.OutputIndex,
                Item = new ResponseStreamItem
                {
                    Id = state.ItemId,
                    Type = "function_call_output",
                    Status = "completed",
                    AdditionalProperties = new Dictionary<string, JsonElement>
                    {
                        ["call_id"] = JsonSerializer.SerializeToElement(state.CallId),
                        ["output"] = JsonSerializer.SerializeToElement(state.Output)
                    }
                }
            },
            new ResponseOutputItemDone
            {
                SequenceNumber = NextSequence(),
                OutputIndex = state.OutputIndex,
                Item = new ResponseStreamItem
                {
                    Id = state.ItemId,
                    Type = "function_call_output",
                    Status = "completed",
                    AdditionalProperties = new Dictionary<string, JsonElement>
                    {
                        ["call_id"] = JsonSerializer.SerializeToElement(state.CallId),
                        ["output"] = JsonSerializer.SerializeToElement(state.Output)
                    }
                }
            }
        ];
    }

    private void EnsureStarted()
    {
        if (started)
            return;

        started = true;
    }

    public IEnumerable<ResponseStreamPart> StartEvents()
    {
        EnsureStarted();

        return
        [
            new ResponseCreated
            {
                SequenceNumber = NextSequence(),
                Response = BuildResult("in_progress", false)
            },
            new ResponseInProgress
            {
                SequenceNumber = NextSequence(),
                Response = BuildResult("in_progress", false)
            }
        ];
    }

    private MessageOutputState GetOrCreateMessage(string id)
    {
        if (messages.TryGetValue(id, out var existing))
            return existing;

        var state = new MessageOutputState(outputs.Count, id);
        outputs.Add(state);
        messages[id] = state;
        return state;
    }

    private ReasoningOutputState GetOrCreateReasoning(string id)
    {
        if (reasoningItems.TryGetValue(id, out var existing))
            return existing;

        var state = new ReasoningOutputState(outputs.Count, id);
        outputs.Add(state);
        reasoningItems[id] = state;
        return state;
    }

    private int NextSequence() => ++sequenceNumber;

    private ResponseResult BuildResult(string status, bool includeCompletedAt)
    {
        var text = string.Join("\n", outputs.OfType<MessageOutputState>().Select(output => output.Text.ToString()).Where(value => !string.IsNullOrWhiteSpace(value)));

        return new ResponseResult
        {
            Id = responseId,
            Object = "response",
            CreatedAt = createdAt.ToUnixTimeSeconds(),
            CompletedAt = includeCompletedAt ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() : null,
            Status = status,
            Model = model,
            Temperature = request.Temperature,
            ParallelToolCalls = request.ParallelToolCalls,
            ToolChoice = request.ToolChoice,
            Tools = request.Tools?.Cast<object>().ToList() ?? [],
            Store = false,
            MaxOutputTokens = request.MaxOutputTokens,
            ServiceTier = request.ServiceTier,
            Text = string.IsNullOrWhiteSpace(text) ? null : text,
            Output = outputs.OrderBy(output => output.OutputIndex).Select(ToOutputObject).ToList(),
            Error = errorMessage is null ? null : new ResponseResultError { Message = errorMessage },
            Metadata = request.Metadata
        };
    }

    private static object ToOutputObject(ResponseOutputState output) => output switch
    {
        MessageOutputState message => new
        {
            type = "message",
            role = "assistant",
            content = new object[]
            {
                new OutputTextPart(message.Text.ToString())
            }
        },
        ReasoningOutputState reasoning => new
        {
            type = "reasoning",
            id = reasoning.ItemId,
            summary = new object[]
            {
                new { type = "summary_text", text = reasoning.Text.ToString() }
            }
        },
        ToolCallOutputState toolCall => new
        {
            type = "function_call",
            id = toolCall.ItemId,
            call_id = toolCall.ItemId,
            name = toolCall.Name,
            arguments = toolCall.Arguments,
            status = "completed"
        },
        ToolResultOutputState toolResult => new
        {
            type = "function_call_output",
            id = toolResult.ItemId,
            call_id = toolResult.CallId,
            output = toolResult.Output,
            status = "completed"
        },
        _ => new { type = "message", role = "assistant", content = Array.Empty<object>() }
    };

    private static string SerializeJson(object? value)
    {
        if (value is null)
            return "{}";

        if (value is string text)
            return text;

        if (value is JsonElement element)
            return element.GetRawText();

        return JsonSerializer.Serialize(value, JsonSerializerOptions.Web);
    }

    private abstract record ResponseOutputState(int OutputIndex, string ItemId);

    private sealed record MessageOutputState(int OutputIndex, string ItemId) : ResponseOutputState(OutputIndex, ItemId)
    {
        public bool Started { get; set; }
        public bool IsClosed { get; set; }
        public StringBuilder Text { get; } = new();
    }

    private sealed record ReasoningOutputState(int OutputIndex, string ItemId) : ResponseOutputState(OutputIndex, ItemId)
    {
        public bool Started { get; set; }
        public bool IsClosed { get; set; }
        public StringBuilder Text { get; } = new();
    }

    private sealed record ToolCallOutputState(int OutputIndex, string ItemId) : ResponseOutputState(OutputIndex, ItemId)
    {
        public string? Name { get; set; }
        public string Arguments { get; set; } = "{}";
    }

    private sealed record ToolResultOutputState(int OutputIndex, string ItemId) : ResponseOutputState(OutputIndex, ItemId)
    {
        public string CallId { get; set; } = string.Empty;
        public string Output { get; set; } = "{}";
    }
}
