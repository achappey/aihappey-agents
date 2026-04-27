using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Abstractions.Http;
using AIHappey.Responses;
using AgentHappey.Common.Extensions;
using Microsoft.Extensions.AI;

namespace AgentHappey.Core.ChatClient;

public partial class AgentChatClient
{
    private ResponseRequest BuildResponseRequest(IEnumerable<ChatMessage> messages, ChatOptions? options)
        => new()
        {
            Model = agent.Model.Id,
            Input = new ResponseInput(ToResponseInputItems(messages)),
            Tools = options?.Tools?.OfType<AIFunctionDeclaration>().Select(ToResponseToolDefinition).ToList(),
            Text = agent.GetCompletionsOutputSchema(),
            Metadata = BuildResponsesProviderMetadata(),
            Instructions = options?.Instructions,
            Stream = false,
            ToolChoice = "auto",
            ParallelToolCalls = HasProviderOption("parallel_tool_calls") ? null : true,
            Temperature = agent.Model.Options?.Temperature ?? 1
        };

    private string GetProviderKey()
        => agent.Model.Id.Split('/')[0];

    private bool HasProviderOption(string optionName)
        => agent.Model.ProviderMetadata?.ContainsKey(optionName) == true;

    private Dictionary<string, object?>? BuildResponsesProviderMetadata()
    {
        if (agent.Model.ProviderMetadata is not { Count: > 0 } providerMetadata)
            return null;

        return new Dictionary<string, object?>
        {
            [GetProviderKey()] = JsonSerializer.SerializeToElement(providerMetadata, JsonSerializerOptions.Web)
        };
    }

    private ProviderBackendCaptureRequest? ResolveBackendCaptureRequest()
    {
        if (agent.Model.ProviderMetadata is not { Count: > 0 } providerMetadata)
            return null;

        return TryGetBackendCaptureRequest(providerMetadata, "capture")
            ?? TryGetBackendCaptureRequest(providerMetadata, "backend_capture")
            ?? TryGetNestedBackendCaptureRequest(providerMetadata, GetProviderKey(), "capture")
            ?? TryGetNestedBackendCaptureRequest(providerMetadata, GetProviderKey(), "backend_capture");
    }

    private static ProviderBackendCaptureRequest? TryGetNestedBackendCaptureRequest(
        Dictionary<string, object> providerMetadata,
        string providerKey,
        string optionName)
    {
        if (!providerMetadata.TryGetValue(providerKey, out var nestedMetadata) || nestedMetadata is null)
            return null;

        try
        {
            var json = ToJsonElement(nestedMetadata);
            if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty(optionName, out var option))
                return null;

            return JsonSerializer.Deserialize<ProviderBackendCaptureRequest>(option.GetRawText(), JsonSerializerOptions.Web);
        }
        catch
        {
            return null;
        }
    }

    private static ProviderBackendCaptureRequest? TryGetBackendCaptureRequest(
        Dictionary<string, object> providerMetadata,
        string optionName)
    {
        if (!providerMetadata.TryGetValue(optionName, out var option) || option is null)
            return null;

        try
        {
            return JsonSerializer.Deserialize<ProviderBackendCaptureRequest>(ToJsonElement(option).GetRawText(), JsonSerializerOptions.Web);
        }
        catch
        {
            return null;
        }
    }

    private static JsonElement ToJsonElement(object value)
        => value is JsonElement element
            ? element
            : JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);

    private List<ResponseInputItem> ToResponseInputItems(IEnumerable<ChatMessage> messages)
    {
        var items = new List<ResponseInputItem>();

        foreach (var message in messages)
        {
            if (message.Role == ChatRole.Tool)
            {
                foreach (var result in message.Contents.OfType<FunctionResultContent>())
                {
                    items.Add(new ResponseFunctionCallOutputItem
                    {
                        CallId = result.CallId,
                        Output = SerializeResponseValue(result.Result),
                        Status = "completed"
                    });
                }

                continue;
            }

            foreach (var item in ToResponseInputItems(message))
                items.Add(item);
        }

        return items;
    }

    private IEnumerable<ResponseInputItem> ToResponseInputItems(ChatMessage message)
    {
        var contentParts = new List<ResponseContentPart>();

        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextReasoningContent reasoning when !string.IsNullOrWhiteSpace(reasoning.ProtectedData):
                    foreach (var item in FlushResponseInputMessage(message, contentParts))
                        yield return item;

                    yield return new ResponseReasoningItem
                    {
                        EncryptedContent = reasoning.ProtectedData,
                        Summary =
                        [
                            new ResponseReasoningSummaryTextPart
                            {
                                Text = reasoning.Text
                            }
                        ]
                    };
                    break;

                case FunctionCallContent call when message.Role == ChatRole.Assistant:
                    foreach (var item in FlushResponseInputMessage(message, contentParts))
                        yield return item;

                    yield return new ResponseFunctionCallItem
                    {
                        CallId = call.CallId,
                        Name = call.Name,
                        Arguments = SerializeResponseValue(call.Arguments)
                    };
                    break;

                default:
                    var contentPart = ToResponseContentPart(message, content);
                    if (contentPart is not null)
                        contentParts.Add(contentPart);
                    break;
            }
        }

        foreach (var item in FlushResponseInputMessage(message, contentParts))
            yield return item;
    }

    private static IEnumerable<ResponseInputItem> FlushResponseInputMessage(
        ChatMessage message,
        List<ResponseContentPart> contentParts)
    {
        if (contentParts.Count == 0)
            yield break;

        yield return new ResponseInputMessage
        {
            Role = ToResponseRole(message.Role),
            Content = new ResponseMessageContent(contentParts.ToList())
        };

        contentParts.Clear();
    }

    private static IEnumerable<ResponseContentPart> ToResponseContentParts(ChatMessage message)
    {
        foreach (var content in message.Contents)
        {
            var part = ToResponseContentPart(message, content);
            if (part is not null)
                yield return part;
        }
    }

    private static ResponseContentPart? ToResponseContentPart(ChatMessage message, AIContent content)
    {
        switch (content)
        {
            case TextContent text when !string.IsNullOrWhiteSpace(text.Text):
                return message.Role == ChatRole.Assistant
                    ? new OutputTextPart(text.Text)
                    : new InputTextPart(text.Text);

            case DataContent data when data.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase):
                if (message.Role == ChatRole.User)
                    return new InputImagePart
                    {
                        ImageUrl = string.IsNullOrWhiteSpace(data.Uri)
                            ? ToDataUrl(data)
                            : data.Uri
                    };

                break;

            case DataContent data when !ShouldIgnoreDataContent(data):
                if (message.Role == ChatRole.User)
                    return new InputFilePart
                    {
                        Filename = data.Name,
                        FileData = data.Uri
                    };

                break;
        }

        return null;
    }

    private static ResponseToolDefinition ToResponseToolDefinition(AIFunctionDeclaration declaration)
    {
        var extra = new Dictionary<string, JsonElement>
        {
            ["name"] = JsonSerializer.SerializeToElement(declaration.Name, JsonSerializerOptions.Web)
        };

        if (!string.IsNullOrWhiteSpace(declaration.Description))
            extra["description"] = JsonSerializer.SerializeToElement(declaration.Description, JsonSerializerOptions.Web);

        if (declaration.JsonSchema.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
            extra["parameters"] = JsonSerializer.SerializeToElement(declaration.JsonSchema, JsonSerializerOptions.Web);

        return new ResponseToolDefinition
        {
            Type = "function",
            Extra = extra
        };
    }

    private ChatResponse ToChatResponse(ResponseResult response)
    {
        var parts = new List<AIContent>();

        foreach (var item in response.Output ?? [])
            AppendResponseOutput(parts, item);

        if (parts.OfType<TextContent>().Any() != true && response.Text is not null)
        {
            var fallbackText = ToDisplayText(response.Text);
            if (!string.IsNullOrWhiteSpace(fallbackText))
                parts.Add(new TextContent(fallbackText));
        }

        if (agent.OutputSchema != null)
        {
            var structuredText = parts.OfType<TextContent>().Select(a => a.Text).FirstOrDefault(a => !string.IsNullOrWhiteSpace(a))
                ?? SerializeStructuredOutput(response.Text);

            if (!string.IsNullOrWhiteSpace(structuredText))
            {
                parts.Add(new DataContent(Encoding.UTF8.GetBytes(structuredText), MediaTypeNames.Application.Json)
                {
                    Name = agent.GetOutputName()
                });
            }
        }

        foreach (var pair in ElicitPairs?.Values ?? [])
        {
            parts.Add(new DataContent(
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(pair.Request)),
                MediaTypeNames.Application.Json)
            {
                Name = "elicitation-request-" + pair.Request.Mode
            });

            if (pair.Result != null)
            {
                parts.Add(new DataContent(
                    Encoding.UTF8.GetBytes(JsonSerializer.Serialize(pair.Result)),
                    MediaTypeNames.Application.Json)
                {
                    Name = "elicitation-result-" + pair.Result.Action
                });
            }
        }

        return new ChatResponse
        {
            CreatedAt = response.CreatedAt > 0
                ? DateTimeOffset.FromUnixTimeSeconds(response.CreatedAt)
                : DateTimeOffset.UtcNow,
            Usage = ToUsageDetails(response.Usage),
            ResponseId = response.Id,
            Messages = [new ChatMessage(ChatRole.Assistant, parts)]
        };
    }

    private void AppendResponseOutput(List<AIContent> parts, object item)
    {
        var json = item is JsonElement element
            ? element
            : JsonSerializer.SerializeToElement(item, ResponseJson.Default);

        if (json.ValueKind != JsonValueKind.Object)
            return;

        var type = json.TryGetProperty("type", out var typeProperty)
            ? typeProperty.GetString()
            : null;

        switch (type)
        {
            case "message":
                AppendResponseMessageContent(parts, json);
                return;

            case "reasoning":
                AppendReasoningContent(parts, json);
                return;

            case "function_call":
                AppendFunctionCall(parts, json);
                return;

            default:
                return;
        }
    }

    private static void AppendResponseMessageContent(List<AIContent> parts, JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return;

        foreach (var part in content.EnumerateArray())
        {
            if (!part.TryGetProperty("type", out var typeProperty))
                continue;

            switch (typeProperty.GetString())
            {
                case "output_text":
                case "input_text":
                    var text = part.TryGetProperty("text", out var textProperty)
                        ? textProperty.GetString()
                        : null;
                    if (!string.IsNullOrWhiteSpace(text))
                        parts.Add(new TextContent(text));
                    break;

                case "input_image":
                    var imageUrl = part.TryGetProperty("image_url", out var imageProperty)
                        ? imageProperty.GetString()
                        : null;
                    if (!string.IsNullOrWhiteSpace(imageUrl))
                        parts.Add(new DataContent(imageUrl, GuessMediaType(imageUrl) ?? MediaTypeNames.Application.Octet));
                    break;

                case "input_file":
                    var filePayload = part.TryGetProperty("file_data", out var fileDataProperty)
                        ? fileDataProperty.GetString()
                        : part.TryGetProperty("file_url", out var fileUrlProperty)
                            ? fileUrlProperty.GetString()
                            : null;

                    if (!string.IsNullOrWhiteSpace(filePayload))
                    {
                        parts.Add(new DataContent(filePayload, GuessMediaType(filePayload) ?? MediaTypeNames.Application.Octet)
                        {
                            Name = part.TryGetProperty("filename", out var filenameProperty)
                                ? filenameProperty.GetString()
                                : null
                        });
                    }

                    break;
            }
        }
    }

    private static void AppendReasoningContent(List<AIContent> parts, JsonElement reasoning)
    {
        if (!reasoning.TryGetProperty("encrypted_content", out var encryptedContent)
            || encryptedContent.ValueKind != JsonValueKind.String)
            return;

        var protectedData = encryptedContent.GetString();
        if (string.IsNullOrWhiteSpace(protectedData))
            return;

        parts.Add(new TextReasoningContent(string.Empty)
        {
            ProtectedData = protectedData
        });
    }

    private static void AppendFunctionCall(List<AIContent> parts, JsonElement functionCall)
    {
        var callId = functionCall.TryGetProperty("call_id", out var callIdProperty)
            ? callIdProperty.GetString()
            : functionCall.TryGetProperty("id", out var idProperty)
                ? idProperty.GetString()
                : null;

        var name = functionCall.TryGetProperty("name", out var nameProperty)
            ? nameProperty.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(callId) || string.IsNullOrWhiteSpace(name))
            return;

        var argumentsText = functionCall.TryGetProperty("arguments", out var argumentsProperty)
            ? argumentsProperty.ValueKind == JsonValueKind.String
                ? argumentsProperty.GetString()
                : argumentsProperty.GetRawText()
            : null;

        parts.Add(new FunctionCallContent(
            callId,
            name,
            DeserializeArguments(argumentsText)));
    }

    private static IDictionary<string, object?> DeserializeArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return new Dictionary<string, object?>();

        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(arguments);
            if (json.ValueKind == JsonValueKind.Object)
            {
                return JsonSerializer.Deserialize<Dictionary<string, object?>>(json.GetRawText(), JsonSerializerOptions.Web)
                    ?? new Dictionary<string, object?>();
            }
        }
        catch
        {
            // fall through to empty args
        }

        return new Dictionary<string, object?>();
    }

    private static UsageDetails? ToUsageDetails(object? usage)
    {
        if (usage is null)
            return null;

        var json = usage is JsonElement element
            ? element
            : JsonSerializer.SerializeToElement(usage, ResponseJson.Default);

        if (json.ValueKind != JsonValueKind.Object)
            return null;

        return new UsageDetails
        {
            TotalTokenCount = ReadLong(json, "total_tokens"),
            InputTokenCount = ReadLong(json, "input_tokens") ?? ReadLong(json, "prompt_tokens"),
            OutputTokenCount = ReadLong(json, "output_tokens") ?? ReadLong(json, "completion_tokens")
        };
    }

    private static long? ReadLong(JsonElement json, string propertyName)
        => json.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number
            ? property.GetInt64()
            : null;

    private static ResponseRole ToResponseRole(ChatRole role)
        => role == ChatRole.Assistant ? ResponseRole.Assistant
            : role == ChatRole.System ? ResponseRole.System
            : ResponseRole.User;

    private static string SerializeResponseValue(object? value)
    {
        if (value is null)
            return "null";

        if (value is JsonElement element)
            return element.GetRawText();

        return JsonSerializer.Serialize(value, JsonSerializerOptions.Web);
    }

    private static string? SerializeStructuredOutput(object? value)
    {
        if (value is null)
            return null;

        if (value is JsonElement element)
            return element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText();

        if (value is string text)
            return text;

        return JsonSerializer.Serialize(value, JsonSerializerOptions.Web);
    }

    private static string? ToDisplayText(object? value)
    {
        if (value is null)
            return null;

        if (value is JsonElement element)
            return element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText();

        return value as string ?? JsonSerializer.Serialize(value, JsonSerializerOptions.Web);
    }

    private static string? GuessMediaType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return null;

        var end = value.IndexOf(';');
        return end > 5 ? value[5..end] : null;
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

    private static string? ToDataUrl(DataContent content)
    {
        var base64Data = content.Base64Data.ToString();
        if (string.IsNullOrWhiteSpace(base64Data))
            return null;

        return $"data:{content.MediaType};base64,{base64Data}";
    }
}
