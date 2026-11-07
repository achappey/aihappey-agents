using System.Reflection;
using System.Text.Json;
using AgentHappey.AsyncResponses;
using AgentHappey.Common.Models;
using AgentHappey.Core.Responses;
using AIHappey.Responses;

namespace AgentHappey.Tests;

public sealed class AgentResponseRequestTests
{
    private const string FullAgent = """
        {"name":"InlineAgent","description":"An inline agent","instructions":"Keep this configuration.",
         "tags":["inline"],"model":{"id":"test/model","options":{"temperature":0.2},
           "providerHeaders":{"X-Provider-Key":"private-key"}},
         "mcpServers":{"lookup":{"url":"https://example.test/mcp","headers":{"X-Api-Key":"mcp-key"}}},
         "skills":[{"type":"skill_reference","skill_id":"lookup-skill","version":"latest"}],
         "tools":[{"type":"web_search","search_context_size":"low"}]}
        """;

    [Theory]
    [InlineData("agent")]
    [InlineData("agents")]
    public void Inline_agents_are_typed_root_properties_and_keep_their_full_configuration(string selector)
    {
        var value = selector == "agent" ? FullAgent : $"[{FullAgent}]";
        var request = Read($$"""{"input":"Hello","{{selector}}":{{value}}} """);
        var agent = selector == "agent" ? request.Agent! : Assert.Single(request.Agents!);
        Assert.True(request.AdditionalProperties is null or { Count: 0 });
        Assert.Same(agent, Assert.Single(request.ToChatRuntimeRequest().Agents!));
        Assert.Equal("Keep this configuration.", agent.Instructions);
        Assert.Equal("test/model", agent.Model?.Id);
        Assert.Equal(0.2f, agent.Model?.Options?.Temperature);
        Assert.Equal("private-key", agent.Model?.ProviderHeaders?["X-Provider-Key"]);
        Assert.Equal("mcp-key", JsonSerializer.SerializeToElement(agent.McpServers?["lookup"].Headers)
            .GetProperty("X-Api-Key").GetString());
        Assert.Equal("lookup-skill", Assert.IsType<SkillReference>(Assert.Single(agent.Skills!)).SkillId);
        Assert.Equal("low", Assert.Single(agent.Tools!).AdditionalProperties?["search_context_size"].GetString());
        var json = JsonSerializer.SerializeToElement(request, ResponseJson.Default);
        Assert.Equal(selector == "agent" ? JsonValueKind.Object : JsonValueKind.Array,
            json.GetProperty(selector).ValueKind);
    }

    [Fact]
    public void Singular_agent_precedes_plural_agents_and_model_selectors_remain_slug_strings()
    {
        var request = Read("""
            {"input":"Hello","model":"catalog/router","models":["catalog/one","",null,"catalog/two"],
             "agent":{"name":"First","instructions":"First instructions"},
             "agents":[{"name":"Second"},{"name":"Third"}]}
            """);
        var mapped = request.ToChatRuntimeRequest();
        Assert.Equal("catalog/router", mapped.Model);
        Assert.Equal(["catalog/one", "catalog/two"], mapped.Models);
        Assert.Equal(["First", "Second", "Third"], mapped.Agents!.Select(agent => agent.Name));
        Assert.Equal(2, request.Agents!.Count);
        Assert.Equal(4, request.Models!.Count);
    }

    [Fact]
    public void Metadata_is_preserved_but_never_used_for_routing()
    {
        var request = Read("""
            {"input":"Hello","metadata":{"model":"old/model","models":["old/one"],
             "agent":{"name":"OldSingle"},"agents":[{"name":"OldPlural"}],"business":"preserved"}}
            """);
        var before = JsonSerializer.Serialize(request.Metadata, ResponseJson.Default);
        var mapped = request.ToChatRuntimeRequest();
        Assert.Null(mapped.Model);
        Assert.Null(mapped.Models);
        Assert.Null(mapped.Agents);
        Assert.Equal(before, JsonSerializer.Serialize(request.Metadata, ResponseJson.Default));
        Assert.Same(request.Metadata, request.ToResponseRequest().Metadata);
    }

    [Theory]
    [InlineData("\"agent\":\"catalog/agent\"")]
    [InlineData("\"agent\":[]")]
    [InlineData("\"agents\":{}")]
    [InlineData("\"agents\":[\"catalog/agent\"]")]
    [InlineData("\"agents\":[42]")]
    [InlineData("\"model\":{\"id\":\"catalog/model\"}")]
    [InlineData("\"models\":[{\"id\":\"catalog/model\"}]")]
    [InlineData("\"models\":\"catalog/model\"")]
    public void Incorrect_selector_shapes_fail_deserialization(string selector)
        => Assert.Throws<JsonException>(() => Read("{\"input\":\"Hello\"," + selector + "}"));

    [Fact]
    public void Null_agent_array_entries_are_not_executable_agents()
        => Assert.Throws<ArgumentException>(() => Read("""{"input":"Hello","agents":[null]}""")
            .ToChatRuntimeRequest());

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"agent\":null,\"agents\":[],\"models\":[]}")]
    [InlineData("{\"agents\":null,\"models\":[\"\",null,\" \"]}")]
    public void Absent_or_empty_selectors_keep_runtime_defaults(string json)
    {
        var mapped = Read(json).ToChatRuntimeRequest();
        Assert.Null(mapped.Agents);
        Assert.Null(mapped.Models);
        Assert.Equal("sequential", mapped.WorkflowType);
    }

    [Fact]
    public void Protocol_projection_preserves_every_standard_field_and_unrelated_extensions()
    {
        const string json = """
            {"model":"catalog/model","input":"Hello","instructions":"System instructions",
             "temperature":0.3,"top_p":0.9,"truncation":"auto","max_output_tokens":128,"top_logprobs":3,
             "parallel_tool_calls":true,"stream":true,"store":false,"background":true,
             "previous_response_id":"resp_previous","user":"user","safety_identifier":"safe-user",
             "prompt_cache_retention":"24h","max_tool_calls":5,"prompt":{"id":"prompt_test"},
             "service_tier":"auto","text":{"format":{"type":"text"}},"include":["reasoning.encrypted_content"],
             "metadata":{"business":"preserved"},"tools":[{"type":"function","name":"lookup",
               "parameters":{"type":"object","properties":{}}}],"tool_choice":"auto","reasoning":{"effort":"high"},
             "context_management":[{"type":"compaction","compact_threshold":1000}],"prompt_cache_key":"cache",
             "prompt_cache_options":{"key":"value"},"stream_options":{"include_usage":true},
             "moderation":{"enabled":true},"conversation":{"id":"conv_test"},"access_programs":["program"],
             "protocol_extension":{"keep":true}}
            """;
        var standard = JsonSerializer.Deserialize<ResponseRequest>(json, ResponseJson.Default)!;
        var request = Read(json);
        request.Headers = standard.Headers = new() { ["X-Test"] = "value" };
        request.ExecutionMetadata = standard.ExecutionMetadata = new() { ["trace"] = "trace_test" };
        var projection = request.ToResponseRequest();

        // Guard against shared-contract additions silently becoming untyped/dropped backend fields.
        foreach (var property in typeof(ResponseRequest).GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            var backendProperty = typeof(AgentResponseRequest).GetProperty(property.Name);
            Assert.NotNull(backendProperty);
            Assert.Equal(property.PropertyType, backendProperty.PropertyType);
            Assert.True(JsonElement.DeepEquals(
                JsonSerializer.SerializeToElement(property.GetValue(standard), ResponseJson.Default),
                JsonSerializer.SerializeToElement(property.GetValue(projection), ResponseJson.Default)), property.Name);
        }
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(standard, ResponseJson.Default),
            JsonSerializer.SerializeToElement(projection, ResponseJson.Default)));
    }

    [Fact]
    public void Protocol_projection_and_runtime_never_read_routing_from_extension_data()
    {
        var request = new AgentResponseRequest
        {
            AdditionalProperties = new()
            {
                ["agent"] = JsonSerializer.SerializeToElement(new { name = "HiddenSingle" }),
                ["AGENTS"] = JsonSerializer.SerializeToElement(new[] { new { name = "HiddenPlural" } }),
                ["models"] = JsonSerializer.SerializeToElement(new[] { "hidden/model" }),
                ["workflow_type"] = JsonSerializer.SerializeToElement("groupchat"),
                ["workflowMetadata"] = JsonSerializer.SerializeToElement(new { }),
                ["unrelated"] = JsonSerializer.SerializeToElement(true)
            }
        };
        var mapped = request.ToChatRuntimeRequest();
        Assert.Null(mapped.Agents);
        Assert.Null(mapped.Models);
        Assert.Equal("sequential", mapped.WorkflowType);
        Assert.Equal("unrelated", Assert.Single(request.ToResponseRequest().AdditionalProperties!).Key);

        var typed = Read("""
            {"input":"Hello","agent":$AGENT,"agents":[$AGENT],"models":["catalog/model"],
             "workflow_type":"groupchat","workflow_metadata":{"groupchat":{"maximumIterationCount":3}}}
            """.Replace("$AGENT", FullAgent));
        var protocol = JsonSerializer.SerializeToElement(typed.ToResponseRequest(), ResponseJson.Default);
        foreach (var key in new[] { "agent", "agents", "models", "workflow_type", "workflow_metadata" })
            Assert.False(protocol.TryGetProperty(key, out _), key);
    }

    [Fact]
    public void Queued_request_round_trip_retains_full_agents_slugs_workflow_and_metadata_independently()
    {
        var request = Read("""
            {"input":"Hello","model":"catalog/router","models":["catalog/one","catalog/two"],
             "agent":$AGENT,"agents":[{"name":"Second","instructions":"Second instructions"}],
             "workflowType":"groupchat","workflowMetadata":{"groupchat":{"maximumIterationCount":3}},
             "background":true,"stream":true,"metadata":{"business":"preserved"}}
            """.Replace("$AGENT", FullAgent));
        var before = JsonSerializer.SerializeToElement(request.Agent, ResponseJson.Default);
        var clone = AsyncResponseLifecycle.CloneForQueue(request);
        request.Agent!.Instructions = "Changed after enqueue";
        request.Agent.Model!.ProviderHeaders!.Clear();
        var message = new AsyncResponsesQueueMessage { Request = clone };
        var restored = JsonSerializer.Deserialize<AsyncResponsesQueueMessage>(
            JsonSerializer.Serialize(message, ResponseJson.Default), ResponseJson.Default)!.Request;

        Assert.False(restored.Stream);
        Assert.False(restored.Background);
        Assert.True(request.Stream);
        Assert.True(request.Background);
        Assert.True(JsonElement.DeepEquals(before, JsonSerializer.SerializeToElement(restored.Agent, ResponseJson.Default)));
        var mapped = restored.ToChatRuntimeRequest();
        Assert.Equal("catalog/router", mapped.Model);
        Assert.Equal(["catalog/one", "catalog/two"], mapped.Models);
        Assert.Equal(["InlineAgent", "Second"], mapped.Agents!.Select(agent => agent.Name));
        Assert.Equal("groupchat", mapped.WorkflowType);
        Assert.Equal(3, mapped.WorkflowMetadata?.Groupchat?.MaximumIterationCount);
        Assert.Equal("preserved", JsonSerializer.SerializeToElement(restored.Metadata).GetProperty("business").GetString());
        Assert.True(restored.AdditionalProperties is null or { Count: 0 });
    }

    private static AgentResponseRequest Read(string json)
        => JsonSerializer.Deserialize<AgentResponseRequest>(json, ResponseJson.Default)!;
}
