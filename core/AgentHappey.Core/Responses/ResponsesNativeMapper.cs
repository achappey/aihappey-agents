using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace AgentHappey.Core.Responses;

public interface IResponsesNativeMapper
{
    IAsyncEnumerable<ResponseStreamPart> MapStreamingAsync(
        ResponseRequest request,
        string? model,
        IAsyncEnumerable<AgentResponseUpdate> updates,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<ResponseStreamPart> MapStreamingAsync(
        ResponseRequest request,
        string? model,
        IAsyncEnumerable<WorkflowEvent> updates,
        CancellationToken cancellationToken = default);

    ResponseResult Map(ResponseRequest request, string? model, AgentResponse response);

    ResponseResult Map(ResponseRequest request, string? model, IEnumerable<WorkflowEvent> events);
}

public sealed class ResponsesNativeMapper : IResponsesNativeMapper
{
    public async IAsyncEnumerable<ResponseStreamPart> MapStreamingAsync(
        ResponseRequest request,
        string? model,
        IAsyncEnumerable<AgentResponseUpdate> updates,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var state = new NativeResponseState(request, model);

        foreach (var part in state.StartEvents())
            yield return part;

        await foreach (var update in updates.WithCancellation(cancellationToken))
            foreach (var part in state.Process(update))
                yield return part;

        foreach (var part in state.Complete())
            yield return part;
    }

    public async IAsyncEnumerable<ResponseStreamPart> MapStreamingAsync(
        ResponseRequest request,
        string? model,
        IAsyncEnumerable<WorkflowEvent> updates,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var state = new NativeResponseState(request, model);

        foreach (var part in state.StartEvents())
            yield return part;

        await foreach (var update in updates.WithCancellation(cancellationToken))
            foreach (var part in state.Process(update))
                yield return part;

        foreach (var part in state.Complete())
            yield return part;
    }

    public ResponseResult Map(ResponseRequest request, string? model, AgentResponse response)
    {
        var state = new NativeResponseState(request, model);
        state.Apply(response);
        foreach (var _ in state.Complete())
        {
        }

        return state.BuildResult();
    }

    public ResponseResult Map(ResponseRequest request, string? model, IEnumerable<WorkflowEvent> events)
    {
        var state = new NativeResponseState(request, model);

        foreach (var update in events)
            foreach (var _ in state.Process(update))
            {
            }

        foreach (var _ in state.Complete())
        {
        }

        return state.BuildResult();
    }

    private sealed class NativeResponseState(ResponseRequest request, string? model)
    {
        private readonly ResponseRequest request = request;
        private readonly string model = string.IsNullOrWhiteSpace(model) ? "agent" : model;
        private readonly List<ResponseOutputState> outputs = [];
        private readonly Dictionary<string, MessageOutputState> messages = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ReasoningOutputState> reasoningItems = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ToolCallOutputState> toolCalls = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ToolResultOutputState> toolResults = new(StringComparer.Ordinal);
        private DateTimeOffset createdAt = DateTimeOffset.UtcNow;
        private string responseId = Guid.NewGuid().ToString("N");
        private object? usage;
        private string? errorMessage;
        private int sequenceNumber;
        private int generatedId;
        private bool started;
        private bool finished;

        public IEnumerable<ResponseStreamPart> StartEvents()
        {
            EnsureStarted();

            return
            [
                new ResponseCreated
                {
                    SequenceNumber = NextSequence(),
                    Response = BuildResult("in_progress", includeCompletedAt: false)
                },
                new ResponseInProgress
                {
                    SequenceNumber = NextSequence(),
                    Response = BuildResult("in_progress", includeCompletedAt: false)
                }
            ];
        }

        public void Apply(AgentResponse response)
        {
            if (!string.IsNullOrWhiteSpace(response.ResponseId))
                responseId = response.ResponseId!;

            if (response.CreatedAt is { } createdAtValue)
                createdAt = createdAtValue;

            SetUsage(response.Usage);

            foreach (var message in response.Messages)
                foreach (var _ in Process(message))
                {
                }
        }

        public IEnumerable<ResponseStreamPart> Process(WorkflowEvent update)
        {
            if (update is AgentResponseUpdateEvent agentResponseUpdateEvent)
                return Process(agentResponseUpdateEvent.Update);

            if (update is WorkflowOutputEvent)
                return CloseOpenItems();

            if (update is WorkflowErrorEvent || update is ExecutorFailedEvent)
            {
                if (update.Data is Exception exception)
                    errorMessage ??= exception.Message;

                return [];
            }

            return [];
        }

        public IEnumerable<ResponseStreamPart> Process(AgentResponseUpdate update)
        {
            EnsureStarted();

            var itemId = GetMessageItemId(update.MessageId);
            var parts = new List<ResponseStreamPart>();

            foreach (var content in update.Contents)
                parts.AddRange(ProcessAssistantContent(itemId, content));

            return parts;
        }

        private IEnumerable<ResponseStreamPart> Process(ChatMessage message)
        {
            EnsureStarted();

            var itemId = GetMessageItemId(message.MessageId);
            var parts = new List<ResponseStreamPart>();

            foreach (var content in message.Contents)
            {
                if (message.Role == ChatRole.Tool)
                    parts.AddRange(ProcessToolContent(content));
                else if (message.Role == ChatRole.Assistant)
                    parts.AddRange(ProcessAssistantContent(itemId, content));
            }

            return parts;
        }

        public IEnumerable<ResponseStreamPart> Complete()
        {
            EnsureStarted();

            if (finished)
                return [];

            var parts = new List<ResponseStreamPart>();
            parts.AddRange(CloseOpenItems());

            finished = true;
            parts.Add(errorMessage is null
                ? new ResponseCompleted
                {
                    SequenceNumber = NextSequence(),
                    Response = BuildResult("completed", includeCompletedAt: true)
                }
                : new ResponseFailed
                {
                    SequenceNumber = NextSequence(),
                    Response = BuildResult("failed", includeCompletedAt: true)
                });

            return parts;
        }

        public ResponseResult BuildResult()
            => BuildResult(errorMessage is null ? "completed" : "failed", includeCompletedAt: true);

        private IEnumerable<ResponseStreamPart> CloseOpenItems()
        {
            var parts = new List<ResponseStreamPart>();

            foreach (var message in messages.Values.Where(state => !state.IsClosed).OrderBy(state => state.OutputIndex))
                parts.AddRange(FinishMessage(message.ItemId));

            foreach (var reasoning in reasoningItems.Values.Where(state => !state.IsClosed).OrderBy(state => state.OutputIndex))
                parts.AddRange(FinishReasoning(reasoning.ItemId));

            return parts;
        }

        private IEnumerable<ResponseStreamPart> ProcessAssistantContent(string itemId, AIContent content)
            => content switch
            {
                UsageContent usageContent => SetUsageAndReturn(usageContent.Details),
                ErrorContent errorContent => SetErrorAndReturn(errorContent.Message),
                TextReasoningContent reasoning => AppendReasoning(itemId, reasoning.Text),
                TextContent text => AppendMessageText(itemId, text.Text),
                FunctionCallContent functionCall => AddToolCall(functionCall),
                FunctionResultContent functionResult => AddToolResult(functionResult),
                DataContent dataContent => AddMessageData(itemId, dataContent),
                _ => []
            };

        private IEnumerable<ResponseStreamPart> ProcessToolContent(AIContent content)
            => content switch
            {
                FunctionResultContent functionResult => AddToolResult(functionResult),
                ErrorContent errorContent => SetErrorAndReturn(errorContent.Message),
                UsageContent usageContent => SetUsageAndReturn(usageContent.Details),
                _ => []
            };

        private IEnumerable<ResponseStreamPart> SetUsageAndReturn(UsageDetails? details)
        {
            SetUsage(details);
            return [];
        }

        private IEnumerable<ResponseStreamPart> SetErrorAndReturn(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message))
                errorMessage ??= message;

            return [];
        }

        private IEnumerable<ResponseStreamPart> AppendMessageText(string itemId, string? delta)
        {
            if (string.IsNullOrEmpty(delta))
                return [];

            var state = GetOrCreateMessage(itemId);
            var parts = new List<ResponseStreamPart>();
            parts.AddRange(EnsureMessageStarted(state));

            var textPart = EnsureTextPart(state, parts);
            ((OutputTextPart)textPart.Part).Text += delta;

            parts.Add(new ResponseOutputTextDelta
            {
                SequenceNumber = NextSequence(),
                Outputindex = state.OutputIndex,
                ItemId = state.ItemId,
                ContentIndex = textPart.ContentIndex,
                Delta = delta
            });

            return parts;
        }

        private IEnumerable<ResponseStreamPart> AddMessageData(string itemId, DataContent content)
        {
            if (!TryCreateResponseContentPart(content, out var responsePart, out var streamPart))
                return [];

            var state = GetOrCreateMessage(itemId);
            var parts = new List<ResponseStreamPart>();
            parts.AddRange(EnsureMessageStarted(state));

            var messagePart = new MessagePartState(state.Parts.Count, responsePart);
            state.Parts.Add(messagePart);

            parts.Add(new ResponseContentPartAdded
            {
                SequenceNumber = NextSequence(),
                OutputIndex = state.OutputIndex,
                ItemId = state.ItemId,
                ContentIndex = messagePart.ContentIndex,
                Part = streamPart
            });

            parts.Add(new ResponseContentPartDone
            {
                SequenceNumber = NextSequence(),
                OutputIndex = state.OutputIndex,
                ItemId = state.ItemId,
                ContentIndex = messagePart.ContentIndex,
                Part = streamPart
            });

            return parts;
        }

        private IEnumerable<ResponseStreamPart> AppendReasoning(string itemId, string? delta)
        {
            if (string.IsNullOrEmpty(delta))
                return [];

            var state = GetOrCreateReasoning(itemId);
            var parts = new List<ResponseStreamPart>();

            if (!state.Started)
            {
                state.Started = true;
                parts.Add(new ResponseOutputItemAdded
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
                });
            }

            state.Text.Append(delta);
            parts.Add(new ResponseReasoningTextDelta
            {
                SequenceNumber = NextSequence(),
                OutputIndex = state.OutputIndex,
                ItemId = state.ItemId,
                ContentIndex = 0,
                Delta = delta
            });

            return parts;
        }

        private IEnumerable<ResponseStreamPart> AddToolCall(FunctionCallContent functionCall)
        {
            if (string.IsNullOrWhiteSpace(functionCall.CallId) || toolCalls.ContainsKey(functionCall.CallId))
                return [];

            var state = new ToolCallOutputState(outputs.Count, functionCall.CallId!)
            {
                Name = functionCall.Name,
                Arguments = SerializeJson(functionCall.Arguments)
            };
            outputs.Add(state);
            toolCalls[state.ItemId] = state;

            var item = new ResponseStreamItem
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
            };

            return
            [
                new ResponseOutputItemAdded
                {
                    SequenceNumber = NextSequence(),
                    OutputIndex = state.OutputIndex,
                    Item = item
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
                    Item = item
                }
            ];
        }

        private IEnumerable<ResponseStreamPart> AddToolResult(FunctionResultContent functionResult)
        {
            if (string.IsNullOrWhiteSpace(functionResult.CallId))
                return [];

            var itemId = $"{functionResult.CallId}:output";
            if (toolResults.ContainsKey(itemId))
                return [];

            var state = new ToolResultOutputState(outputs.Count, itemId)
            {
                CallId = functionResult.CallId,
                Output = SerializeJson(functionResult.Result)
            };
            outputs.Add(state);
            toolResults[state.ItemId] = state;

            var item = new ResponseStreamItem
            {
                Id = state.ItemId,
                Type = "function_call_output",
                Status = "completed",
                AdditionalProperties = new Dictionary<string, JsonElement>
                {
                    ["call_id"] = JsonSerializer.SerializeToElement(state.CallId),
                    ["output"] = JsonSerializer.SerializeToElement(state.Output)
                }
            };

            return
            [
                new ResponseOutputItemAdded
                {
                    SequenceNumber = NextSequence(),
                    OutputIndex = state.OutputIndex,
                    Item = item
                },
                new ResponseOutputItemDone
                {
                    SequenceNumber = NextSequence(),
                    OutputIndex = state.OutputIndex,
                    Item = item
                }
            ];
        }

        private IEnumerable<ResponseStreamPart> EnsureMessageStarted(MessageOutputState state)
        {
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
                }
            ];
        }

        private MessagePartState EnsureTextPart(MessageOutputState state, List<ResponseStreamPart> parts)
        {
            var existing = state.Parts.FirstOrDefault(part => part.Part is OutputTextPart);
            if (existing is not null)
                return existing;

            var textPart = new MessagePartState(state.Parts.Count, new OutputTextPart(string.Empty));
            state.Parts.Add(textPart);

            parts.Add(new ResponseContentPartAdded
            {
                SequenceNumber = NextSequence(),
                OutputIndex = state.OutputIndex,
                ItemId = state.ItemId,
                ContentIndex = textPart.ContentIndex,
                Part = ToStreamContentPart(textPart.Part)
            });

            return textPart;
        }

        private IEnumerable<ResponseStreamPart> FinishMessage(string itemId)
        {
            var state = GetOrCreateMessage(itemId);
            if (state.IsClosed)
                return [];

            state.IsClosed = true;
            var parts = new List<ResponseStreamPart>();

            foreach (var content in state.Parts.Where(part => part.Part is OutputTextPart))
            {
                parts.Add(new ResponseOutputTextDone
                {
                    SequenceNumber = NextSequence(),
                    Outputindex = state.OutputIndex,
                    ItemId = state.ItemId,
                    ContentIndex = content.ContentIndex,
                    Text = ((OutputTextPart)content.Part).Text
                });

                parts.Add(new ResponseContentPartDone
                {
                    SequenceNumber = NextSequence(),
                    OutputIndex = state.OutputIndex,
                    ItemId = state.ItemId,
                    ContentIndex = content.ContentIndex,
                    Part = ToStreamContentPart(content.Part)
                });
            }

            parts.Add(new ResponseOutputItemDone
            {
                SequenceNumber = NextSequence(),
                OutputIndex = state.OutputIndex,
                Item = new ResponseStreamItem
                {
                    Id = state.ItemId,
                    Type = "message",
                    Status = "completed",
                    Role = "assistant",
                    Content = state.Parts.OrderBy(part => part.ContentIndex).Select(part => ToStreamContentPart(part.Part)).ToList()
                }
            });

            return parts;
        }

        private IEnumerable<ResponseStreamPart> FinishReasoning(string itemId)
        {
            var state = GetOrCreateReasoning(itemId);
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

        private MessageOutputState GetOrCreateMessage(string itemId)
        {
            if (messages.TryGetValue(itemId, out var existing))
                return existing;

            var state = new MessageOutputState(outputs.Count, itemId);
            outputs.Add(state);
            messages[itemId] = state;
            return state;
        }

        private ReasoningOutputState GetOrCreateReasoning(string messageItemId)
        {
            var itemId = $"{messageItemId}:reasoning";
            if (reasoningItems.TryGetValue(itemId, out var existing))
                return existing;

            var state = new ReasoningOutputState(outputs.Count, itemId);
            outputs.Add(state);
            reasoningItems[itemId] = state;
            return state;
        }

        private string GetMessageItemId(string? itemId)
            => string.IsNullOrWhiteSpace(itemId)
                ? $"message-{++generatedId:N0}"
                : itemId;

        private void EnsureStarted()
        {
            if (started)
                return;

            started = true;
        }

        private void SetUsage(UsageDetails? details)
        {
            if (details is null)
                return;

            usage = new
            {
                input_tokens = details.InputTokenCount,
                output_tokens = details.OutputTokenCount,
                total_tokens = details.TotalTokenCount
            };
        }

        private int NextSequence() => ++sequenceNumber;

        private ResponseResult BuildResult(string status, bool includeCompletedAt)
        {
            var text = string.Join(
                "\n",
                outputs
                    .OfType<MessageOutputState>()
                    .SelectMany(message => message.Parts)
                    .Select(part => part.Part)
                    .OfType<OutputTextPart>()
                    .Select(part => part.Text)
                    .Where(value => !string.IsNullOrWhiteSpace(value)));

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
                Usage = usage,
                Text = string.IsNullOrWhiteSpace(text) ? null : text,
                ToolChoice = request.ToolChoice,
                Tools = request.Tools?.Cast<object>().ToList() ?? [],
                Reasoning = request.Reasoning,
                Store = request.Store,
                MaxOutputTokens = request.MaxOutputTokens,
                ServiceTier = request.ServiceTier,
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
                id = message.ItemId,
                status = "completed",
                role = "assistant",
                content = message.Parts.OrderBy(part => part.ContentIndex).Select(part => (object)part.Part).ToArray()
            },
            ReasoningOutputState reasoning => new ResponseReasoningItem
            {
                Id = reasoning.ItemId,
                Summary =
                [
                    new ResponseReasoningSummaryTextPart
                    {
                        Text = reasoning.Text.ToString()
                    }
                ]
            },
            ToolCallOutputState toolCall => new ResponseFunctionCallItem
            {
                Id = toolCall.ItemId,
                CallId = toolCall.ItemId,
                Name = toolCall.Name ?? "tool",
                Arguments = toolCall.Arguments,
                Status = "completed"
            },
            ToolResultOutputState toolResult => new ResponseFunctionCallOutputItem
            {
                Id = toolResult.ItemId,
                CallId = toolResult.CallId,
                Output = toolResult.Output,
                Status = "completed"
            },
            _ => new { type = "message", role = "assistant", content = Array.Empty<object>() }
        };

        private static bool TryCreateResponseContentPart(
            DataContent content,
            out ResponseContentPart responsePart,
            out ResponseStreamContentPart streamPart)
        {
            if (ShouldIgnoreDataContent(content))
            {
                responsePart = default!;
                streamPart = default!;
                return false;
            }

            if (content.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                var imageUrl = string.IsNullOrWhiteSpace(content.Uri)
                    ? ToDataUrl(content)
                    : content.Uri;

                var imagePart = new InputImagePart
                {
                    ImageUrl = imageUrl
                };

                responsePart = imagePart;
                streamPart = new ResponseStreamContentPart
                {
                    Type = imagePart.Type,
                    AdditionalProperties = new Dictionary<string, JsonElement>
                    {
                        ["image_url"] = JsonSerializer.SerializeToElement(imagePart.ImageUrl),
                        ["media_type"] = JsonSerializer.SerializeToElement(content.MediaType)
                    }
                };
                return true;
            }

            var filePart = new InputFilePart
            {
                Filename = content.Name,
                FileUrl = string.IsNullOrWhiteSpace(content.Uri) ? null : content.Uri,
                FileData = string.IsNullOrWhiteSpace(content.Uri) ? ToDataUrl(content) : null
            };

            responsePart = filePart;
            streamPart = new ResponseStreamContentPart
            {
                Type = filePart.Type,
                AdditionalProperties = CreateContentAdditionalProperties(
                    ("filename", filePart.Filename),
                    ("file_url", filePart.FileUrl),
                    ("file_data", filePart.FileData),
                    ("media_type", content.MediaType))
            };
            return true;
        }

        private static bool ShouldIgnoreDataContent(DataContent content)
        {
            if (!string.Equals(content.MediaType, MediaTypeNames.Application.Json, StringComparison.OrdinalIgnoreCase))
                return false;

            var name = content.Name?.Trim();
            return name is not null
                && (name.StartsWith("elicitation-", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("model-context-log", StringComparison.OrdinalIgnoreCase));
        }

        private static ResponseStreamContentPart ToStreamContentPart(ResponseContentPart part) => part switch
        {
            OutputTextPart textPart => new ResponseStreamContentPart
            {
                Type = textPart.Type,
                Text = textPart.Text,
                Annotations = []
            },
            InputFilePart filePart => new ResponseStreamContentPart
            {
                Type = filePart.Type,
                AdditionalProperties = CreateContentAdditionalProperties(
                    ("file_data", filePart.FileData),
                    ("file_id", filePart.FileId),
                    ("file_url", filePart.FileUrl),
                    ("filename", filePart.Filename))
            },
            InputImagePart imagePart => new ResponseStreamContentPart
            {
                Type = imagePart.Type,
                AdditionalProperties = CreateContentAdditionalProperties(
                    ("detail", imagePart.Detail),
                    ("file_id", imagePart.FileId),
                    ("image_url", imagePart.ImageUrl))
            },
            _ => new ResponseStreamContentPart
            {
                Type = part.Type
            }
        };

        private static Dictionary<string, JsonElement>? CreateContentAdditionalProperties(params (string Key, object? Value)[] pairs)
        {
            var values = pairs
                .Where(pair => pair.Value is not null)
                .ToDictionary(pair => pair.Key, pair => JsonSerializer.SerializeToElement(pair.Value, ResponseJson.Default));

            return values.Count == 0 ? null : values;
        }

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

        private static string? ToDataUrl(DataContent content)
        {
            var base64Data = content.Base64Data.ToString();
            if (string.IsNullOrWhiteSpace(base64Data))
                return null;

            return $"data:{content.MediaType};base64,{base64Data}";
        }

        private abstract record ResponseOutputState(int OutputIndex, string ItemId);

        private sealed record MessageOutputState(int OutputIndex, string ItemId) : ResponseOutputState(OutputIndex, ItemId)
        {
            public bool Started { get; set; }
            public bool IsClosed { get; set; }
            public List<MessagePartState> Parts { get; } = [];
        }

        private sealed record MessagePartState(int ContentIndex, ResponseContentPart Part);

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
}
