using System.Text.Json;
using AIHappey.Responses;
using Microsoft.Extensions.AI;

namespace AgentHappey.Tests;

public sealed partial class AgentChatClientFixtureTests
{
    [Theory]
    [InlineData("normal", "anthropic")]
    [InlineData("streaming", "anthropic")]
    [InlineData("decision", "anthropic")]
    [InlineData("tool-search", "anthropic")]
    [InlineData("resource-search", "anthropic")]
    [InlineData("normal", "openai")]
    [InlineData("streaming", "openai")]
    [InlineData("decision", "openai")]
    [InlineData("tool-search", "openai")]
    [InlineData("resource-search", "openai")]
    public async Task Native_provider_options_are_root_fields_on_every_inference_path(string path, string provider)
    {
        var sideInference = path is "tool-search" or "resource-search";
        var configured = JsonSerializer.Deserialize<Dictionary<string, object>>("""
            {
              "max_tokens":128000,"max_completion_tokens":64000,"reasoning":{"effort":"low"},
              "thinking":{"type":"adaptive"},"output_config":{"effort":"high"},
              "container":{"skills":[{"skill_id":"pptx","type":"anthropic","version":"latest"}]},
              "tools":[{"type":"web_search"}],"parallel_tool_calls":false,"tool_choice":"required",
              "model":"injected/model","instructions":"injected instructions","input":"injected input","stream":true,
              "previous_response_id":"provider-continuation","background":true,
              "future_null":null,"future_empty":{},"future_array":[],"future_false":false,
              "future_zero":0,"future_number":9007199254740993,"future_nested":{"items":[1,null,true,{}]},
              "future_case":"lower","future_CASE":"upper",
              "MODEL":"case-injected/model","INSTRUCTIONS":"injected instructions"
            }
            """)!;
        var definition = CreateAgent(configured, modelId: $"{provider}/fixture",
            providerHeaders: new() { ["X-Provider-Setting"] = "keep" });
        var before = JsonSerializer.SerializeToElement(definition, JsonSerializerOptions.Web);
        var captured = new List<JsonElement>();
        using var http = CreateHttpClient(request =>
        {
            Assert.Equal("/v1/responses", request.RequestUri!.AbsolutePath);
            Assert.Equal("keep", Assert.Single(request.Headers.GetValues("X-Provider-Setting")));
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            captured.Add(body.RootElement.Clone());
            var text = path == "tool-search" ? "{\"selectedToolNames\":[\"tool_0\"]}"
                : path == "resource-search" ? "{\"selectedResourceUris\":[\"mail://0\"],\"selectedResourceTemplateUriTemplates\":[]}"
                : "{}";
            return SearchModelResponse(path == "streaming", new
            {
                type = "message", id = "msg_options", role = "assistant",
                content = new[] { new { type = "output_text", text } }
            });
        });
        using var client = CreateClient(http, definition);
        var options = new ChatOptions
        {
            Instructions = "Runtime instructions",
            Tools = [AIFunctionFactory.Create(() => "ok", "runtime_tool")]
        };
        switch (path)
        {
            case "tool-search":
                var search = await SeedSearchToolsAsync(client, 1);
                var result = ReadSearchToolResult(await search.InvokeAsync(new AIFunctionArguments(
                    new Dictionary<string, object?> { ["goal"] = "Find tools" })));
                Assert.False(result.IsError);
                break;
            case "resource-search":
                SeedSearchResources(client, 1);
                Assert.False((await InvokeResourceSearchAsync(client)).IsError);
                break;
            case "decision":
                configured["max_output_tokens"] = 50000;
                configured["MAX_OUTPUT_TOKENS"] = 99999;
                configured["text"] = new { format = new { type = "text" } };
                configured["response_format"] = new { type = "text" };
                configured["safety_identifier"] = "provider-default";
                configured["SAFETY_IDENTIFIER"] = "case-injected-safety";
                before = JsonSerializer.SerializeToElement(definition, JsonSerializerOptions.Web);
                client.BeginDecisionExecution(JsonSerializer.SerializeToElement(new { type = "object" }), 321, "decision-safety");
                await client.GetResponseAsync(CreateUserMessages("hello"), options);
                break;
            case "streaming":
                await CollectAsync(client.GetStreamingResponseAsync(CreateUserMessages("hello"), options));
                break;
            default:
                await client.GetResponseAsync(CreateUserMessages("hello"), options);
                break;
        }

        var root = Assert.Single(captured);
        Assert.Equal($"{provider}/fixture", root.GetProperty("model").GetString());
        Assert.False(root.TryGetProperty("metadata", out _));
        Assert.False(root.TryGetProperty(provider, out _));
        Assert.False(root.TryGetProperty("providerMetadata", out _));
        Assert.False(root.TryGetProperty("providerOptions", out _));
        // Agents forward names verbatim. Unified mapping, not agents, consumes aliases.
        Assert.Equal(128000, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal(64000, root.GetProperty("max_completion_tokens").GetInt32());
        Assert.Equal(path == "streaming", root.GetProperty("stream").GetBoolean());
        Assert.NotEqual("injected instructions", root.GetProperty("instructions").GetString());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("input").ValueKind);
        Assert.Equal("adaptive", root.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("high", root.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Equal("pptx", root.GetProperty("container").GetProperty("skills")[0].GetProperty("skill_id").GetString());
        var source = JsonSerializer.SerializeToElement(configured, JsonSerializerOptions.Web);
        foreach (var property in source.EnumerateObject().Where(property => property.Name.StartsWith("future_")))
            Assert.True(JsonElement.DeepEquals(property.Value, root.GetProperty(property.Name)), property.Name);
        Assert.All(root.EnumerateObject().GroupBy(property => property.Name), group => Assert.Single(group));
        Assert.False(root.GetProperty("parallel_tool_calls").GetBoolean());
        if (sideInference)
        {
            Assert.Empty(root.GetProperty("tools").EnumerateArray());
            Assert.Equal("none", root.GetProperty("tool_choice").GetString());
            Assert.False(root.TryGetProperty("previous_response_id", out _));
            Assert.False(root.TryGetProperty("background", out _));
        }
        else
        {
            Assert.Equal("required", root.GetProperty("tool_choice").GetString());
            Assert.Equal(2, root.GetProperty("tools").GetArrayLength());
            Assert.Contains(root.GetProperty("tools").EnumerateArray(), tool => tool.GetProperty("type").GetString() == "web_search");
            Assert.Contains(root.GetProperty("tools").EnumerateArray(), tool => tool.TryGetProperty("name", out var name) && name.GetString() == "runtime_tool");
        }
        if (path == "decision")
        {
            Assert.Equal(321, root.GetProperty("max_output_tokens").GetInt32());
            Assert.Equal("decision-safety", root.GetProperty("safety_identifier").GetString());
            Assert.Equal("json_schema", root.GetProperty("text").GetProperty("format").GetProperty("type").GetString());
            Assert.False(root.TryGetProperty("response_format", out _));
        }
        else
            Assert.False(root.TryGetProperty("max_output_tokens", out _));
        Assert.True(JsonElement.DeepEquals(before, JsonSerializer.SerializeToElement(definition, JsonSerializerOptions.Web)));
    }

    [Fact]
    public async Task Canonical_provider_fields_are_materialized_without_duplicate_extension_properties()
    {
        var configured = new Dictionary<string, object>
        {
            ["max_output_tokens"] = 777, ["safety_identifier"] = "caller",
            ["service_tier"] = "priority", ["parallel_tool_calls"] = false,
            ["metadata"] = new { openai = "application value", tag = "keep" }
        };
        using var http = CreateHttpClient(request =>
        {
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var root = body.RootElement;
            Assert.Equal(777, root.GetProperty("max_output_tokens").GetInt32());
            Assert.Equal("application value", root.GetProperty("metadata").GetProperty("openai").GetString());
            var typed = root.Deserialize<ResponseRequest>(ResponseJson.Default)!;
            Assert.Equal("caller", typed.SafetyIdentifier);
            Assert.Equal("priority", typed.ServiceTier);
            Assert.False(typed.ParallelToolCalls);
            Assert.Empty(typed.AdditionalProperties ?? []);
            Assert.All(root.EnumerateObject().GroupBy(property => property.Name), group => Assert.Single(group));
            return CreateJsonResponse(LoadFixture(StructuredFixturePath));
        });
        using var client = CreateClient(http, CreateAgent(configured));
        await client.GetResponseAsync(CreateUserMessages("hello"));
    }
}
