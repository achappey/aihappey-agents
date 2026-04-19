using System.Text.Json;
using AgentHappey.Common.Extensions;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;

namespace AgentHappey.Core.ChatClient;


public partial class AgentChatClient
{
    private static IEnumerable<ChatResponseUpdate> ToResponseOutputItemDoneUpdates(
        ResponseOutputItemDone done)
    {
        switch (done.Item.Type)
        {
            case "custom_tool_call":
                yield return new ChatResponseUpdate(
                      ChatRole.Assistant,
                      [new FunctionResultContent(done.Item.Id!, new Dictionary<string, JsonElement>
                        {
                        })])
                {
                    MessageId = done.Item.Id,
                };

                yield break;
            case "image_generation_call":
                var resultEl = done.Item.AdditionalProperties?["result"];
                var formatEl = done.Item.AdditionalProperties?["output_format"];

                var base64String = resultEl is JsonElement re && re.ValueKind == JsonValueKind.String
                    ? re.GetString()
                    : null;

                var outputFormat = formatEl is JsonElement fe && fe.ValueKind == JsonValueKind.String
                    ? fe.GetString()
                    : "png"; // fallback

                if (string.IsNullOrEmpty(base64String))
                    yield break;

                // strip data URL prefix if present
                var commaIndex = base64String.IndexOf(',');
                if (commaIndex >= 0)
                    base64String = base64String[(commaIndex + 1)..];

                var bytes = Convert.FromBase64String(base64String);

                var mimeType = outputFormat switch
                {
                    "png" => "image/png",
                    "jpeg" or "jpg" => "image/jpeg",
                    "webp" => "image/webp",
                    _ => "application/octet-stream"
                };

                CallToolResult resultIg = new()
                {
                    StructuredContent = JsonSerializer.SerializeToElement(new
                    {
                        action = done.Item.AdditionalProperties?["action"].GetString(),
                        revised_prompt = done.Item.AdditionalProperties?["revised_prompt"].GetString(),
                        size = done.Item.AdditionalProperties?["size"].GetString(),
                        quality = done.Item.AdditionalProperties?["quality"].GetString(),
                        background = done.Item.AdditionalProperties?["background"].GetString(),
                        output_format = done.Item.AdditionalProperties?["output_format"].GetString()
                    }),
                    Content =
                    [
                        ImageContentBlock.FromBytes(bytes, mimeType)
                    ]
                };

                yield return new ChatResponseUpdate(
                    ChatRole.Assistant,
                    [new DataContent(base64String.ToDataUri(mimeType), mimeType), new FunctionResultContent(done.Item.Id!, resultIg)])
                {
                    MessageId = done.Item.Id,
                };

                yield break;

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

                            yield return new ChatResponseUpdate(
                                ChatRole.Assistant,
                                [new FunctionCallContent(done.Item.Id!, typeEl.GetString()!, new Dictionary<string, object?>() {
                            { "queries", queries },
                            { "query", query }
                                })
                                {
                                    InformationalOnly = true
                                }])
                            {
                                MessageId = done.Item.Id,
                            };

                            var sourcesIt = JsonSerializer.SerializeToElement(sources);

                            yield return new ChatResponseUpdate(
                                ChatRole.Assistant,
                                [new FunctionResultContent(done.Item.Id!, new Dictionary<string, JsonElement>
                        {
                            ["sources"] = sourcesIt
                        })])
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

                            yield return new ChatResponseUpdate(
                                ChatRole.Assistant,
                                [new FunctionCallContent(done.Item.Id!, typeEl.GetString()!, new Dictionary<string, object?>() {
                            { "url", url }
                                })
                                {
                                    InformationalOnly = true
                                }])
                            {
                                MessageId = done.Item.Id,
                            };

                            yield return new ChatResponseUpdate(
                                ChatRole.Assistant,
                                [new FunctionResultContent(done.Item.Id!, new Dictionary<string, JsonElement>
                                        {
                                        })])
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
}
