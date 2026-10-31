using System.Text.Json;
using AgentHappey.Core.ChatClient;
using AIHappey.Decisions;

namespace AgentHappey.Tests;

public sealed partial class AgentDecisionTests
{
    [Fact]
    public void Decisions_use_the_shared_assembly_without_a_gateway_reference()
    {
        Assert.Equal("AIHappey.Decisions", typeof(OpenAIDecisionRequest).Assembly.GetName().Name);
        var assembly = typeof(AgentChatClient).Assembly;
        Assert.Null(assembly.GetType("AIHappey.Core.Models.OpenAIDecisionRequest"));
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name == "AIHappey.Core");
        Assert.Contains(assembly.GetReferencedAssemblies(), reference => reference.Name == "AIHappey.Decisions");
    }

    [Theory]
    [InlineData("""[{"role":"system","content":"Policy"}]""")]
    [InlineData("""[{"role":"assistant","phase":"commentary","content":"History"}]""")]
    [InlineData("""[{"type":"custom_tool_call","call_id":"c1","name":"lookup","input":"query"}]""")]
    [InlineData("""[{"role":"user","content":[{"type":"input_json","data":{"id":1}}]}]""")]
    [InlineData("""[{"role":"user","content":[{"type":"input_image","image_url":"data:image/png;base64,AQ==","prompt_cache_breakpoint":{"mode":"explicit"}}]}]""")]
    public async Task New_wire_shapes_are_rejected_before_agent_execution(string json)
    {
        var fixture = new Fixture();
        var request = NativeRequest();
        request.Input = JsonSerializer.Deserialize<OpenAIDecisionInput>(json)!;
        var error = await Record.ExceptionAsync(() => fixture.Execute(request));
        Assert.True(error is ArgumentException or NotSupportedException, error?.ToString() ?? "Expected explicit rejection.");
        Assert.Empty(fixture.Sent);
    }

    [Fact]
    public async Task New_question_and_cache_fields_are_not_ignored()
    {
        var fixture = new Fixture();
        var request = NativeRequest();
        request.Questions[0].InstructionContent = new OpenAIDecisionInputPart[] { new OpenAIDecisionInputText { Text = "Check" } };
        await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Execute(request));
        request = NativeRequest();
        request.PromptCacheKey = "caller";
        await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Execute(request));
        request = NativeRequest();
        request.PromptCacheOptions = new() { Mode = "explicit" };
        await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Execute(request));
        Assert.Empty(fixture.Sent);
    }
}
