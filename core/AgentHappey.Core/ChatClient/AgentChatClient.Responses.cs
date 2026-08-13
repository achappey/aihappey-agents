using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Abstractions.Http;
using AIHappey.Responses;
using AgentHappey.Common.Extensions;
using Microsoft.Extensions.AI;
using System.Text.RegularExpressions;

namespace AgentHappey.Core.ChatClient;

public partial class AgentChatClient
{
    private readonly Dictionary<string, ResponseCaller> responseCallers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ResponseProgramItem> responsePrograms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ResponseProgramOutputItem> responseProgramOutputs = new(StringComparer.Ordinal);

    private ResponseRequest BuildResponseRequest(IEnumerable<ChatMessage> messages, ChatOptions? options)
        => new()
        {
            Model = agent.Model.Id,
            Input = new ResponseInput(ToResponseInputItems(messages)),
            Tools = BuildResponseToolDefinitions(options?.Tools),
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

    private static readonly HashSet<string> SideInferenceOwnedProviderOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "background",
        "conversation",
        "input",
        "instructions",
        "max_tool_calls",
        "maxToolCalls",
        "metadata",
        "model",
        "parallel_tool_calls",
        "parallelToolCalls",
        "previous_response_id",
        "previousResponseId",
        "prompt",
        "providerMetadata",
        "stream",
        "tool_choice",
        "toolChoice",
        "tools"
    };

    /// <summary>
    /// Preserves provider/model tuning for a side-inference request while ensuring
    /// provider metadata cannot inject tools, replay state, or override the request's
    /// input and instructions. Some providers materialize metadata.tools after the
    /// request has already supplied an empty tool list, so Tools = [] alone is not
    /// sufficient to guarantee a tool-free inference call.
    /// </summary>
    private Dictionary<string, object?>? BuildSideInferenceProviderMetadata()
    {
        if (agent.Model.ProviderMetadata is not { Count: > 0 } providerMetadata)
            return null;

        var source = JsonSerializer.SerializeToElement(providerMetadata, JsonSerializerOptions.Web);
        if (source.ValueKind != JsonValueKind.Object)
            return null;

        var safeOptions = source.EnumerateObject()
            .Where(property => !SideInferenceOwnedProviderOptions.Contains(property.Name))
            .ToDictionary(
                property => property.Name,
                property => property.Value.Clone(),
                StringComparer.Ordinal);

        return safeOptions.Count == 0
            ? null
            : new Dictionary<string, object?>
            {
                [GetProviderKey()] = JsonSerializer.SerializeToElement(safeOptions, JsonSerializerOptions.Web)
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

    private List<ResponseToolDefinition>? BuildResponseToolDefinitions(IList<AITool>? tools)
    {
        if (tools is null)
            return null;

        var definitions = new List<ResponseToolDefinition>();
        var namespaceGroups = new Dictionary<string, NamespaceToolGroup>(StringComparer.Ordinal);

        foreach (var declaration in tools.OfType<AIFunctionDeclaration>())
        {
            if (string.Equals(declaration.Name, ClientToolSearchName, StringComparison.Ordinal)
                && !HasAgentTool(ToolSearchType))
            {
                continue;
            }

            if (string.Equals(declaration.Name, ResourceSearchName, StringComparison.Ordinal)
                && !HasAgentTool(ResourceSearchType))
            {
                continue;
            }

            if (!McpToolSources.TryGetValue(declaration.Name, out var source))
            {
                definitions.Add(ToResponseToolDefinition(declaration));
                continue;
            }

            var function = ToResponseToolDefinition(declaration, source.Configuration);
            if (source.Configuration.Namespace != true)
            {
                definitions.Add(function);
                continue;
            }

            if (!namespaceGroups.TryGetValue(source.ServerId, out var group))
            {
                var displayName = source.ServerInfo.Name ?? source.ServerId;
                group = new NamespaceToolGroup(
                    displayName,
                    !string.IsNullOrWhiteSpace(source.ServerInfo.Description)
                        ? source.ServerInfo.Description
                        : $"Tools provided by {displayName}.",
                    []);
                namespaceGroups[source.ServerId] = group;
            }

            group.Tools.Add(function);
        }

        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in namespaceGroups.Values)
        {
            var baseName = SanitizeNamespaceName(group.ProposedName);
            var name = baseName;
            var suffix = 2;
            while (!used.Add(name))
                name = $"{baseName[..Math.Min(baseName.Length, 60)]}_{suffix++}";

            definitions.Add(new ResponseToolDefinition
            {
                Type = "namespace",
                Extra = new Dictionary<string, JsonElement>
                {
                    ["name"] = JsonSerializer.SerializeToElement(name, JsonSerializerOptions.Web),
                    ["description"] = JsonSerializer.SerializeToElement(group.Description, JsonSerializerOptions.Web),
                    ["tools"] = JsonSerializer.SerializeToElement(group.Tools, ResponseJson.Default)
                }
            });
        }

        return definitions;
    }

    private static string SanitizeNamespaceName(string? value)
    {
        var name = Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), "[^a-z0-9_-]+", "_");
        name = Regex.Replace(name, "^[_-]+|[_-]+$", string.Empty);
        if (name.Length > 64)
            name = name[..64];
        return string.IsNullOrWhiteSpace(name) ? "tools" : name;
    }

    private sealed record NamespaceToolGroup(
        string ProposedName,
        string Description,
        List<ResponseToolDefinition> Tools);

    private List<ResponseInputItem> ToResponseInputItems(IEnumerable<ChatMessage> messages)
    {
        var items = new List<ResponseInputItem>();

        foreach (var message in messages)
        {
            if (message.Role == ChatRole.Tool)
            {
                foreach (var result in message.Contents.OfType<FunctionResultContent>())
                {
                    items.Add(TryCreateToolSearchOutputItem(result, out var toolSearchOutput)
                        ? toolSearchOutput
                        : ToResponseFunctionCallOutputItem(result));
                }

                continue;
            }

            foreach (var item in ToResponseInputItems(message))
                items.Add(item);
        }

        return InsertReferencedPrograms(items);
    }

    private List<ResponseInputItem> InsertReferencedPrograms(List<ResponseInputItem> items)
    {
        var result = new List<ResponseInputItem>(items.Count);
        var insertedProgramIds = new HashSet<string>(
            items.OfType<ResponseProgramItem>().SelectMany(GetProgramReplayIds),
            StringComparer.Ordinal);
        var insertedProgramOutputCallIds = new HashSet<string>(
            items.OfType<ResponseProgramOutputItem>().Select(output => output.CallId),
            StringComparer.Ordinal);

        var linkedPrograms = items
            .Select((item, index) => new { CallerId = GetCallerId(item), Index = index })
            .Where(entry => !string.IsNullOrWhiteSpace(entry.CallerId))
            .Select(entry => TryGetResponseProgram(entry.CallerId!, out var program)
                ? new { Program = program, entry.Index }
                : null)
            .Where(entry => entry is not null)
            .Select(entry => entry!)
            .GroupBy(entry => entry.Program.CallId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => new
                {
                    Program = group.First().Program,
                    FirstIndex = group.Min(entry => entry.Index),
                    LastIndex = group.Max(entry => entry.Index)
                },
                StringComparer.Ordinal);

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var linkedProgram = linkedPrograms.Values.FirstOrDefault(link => link.FirstIndex == index);
            if (linkedProgram is not null
                && !GetProgramReplayIds(linkedProgram.Program).Any(insertedProgramIds.Contains))
            {
                result.Add(linkedProgram.Program);
                insertedProgramIds.UnionWith(GetProgramReplayIds(linkedProgram.Program));
            }

            result.Add(item);

            linkedProgram = linkedPrograms.Values.FirstOrDefault(link => link.LastIndex == index);
            if (linkedProgram is not null
                && !insertedProgramOutputCallIds.Contains(linkedProgram.Program.CallId)
                && responseProgramOutputs.TryGetValue(linkedProgram.Program.CallId, out var programOutput))
            {
                result.Add(programOutput);
                insertedProgramOutputCallIds.Add(programOutput.CallId);
            }
        }

        return result;
    }

    private static string? GetCallerId(ResponseInputItem item)
        => item switch
        {
            ResponseFunctionCallItem call => call.Caller?.CallerId,
            ResponseFunctionCallOutputItem output => output.Caller?.CallerId,
            _ => null
        };

    private static IEnumerable<string> GetProgramReplayIds(ResponseProgramItem program)
    {
        if (!string.IsNullOrWhiteSpace(program.Id))
            yield return program.Id;
        if (!string.IsNullOrWhiteSpace(program.CallId))
            yield return program.CallId;
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

                    if (IsToolSearchCall(call))
                    {
                        yield return ToResponseToolSearchCallItem(call);
                        break;
                    }

                    if (TryReadResponseItem<ResponseProgramItem>(call.RawRepresentation, out var program))
                    {
                        RegisterResponseProgram(program);
                        yield return program;
                        break;
                    }

                    yield return new ResponseFunctionCallItem
                    {
                        Id = ReadResponseMetadataString(call.RawRepresentation, "item_id"),
                        CallId = call.CallId,
                        Name = call.Name,
                        Arguments = SerializeResponseValue(call.Arguments),
                        Namespace = ReadResponseMetadataString(call.RawRepresentation, "namespace"),
                        Status = ReadResponseMetadataString(call.RawRepresentation, "status"),
                        Caller = ReadResponseCaller(call.RawRepresentation)
                            ?? GetResponseCaller(call.CallId)
                    };
                    break;

                case FunctionResultContent result:
                    foreach (var item in FlushResponseInputMessage(message, contentParts))
                        yield return item;

                    if (TryCreateToolSearchOutputItem(result, out var toolSearchOutput))
                    {
                        yield return toolSearchOutput;
                        break;
                    }

                    if (TryReadResponseItem<ResponseProgramOutputItem>(result.Result, out var programOutput))
                    {
                        RegisterResponseProgramOutput(programOutput);
                        yield return programOutput;
                        break;
                    }

                    yield return ToResponseFunctionCallOutputItem(result);
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

    private ResponseFunctionCallOutputItem ToResponseFunctionCallOutputItem(FunctionResultContent result)
        => new()
        {
            CallId = result.CallId,
            Output = SerializeResponseValue(result.Result),
            Status = "completed",
            Caller = ReadResponseCaller(result.RawRepresentation)
                ?? GetResponseCaller(result.CallId)
        };

    private static bool IsToolSearchCall(FunctionCallContent call)
        => string.Equals(
            ReadResponseMetadataString(call.RawRepresentation, "responses_type"),
            "tool_search_call",
            StringComparison.Ordinal)
           || string.Equals(
               ReadNestedResponseItemType(call.RawRepresentation),
               "tool_search_call",
               StringComparison.Ordinal);

    private static ResponseToolSearchCallItem ToResponseToolSearchCallItem(FunctionCallContent call)
    {
        if (TryReadResponseItem<ResponseToolSearchCallItem>(call.RawRepresentation, out var nativeItem))
            return nativeItem;

        var execution = ReadResponseMetadataString(call.RawRepresentation, "execution") ?? "client";
        return new ResponseToolSearchCallItem
        {
            Id = ReadResponseMetadataString(call.RawRepresentation, "item_id"),
            CallId = string.Equals(execution, "client", StringComparison.OrdinalIgnoreCase)
                ? call.CallId
                : ReadResponseMetadataString(call.RawRepresentation, "native_call_id"),
            Execution = execution,
            Status = ReadResponseMetadataString(call.RawRepresentation, "status") ?? "completed",
            Arguments = JsonSerializer.SerializeToElement(
                call.Arguments ?? new Dictionary<string, object?>(),
                JsonSerializerOptions.Web)
        };
    }

    private bool TryCreateToolSearchOutputItem(
        FunctionResultContent result,
        out ResponseToolSearchOutputItem output)
    {
        output = null!;
        if (TryReadResponseItem<ResponseToolSearchOutputItem>(result.Result, out var nativeItem))
        {
            output = nativeItem;
            return true;
        }

        if (string.IsNullOrWhiteSpace(result.CallId)
            || !responseToolSearchCalls.ContainsKey(result.CallId))
            return false;

        var tools = ExtractSelectedToolDefinitions(result.Result);
        output = new ResponseToolSearchOutputItem
        {
            CallId = result.CallId,
            Execution = "client",
            Status = "completed",
            Tools = tools
        };
        return true;
    }

    private static string? ReadNestedResponseItemType(object? value)
    {
        var json = TryReadRawRepresentation(value);
        return json is { ValueKind: JsonValueKind.Object }
            && json.Value.TryGetProperty("responses_item", out var item)
            && item.ValueKind == JsonValueKind.Object
            && item.TryGetProperty("type", out var type)
            && type.ValueKind == JsonValueKind.String
                ? type.GetString()
                : null;
    }

    private static bool TryReadResponseItem<T>(object? value, out T item)
        where T : ResponseInputItem
    {
        item = null!;
        var json = TryReadRawRepresentation(value);
        if (json is not { ValueKind: JsonValueKind.Object }
            || !json.Value.TryGetProperty("responses_item", out var responseItem)
            || responseItem.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        try
        {
            var parsed = responseItem.Deserialize<T>(ResponseJson.Default);
            if (parsed is null)
                return false;

            item = parsed;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static List<ResponseToolDefinition> ExtractSelectedToolDefinitions(object? result)
    {
        var json = ToJsonElement(result ?? new { });
        JsonElement selectedTools;
        if (json.ValueKind == JsonValueKind.Object
            && json.TryGetProperty("structuredContent", out var structured)
            && structured.ValueKind == JsonValueKind.Object
            && structured.TryGetProperty("selectedTools", out selectedTools)
            && selectedTools.ValueKind == JsonValueKind.Array)
        {
            return selectedTools.EnumerateArray()
                .Select(ToSelectedResponseToolDefinition)
                .Where(tool => tool is not null)
                .Cast<ResponseToolDefinition>()
                .ToList();
        }

        if (json.ValueKind == JsonValueKind.Object
            && json.TryGetProperty("selectedTools", out selectedTools)
            && selectedTools.ValueKind == JsonValueKind.Array)
        {
            return selectedTools.EnumerateArray()
                .Select(ToSelectedResponseToolDefinition)
                .Where(tool => tool is not null)
                .Cast<ResponseToolDefinition>()
                .ToList();
        }

        return [];
    }

    private static ResponseToolDefinition? ToSelectedResponseToolDefinition(JsonElement tool)
    {
        if (tool.ValueKind != JsonValueKind.Object
            || !tool.TryGetProperty("name", out var name)
            || name.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(name.GetString()))
        {
            return null;
        }

        var extra = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["name"] = name.Clone(),
            ["parameters"] = tool.TryGetProperty("inputSchema", out var inputSchema)
                ? inputSchema.Clone()
                : JsonSerializer.SerializeToElement(new { type = "object", properties = new { } })
        };

        if (tool.TryGetProperty("description", out var description)
            && description.ValueKind == JsonValueKind.String)
        {
            extra["description"] = description.Clone();
        }

        if (tool.TryGetProperty("outputSchema", out var outputSchema)
            && outputSchema.ValueKind == JsonValueKind.Object)
        {
            extra["output_schema"] = outputSchema.Clone();
        }

        return new ResponseToolDefinition
        {
            Type = "function",
            Extra = extra
        };
    }

    private ResponseCaller? GetResponseCaller(string? callId)
        => !string.IsNullOrWhiteSpace(callId)
            && responseCallers.TryGetValue(callId, out var caller)
                ? caller
                : null;

    private void RegisterResponseCaller(string? itemId, string? callId, ResponseCaller? caller)
    {
        if (caller is null)
            return;

        if (!string.IsNullOrWhiteSpace(itemId))
            responseCallers[itemId] = caller;
        if (!string.IsNullOrWhiteSpace(callId))
            responseCallers[callId] = caller;
    }

    private bool TryGetResponseProgram(string callerId, out ResponseProgramItem program)
    {
        if (responsePrograms.TryGetValue(callerId, out program!))
            return true;

        program = responsePrograms.Values.FirstOrDefault(item =>
            string.Equals(item.Id, callerId, StringComparison.Ordinal)
            || string.Equals(item.CallId, callerId, StringComparison.Ordinal))!;
        return program is not null;
    }

    private void RegisterResponseProgram(ResponseProgramItem program)
    {
        if (!string.IsNullOrWhiteSpace(program.Id))
            responsePrograms[program.Id] = program;
        if (!string.IsNullOrWhiteSpace(program.CallId))
            responsePrograms[program.CallId] = program;
    }

    private void RegisterResponseProgramOutput(ResponseProgramOutputItem output)
    {
        if (!string.IsNullOrWhiteSpace(output.CallId))
            responseProgramOutputs[output.CallId] = output;
    }

    private static string? ReadResponseMetadataString(object? rawRepresentation, string propertyName)
    {
        var json = TryReadRawRepresentation(rawRepresentation);
        return json is { ValueKind: JsonValueKind.Object }
            && json.Value.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    private static ResponseCaller? ReadResponseCaller(object? rawRepresentation)
    {
        var json = TryReadRawRepresentation(rawRepresentation);
        if (json is not { ValueKind: JsonValueKind.Object }
            || !json.Value.TryGetProperty("caller", out var caller)
            || caller.ValueKind != JsonValueKind.Object)
            return null;

        return caller.Deserialize<ResponseCaller>(JsonSerializerOptions.Web);
    }

    private static JsonElement? TryReadRawRepresentation(object? rawRepresentation)
    {
        if (rawRepresentation is null)
            return null;

        try
        {
            return rawRepresentation is JsonElement element
                ? element.Clone()
                : JsonSerializer.SerializeToElement(rawRepresentation, JsonSerializerOptions.Web);
        }
        catch
        {
            return null;
        }
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

    private static ResponseToolDefinition ToResponseToolDefinition(
        AIFunctionDeclaration declaration,
        AgentHappey.Common.Models.McpServer? server = null)
    {
        var extra = new Dictionary<string, JsonElement>
        {
            ["name"] = JsonSerializer.SerializeToElement(declaration.Name, JsonSerializerOptions.Web)
        };

        if (!string.IsNullOrWhiteSpace(declaration.Description))
            extra["description"] = JsonSerializer.SerializeToElement(declaration.Description, JsonSerializerOptions.Web);


        if (declaration.JsonSchema.ValueKind is not JsonValueKind.Undefined
            and not JsonValueKind.Null)
        {
            extra["parameters"] = declaration.JsonSchema.Clone();
        }

        if (declaration.ReturnJsonSchema is
            {
                ValueKind: not JsonValueKind.Undefined
            and not JsonValueKind.Null
            } returnJsonSchema)
        {
            extra["output_schema"] = returnJsonSchema.Clone();
        }

        if (declaration.AdditionalProperties?.TryGetValue(
                   "allowed_callers",
                   out var allowedCallers) == true &&
               allowedCallers is not null)
        {
            if (allowedCallers is JsonElement element)
            {
                if (element.ValueKind is not JsonValueKind.Undefined
                    and not JsonValueKind.Null)
                {
                    extra["allowed_callers"] = element.Clone();
                }
            }
            else
            {
                extra["allowed_callers"] = JsonSerializer.SerializeToElement(
                    allowedCallers,
                    allowedCallers.GetType(),
                    JsonSerializerOptions.Web);
            }
        }

        if (declaration.AdditionalProperties?.TryGetValue(
                "defer_loading",
                out var deferLoading) == true &&
            deferLoading is not null)
        {
            if (deferLoading is JsonElement element)
            {
                if (element.ValueKind is not JsonValueKind.Undefined
                    and not JsonValueKind.Null)
                {
                    extra["defer_loading"] = element.Clone();
                }
            }
            else
            {
                extra["defer_loading"] = JsonSerializer.SerializeToElement(
                    deferLoading,
                    deferLoading.GetType(),
                    JsonSerializerOptions.Web);
            }
        }

        var serverAllowedCallers = server?.AllowedCallers?
            .Where(caller => caller is "direct" or "programmatic")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (serverAllowedCallers is { Length: > 0 })
            extra["allowed_callers"] = JsonSerializer.SerializeToElement(serverAllowedCallers, JsonSerializerOptions.Web);

        if (server?.DeferLoading == true)
            extra["defer_loading"] = JsonSerializer.SerializeToElement(true, JsonSerializerOptions.Web);

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

        if (parts.OfType<TextContent>().Any() != true && response.OutputText is not null)
        {
            parts.Add(new TextContent(response.OutputText));
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

        if (response.Metadata is not null)
        {
            parts.Add(new DataContent(
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response.Metadata, JsonSerializerOptions.Web)),
                MediaTypeNames.Application.Json)
            {
                Name = "finish_metadata"
            });
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
            Messages = [new ChatMessage(ChatRole.Assistant, parts)],
            AdditionalProperties = response.Metadata is null
                ? null
                : new AdditionalPropertiesDictionary(response.Metadata)
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

            case "tool_search_call":
                AppendHostedToolSearchCall(parts, json);
                return;

            case "tool_search_output":
                AppendHostedToolSearchOutput(parts, json);
                return;

            case "program":
                RegisterProgram(json);
                AppendProgramCall(parts, json);
                return;

            case "program_output":
                RegisterProgramOutput(json);
                AppendProgramOutput(parts, json);
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

    private void AppendFunctionCall(List<AIContent> parts, JsonElement functionCall)
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

        var caller = functionCall.TryGetProperty("caller", out var callerProperty)
            && callerProperty.ValueKind == JsonValueKind.Object
                ? callerProperty.Deserialize<ResponseCaller>(JsonSerializerOptions.Web)
                : null;
        var itemId = functionCall.TryGetProperty("id", out var itemIdProperty)
            ? itemIdProperty.GetString()
            : null;
        RegisterResponseCaller(itemId, callId, caller);

        parts.Add(new FunctionCallContent(
            callId,
            name,
            DeserializeArguments(argumentsText))
        {
            RawRepresentation = new Dictionary<string, object?>
            {
                ["item_id"] = itemId,
                ["namespace"] = functionCall.TryGetProperty("namespace", out var namespaceProperty)
                    ? namespaceProperty.GetString()
                    : null,
                ["status"] = functionCall.TryGetProperty("status", out var statusProperty)
                    ? statusProperty.GetString()
                    : null,
                ["caller"] = caller
            }
        });
    }

    private void RegisterProgram(JsonElement program)
    {
        try
        {
            var item = program.Deserialize<ResponseProgramItem>(ResponseJson.Default);
            if (item is not null && !string.IsNullOrWhiteSpace(item.CallId))
                RegisterResponseProgram(item);
        }
        catch
        {
        }
    }

    private static void AppendProgramCall(List<AIContent> parts, JsonElement program)
    {
        var nativeItem = program.Clone();
        var item = program.Deserialize<ResponseProgramItem>(ResponseJson.Default);
        if (item is null || string.IsNullOrWhiteSpace(item.CallId))
            return;

        parts.Add(new FunctionCallContent(item.CallId, "program", new Dictionary<string, object?>
        {
            ["code"] = item.Code
        })
        {
            InformationalOnly = true,
            RawRepresentation = new Dictionary<string, object?>
            {
                ["responses_type"] = "program",
                ["item_id"] = item.Id,
                ["call_id"] = item.CallId,
                ["responses_item"] = nativeItem,
                ["provider_metadata"] = CreateNativeResponseProviderMetadata(nativeItem),
                ["title"] = "program"
            }
        });
    }

    private static void AppendProgramOutput(List<AIContent> parts, JsonElement programOutput)
    {
        var item = programOutput.Deserialize<ResponseProgramOutputItem>(ResponseJson.Default);
        if (item is null || string.IsNullOrWhiteSpace(item.CallId))
            return;

        parts.Add(new FunctionResultContent(item.CallId, CreateNativeToolOutputEnvelope(
            new { result = item.Result, status = item.Status },
            programOutput)));
    }

    private static void AppendHostedToolSearchCall(List<AIContent> parts, JsonElement call)
    {
        var item = call.Deserialize<ResponseToolSearchCallItem>(ResponseJson.Default);
        if (item is null)
            return;

        var lifecycleId = item.CallId ?? item.Id ?? Guid.NewGuid().ToString("N");
        parts.Add(new FunctionCallContent(lifecycleId, "tool_search", DeserializeArguments(item.Arguments.GetRawText()))
        {
            InformationalOnly = !string.Equals(item.Execution, "client", StringComparison.OrdinalIgnoreCase),
            RawRepresentation = new Dictionary<string, object?>
            {
                ["responses_type"] = "tool_search_call",
                ["item_id"] = item.Id,
                ["native_call_id"] = item.CallId,
                ["execution"] = item.Execution,
                ["status"] = item.Status,
                ["responses_item"] = call.Clone(),
                ["provider_metadata"] = CreateNativeResponseProviderMetadata(call),
                ["title"] = "tool_search"
            }
        });
    }

    private static void AppendHostedToolSearchOutput(List<AIContent> parts, JsonElement output)
    {
        var item = output.Deserialize<ResponseToolSearchOutputItem>(ResponseJson.Default);
        if (item is null)
            return;

        var lifecycleId = item.CallId ?? item.Id;
        if (string.IsNullOrWhiteSpace(lifecycleId))
            return;

        parts.Add(new FunctionResultContent(lifecycleId, CreateNativeToolOutputEnvelope(item.Tools, output)));
    }

    private static Dictionary<string, Dictionary<string, object>?> CreateNativeResponseProviderMetadata(JsonElement item)
        => new(StringComparer.Ordinal)
        {
            ["openai"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = item.TryGetProperty("type", out var type) ? type.GetString() : null,
                ["id"] = item.TryGetProperty("id", out var id) ? id.GetString() : null,
                ["call_id"] = item.TryGetProperty("call_id", out var callId) && callId.ValueKind == JsonValueKind.String
                    ? callId.GetString()
                    : null,
                ["responses_item"] = item.Clone()
            }
        };

    private static Dictionary<string, object?> CreateNativeToolOutputEnvelope(object? output, JsonElement responseItem)
        => new(StringComparer.Ordinal)
        {
            ["output"] = output,
            ["preliminary"] = false,
            ["provider_executed"] = true,
            ["provider_metadata"] = CreateNativeResponseProviderMetadata(responseItem),
            ["responses_item"] = responseItem.Clone()
        };

    private void RegisterProgramOutput(JsonElement programOutput)
    {
        try
        {
            var item = programOutput.Deserialize<ResponseProgramOutputItem>(ResponseJson.Default);
            if (item is not null && !string.IsNullOrWhiteSpace(item.CallId))
                RegisterResponseProgramOutput(item);
        }
        catch
        {
        }
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
