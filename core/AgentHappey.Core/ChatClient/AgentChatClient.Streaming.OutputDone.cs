using System.Text.Json;
using AgentHappey.Common.Extensions;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;

namespace AgentHappey.Core.ChatClient;


public partial class AgentChatClient
{
    private IEnumerable<ChatResponseUpdate> ToResponseOutputItemDoneUpdates(
        ResponseOutputItemDone done,
        StreamingResponseState state)
    {
        switch (done.Item.Type)
        {
            case "reasoning":
                {
                    if (done.Item is null)
                        yield break;

                    var protectedData = done.Item.AdditionalProperties != null &&
                                        done.Item.AdditionalProperties.TryGetValue("encrypted_content", out var value)
                                            ? value.ToString()
                                            : null;

                    yield return CreateStreamingUpdate(
                        ChatRole.Assistant,
                        [new TextReasoningContent(string.Empty)
                        {
                            ProtectedData = protectedData
                        }],
                        done.Item.Id);

                    yield break;
                }

            case "custom_tool_call":
                var items = done.Item.AdditionalProperties?["output"];

                yield return new ChatResponseUpdate(
                      ChatRole.Assistant,
                      [new FunctionResultContent(done.Item.Id!, items)])
                {
                    MessageId = done.Item.Id,
                };

                yield break;
            case "image_generation_call":
                {
                    var additionalProps = done.Item.AdditionalProperties;

                    JsonElement resultEl = default;
                    JsonElement formatEl = default;

                    var hasResult = additionalProps?.TryGetValue("result", out resultEl) == true;
                    var hasFormat = additionalProps?.TryGetValue("output_format", out formatEl) == true;

                    var rawBase64String = hasResult
                        && resultEl is JsonElement re
                        && re.ValueKind == JsonValueKind.String
                            ? re.GetString()
                            : null;

                    var outputFormat = hasFormat
                        && formatEl is JsonElement fe
                        && fe.ValueKind == JsonValueKind.String
                            ? fe.GetString()
                            : "png";

                    if (string.IsNullOrEmpty(rawBase64String))
                        yield break;

                    var base64String = rawBase64String;

                    var commaIndex = base64String.IndexOf(',');
                    if (commaIndex >= 0)
                        base64String = base64String[(commaIndex + 1)..];

                    byte[] bytes;

                    try
                    {
                        bytes = Convert.FromBase64String(base64String);
                    }
                    catch (FormatException)
                    {
                        yield break;
                    }

                    var mimeType = outputFormat switch
                    {
                        "png" => "image/png",
                        "jpeg" or "jpg" => "image/jpeg",
                        "webp" => "image/webp",
                        _ => "application/octet-stream"
                    };

                    CallToolResult resultIg = new()
                    {
                        StructuredContent = additionalProps is not null
                            ? JsonSerializer.SerializeToElement(additionalProps)
                            : JsonSerializer.SerializeToElement(new Dictionary<string, object?>()),

                        Content =
                        [
                            ImageContentBlock.FromBytes(bytes, mimeType)
                        ]
                    };

                    yield return new ChatResponseUpdate(
                        ChatRole.Assistant,
                        [
                            new DataContent(base64String.ToDataUri(mimeType), mimeType),
                             new FunctionResultContent(done.Item.Id!, resultIg)
                        ])
                    {
                        MessageId = done.Item.Id,
                    };

                    yield break;
                }

            case "code_interpreter_call":

                JsonElement codeEl;
                JsonElement containerIdEl;

                string code = string.Empty;
                string containerId = string.Empty;

                if (done.Item.AdditionalProperties is { } props)
                {
                    if (props.TryGetValue("code", out var c) && c.ValueKind == JsonValueKind.String)
                    {
                        codeEl = c;
                        code = c.GetString()!;
                    }

                    if (props.TryGetValue("container_id", out var cid) && cid.ValueKind == JsonValueKind.String)
                    {
                        containerIdEl = cid;
                        containerId = cid.GetString()!;
                    }
                }

                yield return new ChatResponseUpdate(
                    ChatRole.Assistant,
                    [new FunctionCallContent(done.Item.Id!, "code_interpreter", new Dictionary<string, object?>()
                            {
                                { "code", code },
                                { "container_id", containerId }
                            })
                            {
                                InformationalOnly = true
                            }])
                {
                    MessageId = done.Item.Id,
                };

                JsonElement outputEl;

                if (done.Item.AdditionalProperties is { } propsOut &&
                    propsOut.TryGetValue("outputs", out var el) &&
                    el.ValueKind == JsonValueKind.Array)
                {
                    outputEl = el;
                }
                else
                {
                    // fallback → empty array
                    outputEl = JsonSerializer.SerializeToElement(Array.Empty<object>());
                }

                yield return new ChatResponseUpdate(
                    ChatRole.Assistant,
                    [new FunctionResultContent(done.Item.Id!, new Dictionary<string, JsonElement>
                        {
                            ["outputs"] = outputEl
                        })])
                {
                    MessageId = done.Item.Id,
                };

                yield break;
            case "web_search_call":

                JsonElement? action = done.Item.AdditionalProperties?["action"];

                if (action is JsonElement aEl && aEl.TryGetProperty("type", out var typeEl) &&
                                            typeEl.ValueKind == JsonValueKind.String)
                {
                    switch (typeEl.GetString())
                    {
                        case "search":
                            var queries = Array.Empty<string>();
                            var query = string.Empty;
                            var sources = Array.Empty<object>();

                            if (aEl.ValueKind == JsonValueKind.Object)
                            {
                                // queries[]
                                if (aEl.TryGetProperty("queries", out var queriesEl) &&
                                    queriesEl.ValueKind == JsonValueKind.Array)
                                {
                                    queries = [.. queriesEl
                                .EnumerateArray()
                                .Where(x => x.ValueKind == JsonValueKind.String)
                                .Select(x => x.GetString()!)];
                                }

                                // query
                                if (aEl.TryGetProperty("query", out var queryEl) &&
                                    queryEl.ValueKind == JsonValueKind.String)
                                {
                                    query = queryEl.GetString()!;
                                }

                                // sources[]
                                if (aEl.TryGetProperty("sources", out var sourcesEl) &&
                                    sourcesEl.ValueKind == JsonValueKind.Array)
                                {
                                    sources = [.. sourcesEl
                                .EnumerateArray()
                                .Select(x => JsonSerializer.Deserialize<object>(x.GetRawText())!)];
                                }
                            }

                            var searchCall = new FunctionCallContent(done.Item.Id!, "web_search", new Dictionary<string, object?>() {
                                { "queries", queries },
                                { "query", query }
                            })
                            {
                                InformationalOnly = true,
                                RawRepresentation = CreateNativeResponseItemRawRepresentation(done.Item)
                            };

                            yield return new ChatResponseUpdate(
                                ChatRole.Assistant,
                                [searchCall])
                            {
                                MessageId = done.Item.Id,
                            };

                            yield break;
                        case "open_page":

                            var url = string.Empty;

                            if (aEl.ValueKind == JsonValueKind.Object)
                            {
                                // query
                                if (aEl.TryGetProperty("url", out var queryEl) &&
                                    queryEl.ValueKind == JsonValueKind.String)
                                {
                                    url = queryEl.GetString()!;
                                }
                            }

                            var openPageCall = new FunctionCallContent(done.Item.Id!, "web_search", new Dictionary<string, object?>() {
                                { "url", url }
                            })
                            {
                                InformationalOnly = true,
                                RawRepresentation = CreateNativeResponseItemRawRepresentation(done.Item)
                            };

                            yield return new ChatResponseUpdate(
                                ChatRole.Assistant,
                                [openPageCall])
                            {
                                MessageId = done.Item.Id,
                            };

                            yield break;

                        default:
                            break;

                    }
                }

                yield break;


        }
    }

    private static Dictionary<string, object?> CreateNativeResponseItemRawRepresentation(ResponseStreamItem item)
        => new(StringComparer.Ordinal)
        {
            ["responses_type"] = item.Type,
            ["responses_item"] = JsonSerializer.SerializeToElement(item, ResponseJson.Default)
        };

}
