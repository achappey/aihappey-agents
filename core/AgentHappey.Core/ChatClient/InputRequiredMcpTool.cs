using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Text.Json.Nodes;

namespace AgentHappey.Core.ChatClient;

// The original MCP invocation is reconstructed from the tool call in chat history.
// Nothing is retained on the server between the two HTTP requests.
internal sealed class InputRequiredMcpTool(
    McpClientTool declaration, HttpClient httpClient, string serverUrl,
    string? protocolVersion, Implementation clientInfo, ClientCapabilities? capabilities) : DelegatingAIFunction(declaration)
{
    internal string ServerUrl => serverUrl;
    internal string McpName => declaration.ProtocolTool.Name;

    internal Task<object> RetryAsync(CallToolRequestParams request, CancellationToken cancellationToken)
        => CallAsync(httpClient, serverUrl, protocolVersion, clientInfo, capabilities,
            request, declaration.JsonSerializerOptions, cancellationToken);

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var request = new CallToolRequestParams
        {
            Name = declaration.ProtocolTool.Name,
            Arguments = arguments.ToDictionary(pair => pair.Key,
                pair => JsonSerializer.SerializeToElement(pair.Value, declaration.JsonSerializerOptions))
        };
        // Return a private marker instead of throwing: FunctionInvokingChatClient
        // converts tool exceptions into ordinary "Function failed" tool results.
        return await CallAsync(httpClient, serverUrl, protocolVersion, clientInfo, capabilities,
            request, declaration.JsonSerializerOptions, cancellationToken);
    }

    internal static async Task<object> CallAsync(HttpClient httpClient, string serverUrl,
        string? protocolVersion, Implementation clientInfo, ClientCapabilities? capabilities,
        CallToolRequestParams request, JsonSerializerOptions serializerOptions, CancellationToken cancellationToken)
    {
        // Even McpClient.SendRequestAsync intercepts MRTR and requires a live
        // ElicitationHandler. Use the SDK HTTP transport's raw message channel
        // for this one request; it preserves the configured HTTP client/auth.
        await using var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(serverUrl),
            Name = "input-required-tool"
        }, httpClient, ownsHttpClient: false);
        await using var channel = await transport.ConnectAsync(cancellationToken);
        var parameters = JsonSerializer.SerializeToNode(request, serializerOptions)!.AsObject();
        if (protocolVersion == "2026-07-28")
        {
            var meta = parameters["_meta"] as JsonObject ?? new JsonObject();
            meta["io.modelcontextprotocol/protocolVersion"] = protocolVersion;

            meta["io.modelcontextprotocol/clientInfo"] = JsonSerializer.SerializeToNode(clientInfo, serializerOptions);
            if (capabilities is not null)
                meta["io.modelcontextprotocol/clientCapabilities"] = JsonSerializer.SerializeToNode(capabilities, serializerOptions);
            parameters["_meta"] = meta;
        }
        var id = new RequestId(Guid.NewGuid().ToString("N"));
        await channel.SendMessageAsync(new JsonRpcRequest
        {
            Id = id,
            Method = RequestMethods.ToolsCall,
            Params = parameters,
            Context = new JsonRpcMessageContext { ProtocolVersion = protocolVersion }
        }, cancellationToken);
        JsonRpcResponse response;
        while (true)
        {
            var message = await channel.MessageReader.ReadAsync(cancellationToken);
            if (message is JsonRpcError error && error.Id == id)
                throw new McpException(error.Error.Message);
            if (message is JsonRpcResponse matched && matched.Id == id)
            {
                response = matched;
                break;
            }
        }
        var result = JsonSerializer.SerializeToElement(response.Result, serializerOptions);
        if (result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("resultType", out var kind)
            && kind.ValueKind == JsonValueKind.String && kind.GetString() == "input_required")
        {
            var required = result.Deserialize<InputRequiredResult>(serializerOptions)
                ?? throw new InvalidOperationException("Invalid MCP input-required result.");
            return new McpToolInputRequiredException(serverUrl, request, required);
        }
        // McpClientTool.InvokeAsync normally returns serialized CallToolResult.
        // Preserve that result representation (including structured and multimodal content).
        return result;
    }
}

internal sealed class McpToolInputRequiredException(
    string serverUrl, CallToolRequestParams request, InputRequiredResult result) : Exception("MCP tool requires user input")
{
    public string ServerUrl { get; } = serverUrl;
    public CallToolRequestParams Request { get; } = request;
    public InputRequiredResult Result { get; } = result;
}
