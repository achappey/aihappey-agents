using System.Text.Json;
using AgentHappey.Common.Extensions;
using AgentHappey.Common.Models;
using AgentHappey.Core.ChatClient;
using AgentHappey.Core.ChatRuntime;
using AIHappey.Responses;
using Microsoft.Extensions.AI;

namespace AgentHappey.Core.Responses;

public static class ResponsesRuntimeMapper
{
    public static ChatRuntimeRequest ToChatRuntimeRequest(this ResponseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var messages = ToChatMessages(request.Input).ToList();
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
            workflowMetadata,
            ReadClientTools(request));
    }

    private static IReadOnlyList<AIHappey.Vercel.Models.Tool>? ReadClientTools(ResponseRequest request)
    {
        var tools = new List<AIHappey.Vercel.Models.Tool>();
        foreach (var definition in request.Tools ?? [])
        {
            if (!string.Equals(definition.Type, "function", StringComparison.Ordinal))
                continue;

            var json = JsonSerializer.SerializeToElement(definition, ResponseJson.Default);
            if (!json.TryGetProperty("name", out var name) || string.IsNullOrWhiteSpace(name.GetString()))
                throw new ArgumentException("Client function tools must have a non-empty name.");

            tools.Add(new AIHappey.Vercel.Models.Tool
            {
                Name = name.GetString()!,
                Description = json.TryGetProperty("description", out var description) ? description.GetString() : null,
                InputSchema = json.TryGetProperty("parameters", out var schema)
                    ? JsonSerializer.Deserialize<AIHappey.Vercel.Models.ToolSchema>(schema, JsonSerializerOptions.Web)
                    : null,
                OutputSchema = json.TryGetProperty("output_schema", out var outputSchema)
                    ? JsonSerializer.Deserialize<AIHappey.Vercel.Models.ToolSchema>(outputSchema, JsonSerializerOptions.Web)
                    : null
            });
        }

        return tools.Count == 0 ? null : tools;
    }

    // Microsoft's conversion extensions accept OpenAI SDK ResponseItem, not our
    // contracts. Map directly and retain the agent's native replay envelopes.
    private static IEnumerable<ChatMessage> ToChatMessages(ResponseInput? input)
    {
        if (input?.IsText == true)
            yield return new ChatMessage(ChatRole.User, input.Text!);

        string? hostedSearchCallId = null;
        foreach (var item in input?.Items ?? [])
        {
            if (item is ResponseToolSearchCallItem { Execution: "server" } search)
                hostedSearchCallId = search.Id;

            foreach (var message in ToChatMessages(item, hostedSearchCallId))
                yield return message;
        }
    }

    private static IEnumerable<ChatMessage> ToChatMessages(ResponseInputItem item, string? hostedSearchCallId)
    {
        switch (item)
        {
            case ResponseInputMessage message:
                yield return new ChatMessage(ParseRole(message.Role), [.. ToContents(message)]) { MessageId = message.Id };
                yield break;

            case ResponseFunctionCallItem call:
                yield return new ChatMessage(ChatRole.Assistant,
                [
                    new FunctionCallContent(call.CallId, call.Name, NormalizeArguments(ParseJsonString(call.Arguments)))
                    {
                        InformationalOnly = VercelHelpers.IsContinuationStateTool(call.Name),
                        RawRepresentation = CreateRawRepresentation(call)
                    }
                ]) { MessageId = call.Id ?? call.CallId };
                yield break;

            case ResponseFunctionCallOutputItem output:
                yield return new ChatMessage(ChatRole.Tool,
                [
                    new FunctionResultContent(output.CallId, NormalizeObject(ParseJsonString(output.Output)) ?? new { })
                    {
                        RawRepresentation = CreateRawRepresentation(output)
                    }
                ]) { MessageId = output.Id ?? output.CallId };
                yield break;

            case ResponseReasoningItem reasoning:
                var text = reasoning.Content is { ValueKind: JsonValueKind.Array } reasoningContent
                    ? string.Join("\n", reasoningContent.EnumerateArray()
                        .Where(part => part.ValueKind == JsonValueKind.Object
                            && part.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                            && (type.GetString() is "reasoning_text" or "summary_text")
                            && part.TryGetProperty("text", out var value) && value.ValueKind == JsonValueKind.String)
                        .Select(part => part.GetProperty("text").GetString()))
                    : string.Empty;
                if (string.IsNullOrEmpty(text))
                    text = string.Join("\n", reasoning.Summary.Select(part => part.Text));
                if (string.IsNullOrEmpty(text) && string.IsNullOrEmpty(reasoning.EncryptedContent))
                    yield break;

                yield return new ChatMessage(ChatRole.Assistant,
                [
                    new TextReasoningContent(text)
                    {
                        ProtectedData = reasoning.EncryptedContent,
                        RawRepresentation = CreateRawRepresentation(reasoning)
                    }
                ]) { MessageId = reasoning.Id };
                yield break;

            case ResponseWebSearchCallItem webSearch:
                yield return NativeToolCall(webSearch, webSearch.Id!, "web_search", webSearch.Action);
                yield break;

            case ResponseCodeInterpreterCallItem code:
                yield return NativeToolCall(code, code.Id!, "code_interpreter", new { code = code.Code });
                yield break;

            case ResponseProgramItem program:
                yield return NativeToolCall(program, program.CallId, "program", new { code = program.Code });
                yield break;

            case ResponseProgramOutputItem programOutput:
                yield return new ChatMessage(ChatRole.Tool,
                    [new FunctionResultContent(programOutput.CallId, CreateRawRepresentation(programOutput))])
                { MessageId = programOutput.Id ?? programOutput.CallId };
                yield break;

            case ResponseMultiAgentCallItem multiAgent:
                yield return NativeToolCall(multiAgent, multiAgent.CallId, "multi_agent", ParseJsonString(multiAgent.Arguments) ?? new { });
                yield break;

            case ResponseMultiAgentCallOutputItem multiAgentOutput:
                yield return new ChatMessage(ChatRole.Tool,
                [
                    new FunctionResultContent(multiAgentOutput.CallId, multiAgentOutput.Output.Clone())
                    {
                        RawRepresentation = CreateRawRepresentation(multiAgentOutput)
                    }
                ]) { MessageId = multiAgentOutput.Id ?? multiAgentOutput.CallId };
                yield break;

            case ResponseToolSearchCallItem search:
                var searchContents = new List<AIContent>();
                AgentChatClient.AppendHostedToolSearchCall(searchContents, JsonSerializer.SerializeToElement(search, ResponseJson.Default));
                yield return new ChatMessage(ChatRole.Assistant, searchContents) { MessageId = search.Id ?? search.CallId };
                yield break;

            case ResponseToolSearchOutputItem searchOutput:
                yield return new ChatMessage(ChatRole.Tool,
                [
                    new FunctionResultContent(searchOutput.CallId ?? hostedSearchCallId ?? "tool_search",
                        new { selectedTools = searchOutput.Tools })
                    {
                        RawRepresentation = CreateRawRepresentation(searchOutput)
                    }
                ]) { MessageId = searchOutput.Id ?? searchOutput.CallId };
                yield break;

            case ResponseItemReference:
                throw new NotSupportedException("Responses item_reference replay is not supported by the agents runtime.");

            default:
                // Opaque/unsupported items are not executable client functions.
                yield break;
        }
    }

    private static ChatMessage NativeToolCall(ResponseInputItem item, string callId, string name, object arguments)
        => new(ChatRole.Assistant,
        [
            new FunctionCallContent(callId, name, NormalizeArguments(arguments))
            {
                InformationalOnly = true,
                RawRepresentation = CreateRawRepresentation(item)
            }
        ]) { MessageId = callId };

    private static Dictionary<string, object?> CreateRawRepresentation(ResponseInputItem item)
        => new(StringComparer.Ordinal)
        {
            ["responses_type"] = item.Type,
            // Provider-native agent names stay inside the item. They are not the
            // application-agent ownership key used by stateless UI replay.
            ["responses_item"] = JsonSerializer.SerializeToElement(item, ResponseJson.Default)
        };

    private static IEnumerable<AIContent> ToContents(ResponseInputMessage message)
    {
        if (message.Content.IsText && !string.IsNullOrWhiteSpace(message.Content.Text))
        {
            yield return new TextContent(message.Content.Text)
            {
                RawRepresentation = message.Role == ResponseRole.Assistant ? CreateRawRepresentation(message) : null
            };
            yield break;
        }

        foreach (var part in message.Content.Parts ?? [])
        {
            AIContent? content = part switch
            {
                InputTextPart text => new TextContent(text.Text),
                OutputTextPart text => new TextContent(text.Text),
                InputImagePart image => ToFileContent(image.ImageUrl, image.FileId, "image/*", null),
                InputFilePart file => ToFileContent(file.FileData ?? file.FileUrl, file.FileId, "application/octet-stream", file.Filename),
                _ => null
            };
            if (content is null)
                continue;

            // Keep authoritative assistant identity and user media's detail/file_id.
            content.RawRepresentation = message.Role == ResponseRole.Assistant
                ? CreateRawRepresentation(message)
                : part;
            yield return content;
        }
    }

    private static AIContent? ToFileContent(string? data, string? fileId, string mediaType, string? filename)
    {
        if (!string.IsNullOrWhiteSpace(data))
        {
            // Let Microsoft's parser retain an embedded MIME type instead of
            // rewriting it with the image/* or octet-stream transport fallback.
            if (data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                && data.Length > 5 && data[5] is not ';' and not ',')
                return new DataContent(data) { Name = filename };

            return data.ToFileContent(mediaType, filename);
        }

        return string.IsNullOrWhiteSpace(fileId)
            ? null
            : new HostedFileContent(fileId) { MediaType = mediaType, Name = filename };
    }

    private static object? ParseJsonString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        try
        {
            return JsonSerializer.Deserialize<JsonElement>(value);
        }
        catch (JsonException)
        {
            return value;
        }
    }

    private static ChatRole ParseRole(ResponseRole role) => role switch
    {
        ResponseRole.Assistant => ChatRole.Assistant,
        ResponseRole.System or ResponseRole.Developer => ChatRole.System,
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

    private static IDictionary<string, object?> NormalizeArguments(object? value)
    {
        if (value is IDictionary<string, object?> arguments)
            return arguments;

        var element = value is JsonElement jsonElement
            ? jsonElement
            : JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);

        return element.ValueKind == JsonValueKind.Object
            ? JsonSerializer.Deserialize<Dictionary<string, object?>>(element.GetRawText(), JsonSerializerOptions.Web)
                ?? new Dictionary<string, object?>()
            : new Dictionary<string, object?>();
    }
}
