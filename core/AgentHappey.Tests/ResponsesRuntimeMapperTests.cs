using System.Text.Json;
using AgentHappey.Core.Responses;
using AIHappey.Responses;
using Microsoft.Extensions.AI;

namespace AgentHappey.Tests;

public sealed class ResponsesRuntimeMapperTests
{
    [Theory]
    [InlineData("Hello")]
    [InlineData("")]
    [InlineData("  ")]
    public void String_input_is_a_single_user_message(string text)
    {
        var mapped = new ResponseRequest { Input = text }.ToChatRuntimeRequest();
        var message = Assert.Single(mapped.Messages);
        Assert.Equal(ChatRole.User, message.Role);
        Assert.Equal(text, Assert.IsType<TextContent>(Assert.Single(message.Contents)).Text);
    }

    [Theory]
    [InlineData("user", "user")]
    [InlineData("assistant", "assistant")]
    [InlineData("system", "system")]
    [InlineData("developer", "system")]
    public void Message_roles_ids_and_content_order_are_preserved(string role, string expectedRole)
    {
        var request = Read($$"""
            {"input":[{"type":"message","id":"msg-1","role":"{{role}}","status":"completed",
              "phase":"final_answer","content":[{"type":"input_text","text":"first"},
              {"type":"output_text","text":"second","annotations":[{"type":"citation"}]}]}]}
            """);
        var message = Assert.Single(request.ToChatRuntimeRequest().Messages);
        Assert.Equal(new ChatRole(expectedRole), message.Role);
        Assert.Equal("msg-1", message.MessageId);
        Assert.Equal(["first", "second"], message.Contents.OfType<TextContent>().Select(part => part.Text));
        if (role == "assistant")
        {
            var native = NativeItem(message.Contents[0]);
            Assert.Equal("final_answer", native.GetProperty("phase").GetString());
            Assert.Single(native.GetProperty("content")[1].GetProperty("annotations").EnumerateArray());
        }
    }

    [Theory]
    [InlineData("workflowType", "workflowMetadata")]
    [InlineData("workflow_type", "workflow_metadata")]
    public void Routing_and_workflow_extensions_are_preserved_without_mutating_the_contract(string typeKey, string metadataKey)
    {
        var request = Read($$$"""
            {"model":"Router","models":["One","",null,"Two"],"input":"Hello",
             "{{{typeKey}}}":"groupchat","{{{metadataKey}}}":{"groupchat":{"maximumIterationCount":3}},
             "metadata":{"agents":[{"name":"InlineAgent"}],"custom":"unchanged"},
             "instructions":"Existing instructions","temperature":0.3,"reasoning":{"effort":"high"}}
            """);
        var before = JsonSerializer.Serialize(request, ResponseJson.Default);
        var mapped = request.ToChatRuntimeRequest();
        Assert.Equal("Router", mapped.Model);
        Assert.Equal(["One", "Two"], mapped.Models);
        Assert.Equal("InlineAgent", Assert.Single(mapped.Agents!).Name);
        Assert.Equal("groupchat", mapped.WorkflowType);
        Assert.Equal(3, mapped.WorkflowMetadata?.Groupchat?.MaximumIterationCount);
        Assert.Equal(before, JsonSerializer.Serialize(request, ResponseJson.Default));
    }

    [Fact]
    public void Null_and_empty_input_retain_runtime_defaults()
    {
        var mapped = new ResponseRequest().ToChatRuntimeRequest();
        Assert.Empty(mapped.Messages);
        Assert.Null(mapped.Models);
        Assert.Null(mapped.Agents);
        Assert.Null(mapped.Tools);
        Assert.Equal("sequential", mapped.WorkflowType);
        Assert.Empty(new ResponseRequest { Input = new ResponseInput([]) }.ToChatRuntimeRequest().Messages);
        Assert.Throws<ArgumentNullException>(() => ResponsesRuntimeMapper.ToChatRuntimeRequest(null!));
    }

    [Fact]
    public void Multimodal_parts_keep_order_urls_bytes_filenames_and_native_image_details()
    {
        var request = Read("""
            {"input":[{"role":"user","content":[
              {"type":"input_text","text":"Inspect"},
              {"type":"input_image","image_url":"https://files.test/image?sig=a%2Fb","detail":"high"},
              {"type":"input_image","image_url":"data:image/png;base64,AQID","detail":"low"},
              {"type":"input_file","file_url":"https://files.test/report.pdf","filename":"report.pdf"},
              {"type":"input_file","file_data":"data:application/pdf;base64,AQID","filename":"inline.pdf"}]}]}
            """);
        var parts = Assert.Single(request.ToChatRuntimeRequest().Messages).Contents;
        Assert.Equal(5, parts.Count);
        Assert.Equal("Inspect", Assert.IsType<TextContent>(parts[0]).Text);
        Assert.Equal("https://files.test/image?sig=a%2Fb", Assert.IsType<UriContent>(parts[1]).Uri.OriginalString);
        Assert.Equal("high", Assert.IsType<InputImagePart>(parts[1].RawRepresentation).Detail);
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.IsType<DataContent>(parts[2]).Data.ToArray());
        Assert.Equal("report.pdf", Assert.IsType<UriContent>(parts[3]).AdditionalProperties?["filename"]);
        Assert.Equal("inline.pdf", Assert.IsType<DataContent>(parts[4]).Name);
    }

    [Fact]
    public void Provider_file_ids_are_framework_file_references_not_base64_payloads()
    {
        var request = Read("""
            {"input":[{"role":"user","content":[
              {"type":"input_image","file_id":"file-image","detail":"high"},
              {"type":"input_file","file_id":"file-document","filename":"notes.pdf"}]}]}
            """);
        var parts = Assert.Single(request.ToChatRuntimeRequest().Messages).Contents;
        var image = Assert.IsType<HostedFileContent>(parts[0]);
        Assert.Equal("file-image", image.FileId);
        Assert.Equal("image/*", image.MediaType);
        var file = Assert.IsType<HostedFileContent>(parts[1]);
        Assert.Equal("file-document", file.FileId);
        Assert.Equal("notes.pdf", file.Name);
    }

    [Theory]
    [InlineData("{\"ok\":true}", "{\"ok\":true}")]
    [InlineData("plain result", "\"plain result\"")]
    [InlineData("[1,2]", "[1,2]")]
    [InlineData("", "{}")]
    [InlineData("null", "{}")]
    public void Function_calls_and_results_keep_correlation_arguments_and_output(string output, string expectedJson)
    {
        var request = new ResponseRequest
        {
            Input = new ResponseInput([
                new ResponseFunctionCallItem { Id = "fc-1", CallId = "call-1", Name = "lookup", Namespace = "catalog",
                    Arguments = "{\"query\":\"news\",\"options\":{\"limit\":3}}",
                    Caller = new ResponseCaller { Type = "program", CallerId = "program-1" } },
                new ResponseFunctionCallOutputItem { Id = "out-1", CallId = "call-1", Output = output }
            ])
        };
        var messages = request.ToChatRuntimeRequest().Messages;
        Assert.Equal([ChatRole.Assistant, ChatRole.Tool], messages.Select(message => message.Role));
        var call = Assert.IsType<FunctionCallContent>(Assert.Single(messages[0].Contents));
        Assert.Equal("call-1", call.CallId);
        Assert.Equal("lookup", call.Name);
        Assert.Equal(3, JsonSerializer.SerializeToElement(call.Arguments).GetProperty("options").GetProperty("limit").GetInt32());
        Assert.Equal("catalog", NativeItem(call).GetProperty("namespace").GetString());
        Assert.Equal("program-1", NativeItem(call).GetProperty("caller").GetProperty("caller_id").GetString());
        var result = Assert.IsType<FunctionResultContent>(Assert.Single(messages[1].Contents));
        Assert.Equal("call-1", result.CallId);
        Assert.Equal(expectedJson, JsonSerializer.Serialize(result.Result));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reasoning_preserves_summaries_encrypted_only_state_and_exact_native_identity(bool encryptedOnly)
    {
        var reasoning = new ResponseReasoningItem
        {
            Id = "rs-original", EncryptedContent = "opaque-encrypted-state",
            Summary = encryptedOnly ? [] : [new() { Text = "first" }, new() { Text = "second" }],
            AdditionalProperties = new() { ["signature"] = JsonSerializer.SerializeToElement("provider-signature") }
        };
        var message = Assert.Single(new ResponseRequest { Input = new ResponseInput([reasoning]) }.ToChatRuntimeRequest().Messages);
        Assert.Equal(ChatRole.Assistant, message.Role);
        Assert.Equal(reasoning.Id, message.MessageId);
        var content = Assert.IsType<TextReasoningContent>(Assert.Single(message.Contents));
        Assert.Equal(encryptedOnly ? "" : "first\nsecond", content.Text);
        Assert.Equal(reasoning.EncryptedContent, content.ProtectedData);
        Assert.Equal("provider-signature", NativeItem(content).GetProperty("signature").GetString());
        Assert.Equal(encryptedOnly ? 0 : 2, NativeItem(content).GetProperty("summary").GetArrayLength());
    }

    [Fact]
    public void Unsupported_item_references_still_fail_explicitly()
    {
        var request = Read("""{"input":[{"type":"item_reference","id":"prior-item"}]}""");
        Assert.Throws<NotSupportedException>(() => request.ToChatRuntimeRequest());
    }

    [Fact]
    public void Client_function_definitions_keep_both_schemas_and_validation()
    {
        var request = Read("""
            {"input":"hello","tools":[{"type":"function","name":"lookup","description":"Find something",
              "parameters":{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]},
              "output_schema":{"type":"object","properties":{"found":{"type":"boolean"}}}},
              {"type":"web_search"}]}
            """);
        var tool = Assert.Single(request.ToChatRuntimeRequest().Tools!);
        Assert.Equal("lookup", tool.Name);
        Assert.Equal("Find something", tool.Description);
        Assert.Equal("boolean", JsonSerializer.SerializeToElement(tool.OutputSchema, JsonSerializerOptions.Web)
            .GetProperty("properties").GetProperty("found").GetProperty("type").GetString());
        var invalid = Read("""{"input":"hello","tools":[{"type":"function","name":" "}]}""");
        Assert.Throws<ArgumentException>(() => invalid.ToChatRuntimeRequest());
    }

    [Theory]
    [InlineData("{\"type\":\"web_search_call\",\"id\":\"web-1\",\"action\":{\"type\":\"search\",\"query\":\"news\"}}", "web_search")]
    [InlineData("{\"type\":\"code_interpreter_call\",\"id\":\"code-1\",\"code\":\"print(1)\",\"outputs\":[]}", "code_interpreter")]
    [InlineData("{\"type\":\"program\",\"call_id\":\"program-1\",\"code\":\"return 1\",\"fingerprint\":\"fp\"}", "program")]
    public void Native_provider_tools_remain_informational_and_keep_the_original_item(string itemJson, string name)
    {
        var content = Assert.IsType<FunctionCallContent>(Assert.Single(Assert.Single(
            Read("{\"input\":[" + itemJson + "]}").ToChatRuntimeRequest().Messages).Contents));
        Assert.True(content.InformationalOnly);
        Assert.Equal(name, content.Name);
        Assert.Equal(JsonDocument.Parse(itemJson).RootElement.GetProperty("type").GetString(), NativeItem(content).GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("client", false)]
    [InlineData("server", true)]
    public void Native_tool_search_keeps_call_output_correlation_and_execution_mode(string execution, bool informational)
    {
        var callId = execution == "client" ? "\"search-call\"" : "null";
        var request = Read($$$"""
            {"input":[{"type":"tool_search_call","id":"search-item","call_id":{{{callId}}},
              "execution":"{{{execution}}}","arguments":{"query":"lookup"}},
              {"type":"tool_search_output","id":"search-output","call_id":{{{callId}}},
              "execution":"{{{execution}}}","tools":[]}]}
            """);
        var messages = request.ToChatRuntimeRequest().Messages;
        var call = Assert.IsType<FunctionCallContent>(Assert.Single(messages[0].Contents));
        var output = Assert.IsType<FunctionResultContent>(Assert.Single(messages[1].Contents));
        Assert.Equal(informational, call.InformationalOnly);
        Assert.Equal(execution == "client" ? "client_tool_search" : "tool_search", call.Name);
        Assert.Equal(call.CallId, output.CallId);
        Assert.Equal(execution, NativeItem(output).GetProperty("execution").GetString());
    }

    [Fact]
    public void Agent_assemblies_do_not_reference_the_gateway_or_its_unified_model()
    {
        foreach (var assembly in new[] { typeof(ResponsesRuntimeMapper).Assembly, typeof(AgentHappey.Common.Extensions.VercelHelpers).Assembly })
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference =>
                reference.Name?.Contains("Unified", StringComparison.OrdinalIgnoreCase) == true
                || reference.Name == "AIHappey.Core");
    }

    private static ResponseRequest Read(string json) => JsonSerializer.Deserialize<ResponseRequest>(json, ResponseJson.Default)!;

    private static JsonElement NativeItem(AIContent content)
        => JsonSerializer.SerializeToElement(content.RawRepresentation, ResponseJson.Default).GetProperty("responses_item");
}
