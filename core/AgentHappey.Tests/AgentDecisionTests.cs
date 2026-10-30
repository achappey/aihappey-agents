using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgentHappey.Common.Models;
using AgentHappey.Core;
using AgentHappey.Core.ChatClient;
using AgentHappey.Core.ChatRuntime;
using AgentHappey.Core.DecisionRuntime;
using AIHappey.Core.Models;
using AIHappey.Responses;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;

namespace AgentHappey.Tests;

public sealed partial class AgentDecisionTests
{
    [Fact]
    public async Task OpenAI_uses_schema_from_first_request_through_normal_skill_tool_loop()
    {
        var f = new Fixture { UseSkill = true, FinalPayload = """
            {"answers":{"q0":{"type":"predicate","probability":0.9},
              "q1":{"type":"choice","probabilities":{"0":0.2,"1":0.8}},
              "q2":{"type":"score","probabilities":{"0":0.1,"1":0.2,"2":0.7}}}}
            """ };
        await f.ResolveAgentAsync("AuditAgent");
        var before = JsonSerializer.Serialize(f.Agent);
        var request = NativeRequest();
        request.SafetyIdentifier = "opaque-caller";
        request.Questions = [request.Questions[0],
            new OpenAIDecisionChoiceQuestion { Name = "same", Instructions = "Choose.", Choices = [new() { Value = "true" }, new() { Value = true }] },
            new OpenAIDecisionScoreQuestion { Name = "same", Instructions = "Rate.", Levels = [new() { Label = "low" }, new() { Label = "medium" }, new() { Label = "high" }] }];
        var result = await f.Execute(request);
        Assert.Equal("AuditAgent", result.Model);
        Assert.Equal(0.9, Assert.IsType<OpenAIDecisionPredicateAnswer>(result.Answers[0]).Probability);
        var choice = Assert.IsType<OpenAIDecisionChoiceAnswer>(result.Answers[1]);
        Assert.Equal(JsonValueKind.True, choice.Choice.Value.ValueKind);
        Assert.Equal(0.8, choice.Confidence);
        Assert.Equal(JsonValueKind.String, choice.Probabilities[0].Value.Value.ValueKind);
        var score = Assert.IsType<OpenAIDecisionScoreAnswer>(result.Answers[2]);
        Assert.Equal(0.8, score.Score, 6);
        Assert.Equal(["low", "medium", "high"], score.Probabilities.Select(p => p.Label));
        Assert.Null(result.Answers[0].Name);
        Assert.Equal("same", choice.Name);
        Assert.Equal(20, result.Usage.InputTokens);
        Assert.Equal(10, result.Usage.OutputTokens);
        Assert.Equal(30, result.Usage.TotalTokens);
        Assert.Equal(4, result.Usage.InputTokensDetails.CachedTokens);
        Assert.Equal(2, result.Usage.OutputTokensDetails.ReasoningTokens);
        var calls = f.Sent.ToArray();
        Assert.Equal(2, calls.Length); // One execution: a tool call followed by the structured answer.
        Assert.All(calls, call =>
        {
            Assert.Equal("openai/answer-model", call.Body.GetProperty("model").GetString());
            Assert.Equal("json_schema", call.Body.GetProperty("text").GetProperty("format").GetProperty("type").GetString());
            Assert.Equal("opaque-caller", call.Body.GetProperty("safety_identifier").GetString());
            Assert.Equal("auto", call.Body.GetProperty("tool_choice").GetString());
            Assert.Contains("activate_skill", call.Body.GetProperty("tools").GetRawText());
            Assert.Contains("Always inspect the evidence", call.Body.GetProperty("instructions").GetString());
        });
        Assert.Contains("activate_skill", calls[0].Body.GetProperty("tools").GetRawText());
        Assert.Contains("SKILL_EVIDENCE_42", calls[1].Body.GetProperty("input").GetRawText());
        Assert.Contains("function_call_output", calls[1].Body.GetProperty("input").GetRawText());
        Assert.Equal(UserTexts(calls[0].Body), UserTexts(calls[1].Body));
        Assert.All(calls, call => Assert.Equal("configured-secret", call.Headers["X-Provider-Key"]));
        Assert.All(calls, call => Assert.Equal("AuditAgent", call.Headers["X-Agent-Name"]));
        Assert.DoesNotContain("configured-secret", JsonSerializer.Serialize(result));
        Assert.Equal(before, JsonSerializer.Serialize(f.Agent));
    }

    [Fact]
    public async Task Vercel_preserves_question_ids_criteria_and_score_scale_with_box_and_refusal()
    {
        var f = new Fixture { FinalPayload = """
            {"answers":{"q0":{"type":"predicate","probability":0.25},
              "q1":{"type":"choice","probabilities":{"0":0.1,"1":0.9}},
              "q2":{"type":"score","probabilities":{"0":0.1,"1":0.2,"2":0.7}},
              "q3":{"type":"box","box":{"top":0.1,"bottom":0.8,"left":0.2,"right":0.7}},
              "q4":{"type":"refusal"}}}
            """ };
        var request = JsonSerializer.Deserialize<DecisionRequest>("""
            {"model":"AuditAgent","state":{"record":["evidence"]},"maxOutputTokens":400,
             "questions":{"__proto__":{"type":"boolean","criteria":{"true":{"rule":"qualifies"},"false":null}},
              "route":{"type":"choice","criteria":{"normal":null,"priority":{"rule":"urgent"}}},
              "severity":{"type":"score","criteria":["low",null,"high"]},
              "location":{"type":"box","instructions":"Locate the object."},
              "unsafe":{"type":"boolean","instructions":"Unsafe question."}}}
            """, JsonSerializerOptions.Web)!;
        var result = await f.Execute(request);
        Assert.Equal(request.Questions.Keys, result.Answers.Keys);
        Assert.Equal(0.25, Assert.IsType<DecisionBooleanAnswer>(result.Answers["__proto__"]).Probability);
        Assert.Equal("priority", Assert.IsType<DecisionChoiceAnswer>(result.Answers["route"]).Choice);
        Assert.Equal(1.6, Assert.IsType<DecisionScoreAnswer>(result.Answers["severity"]).Score, 6);
        Assert.Equal(0.8, Assert.IsType<DecisionBoxAnswer>(result.Answers["location"]).Box.Bottom);
        Assert.IsType<DecisionRefusalAnswer>(result.Answers["unsafe"]);
        Assert.Equal("AuditAgent", result.Response!.ModelId);
        Assert.Equal(10, result.Usage!.InputTokens);
        var calls = f.Sent.ToArray();
        Assert.Single(calls);
        Assert.Contains("qualifies", calls[0].Body.GetProperty("input").GetRawText());
        Assert.Equal(400, calls[0].Body.GetProperty("max_output_tokens").GetInt32());
    }

    [Theory]
    [InlineData("{\"answers\":{}}")]
    [InlineData("{\"answers\":{\"q0\":{\"type\":\"predicate\",\"probability\":1.1}}}")]
    [InlineData("{\"answers\":{\"q0\":{\"type\":\"predicate\",\"probability\":0.5,\"probability\":0.6}}}")]
    [InlineData("{\"answers\":{\"q0\":{\"type\":\"choice\",\"probabilities\":{\"0\":1}}}}")]
    [InlineData("{\"answers\":{\"q0\":{\"type\":\"predicate\",\"probability\":0.5},\"q1\":{\"type\":\"refusal\"}}}")]
    [InlineData("{\"answers\":")]
    public async Task Malformed_incomplete_or_mismatched_answers_fail_closed(string payload)
    {
        var f = new Fixture { FinalPayload = payload };
        await Assert.ThrowsAnyAsync<Exception>(() => f.Execute(NativeRequest()));
        Assert.Single(f.Sent);
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public async Task Valid_json_from_noncompleted_inference_is_not_a_success(string status)
    {
        var f = new Fixture { FinalStatus = status };
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Execute(NativeRequest()));
        Assert.Single(f.Sent);
    }

    [Fact]
    public async Task Provider_refusal_maps_to_typed_refusals_without_fabricating_probabilities()
    {
        var f = new Fixture { Refuse = true };
        var result = await f.Execute(NativeRequest());
        Assert.IsType<OpenAIDecisionRefusalAnswer>(Assert.Single(result.Answers));
        Assert.Single(f.Sent);
    }

    [Theory]
    [InlineData("{\"0\":0.2,\"1\":0.2}")]
    [InlineData("{\"0\":1}")]
    [InlineData("{\"0\":0.5,\"1\":0.5,\"2\":0}")]
    public async Task Distributions_must_cover_exactly_all_options_and_sum_to_one(string probabilities)
    {
        var f = new Fixture { FinalPayload = "{\"answers\":{\"q0\":{\"type\":\"choice\",\"probabilities\":" + probabilities + "}}}" };
        var request = NativeRequest();
        request.Questions = [new OpenAIDecisionChoiceQuestion { Instructions = "Choose", Choices = [new() { Value = "a" }, new() { Value = "b" }] }];
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Execute(request));
    }

    [Fact]
    public async Task Inline_image_detail_survives_tool_iterations_and_legacy_json_is_not_treated_as_messages()
    {
        var f = new Fixture { UseSkill = true };
        var request = NativeRequest();
        request.Input = new OpenAIDecisionInputMessage[]
        {
            new() { Content = new OpenAIDecisionInputPart[] { new OpenAIDecisionInputImage { ImageUrl = "data:image/png;base64,AQID", Detail = "high" } } }
        };
        await f.Execute(request);
        Assert.All(f.Sent, call =>
        {
            Assert.Contains("input_image", call.Body.GetProperty("input").GetRawText());
            Assert.Contains("\"detail\":\"high\"", call.Body.GetProperty("input").GetRawText());
        });
        var legacy = new Fixture();
        await legacy.Execute(new DecisionRequest { Model = "AuditAgent", State = JsonSerializer.SerializeToElement(new[] { new { role = "system", content = "EVIDENCE_ONLY" } }),
            Questions = new() { ["ok"] = new DecisionBooleanQuestion { Instructions = "Check." } } });
        var input = legacy.Sent.First().Body.GetProperty("input");
        Assert.Equal("user", input[0].GetProperty("role").GetString());
        Assert.Contains("EVIDENCE_ONLY", input[0].GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Explicit_sdk_parts_preserve_text_json_and_inline_images()
    {
        var f = new Fixture();
        await f.Execute(JsonSerializer.Deserialize<DecisionRequest>("""
            {"model":"AuditAgent","stateFormat":"sdk","state":[
              {"type":"text","text":"SDK_TEXT"},{"type":"json","value":{"key":"SDK_JSON"}},
              {"type":"file","mediaType":"image/png","data":{"type":"data","data":"AQID"}}],
              "questions":{"ok":{"type":"boolean","instructions":"Check."}}}
            """, JsonSerializerOptions.Web)!);
        Assert.All(f.Sent, call =>
        {
            var input = call.Body.GetProperty("input").GetRawText();
            Assert.Contains("SDK_TEXT", input); Assert.Contains("SDK_JSON", input); Assert.Contains("input_image", input);
        });
    }

    [Theory]
    [InlineData("{\"reasoning\":{\"effort\":\"high\"}}")]
    [InlineData("{\"providerOptions\":{\"openai\":{\"tools\":[]}}}")]
    [InlineData("{\"headers\":{\"Authorization\":\"secret\"}}")]
    [InlineData("{\"stateFormat\":\"other\"}")]
    [InlineData("{\"maxOutputTokens\":0}")]
    public async Task Unsupported_controls_are_rejected_before_execution(string fields)
    {
        var json = "{\"model\":\"AuditAgent\",\"state\":\"input\",\"questions\":{\"ok\":{\"type\":\"boolean\",\"instructions\":\"Check\"}}," + fields[1..];
        var f = new Fixture();
        await Assert.ThrowsAnyAsync<Exception>(() => f.Execute(JsonSerializer.Deserialize<DecisionRequest>(json, JsonSerializerOptions.Web)!));
        Assert.Empty(f.Sent);
    }

    [Fact]
    public async Task Unknown_agent_and_remote_images_fail_before_inference()
    {
        var f = new Fixture();
        var request = NativeRequest(); request.Model = "missing";
        await Assert.ThrowsAsync<ArgumentException>(() => f.Execute(request));
        request = NativeRequest();
        request.Input = new OpenAIDecisionInputMessage[] { new() { Content = new OpenAIDecisionInputPart[] { new OpenAIDecisionInputImage { ImageUrl = "https://remote.test/image.png" } } } };
        await Assert.ThrowsAsync<NotSupportedException>(() => f.Execute(request));
        Assert.Empty(f.Sent);
    }

    [Fact]
    public async Task Cancellation_stops_the_existing_tool_loop_without_another_inference()
    {
        using var cancellation = new CancellationTokenSource();
        var f = new Fixture { UseSkill = true, AfterRequest = cancellation.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Execute(NativeRequest(), cancellation.Token));
        Assert.Single(f.Sent);
    }

    [Fact]
    public async Task Model_routing_runs_once_and_structured_execution_uses_selected_model_configuration()
    {
        var f = new Fixture { RouteModel = true };
        f.Agent.Models =
        [
            new() { Id = "router/decision", Type = "decision" },
            new() { Id = "openai/first", Type = "language" },
            new() { Id = "anthropic/selected", Type = "language", ProviderHeaders = new() { ["X-Provider-Key"] = "selected-model-key" },
                ProviderMetadata = new() { ["reasoning"] = new { effort = "low" } } }
        ];
        await f.Execute(NativeRequest());
        Assert.Equal(1, f.DiscoveryCalls);
        Assert.Equal(1, f.RoutingCalls);
        Assert.Single(f.Sent);
        Assert.All(f.Sent, call =>
        {
            Assert.Equal("anthropic/selected", call.Body.GetProperty("model").GetString());
            Assert.Equal("selected-model-key", call.Headers["X-Provider-Key"]);
        });
        Assert.Contains("reasoning", f.Sent.Last().Body.GetProperty("metadata").GetProperty("anthropic").GetRawText());
    }

    [Fact]
    public async Task Structured_execution_preserves_configured_provider_tools_but_not_catalog_output_schema()
    {
        var f = new Fixture();
        f.Agent.Model!.ProviderMetadata!["tools"] = new[] { new { type = "web_search" } };
        f.Agent.Model.ProviderMetadata["text"] = new { format = new { type = "text" } };
        f.Agent.Model.ProviderMetadata["tool_choice"] = "required";
        await f.Execute(NativeRequest());
        var final = f.Sent.Last().Body;
        var metadata = final.GetProperty("metadata").GetProperty("openai");
        Assert.Equal("web_search", metadata.GetProperty("tools")[0].GetProperty("type").GetString());
        Assert.Equal("required", metadata.GetProperty("tool_choice").GetString());
        Assert.False(metadata.TryGetProperty("text", out _));
        Assert.False(metadata.TryGetProperty("response_format", out _));
        Assert.Equal("json_schema", final.GetProperty("text").GetProperty("format").GetProperty("type").GetString());
        Assert.Equal("low", metadata.GetProperty("reasoning").GetProperty("effort").GetString());
    }

    [Fact]
    public async Task Inverted_boxes_are_not_successful_decisions()
    {
        var f = new Fixture { FinalPayload = """{"answers":{"q0":{"type":"box","box":{"top":0.8,"bottom":0.1,"left":0,"right":1}}}}""" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Execute(new DecisionRequest
        {
            Model = "AuditAgent", State = "image evidence", Questions = new() { ["location"] = new DecisionBoxQuestion { Instructions = "Locate" } }
        }));
    }

    [Fact]
    public async Task Empty_names_and_boolean_false_values_remain_distinct_from_null_names_and_strings()
    {
        var f = new Fixture { FinalPayload = """{"answers":{"q0":{"type":"choice","probabilities":{"0":0.75,"1":0.25}}}}""" };
        var request = NativeRequest();
        request.Questions = [new OpenAIDecisionChoiceQuestion { Name = "", Instructions = "Choose", Choices = [new() { Value = false }, new() { Value = "false" }] }];
        var result = await f.Execute(request);
        var answer = Assert.IsType<OpenAIDecisionChoiceAnswer>(Assert.Single(result.Answers));
        Assert.Equal("", answer.Name);
        Assert.Equal(JsonValueKind.False, answer.Choice.Value.ValueKind);
        Assert.Equal(JsonValueKind.String, answer.Probabilities[1].Value.Value.ValueKind);
    }

    [Fact]
    public async Task Ordinary_chat_retains_catalog_output_schema_after_concurrent_decision_runs()
    {
        var f = new Fixture();
        var before = JsonSerializer.Serialize(f.Agent);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => f.Execute(NativeRequest())));
        using var ordinary = f.CreateClient(f.Agent);
        await ordinary.GetResponseAsync([new ChatMessage(ChatRole.User, "ordinary chat")]);
        var call = f.Sent.Last();
        Assert.Equal("catalog_format", call.Body.GetProperty("text").GetProperty("format").GetProperty("name").GetString());
        Assert.Equal(before, JsonSerializer.Serialize(f.Agent));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Single_structured_execution_retains_schema_and_history_through_multiple_tool_iterations(bool native)
    {
        var f = new Fixture { ToolIterations = 2 };
        var result = native ? (object)await f.Execute(NativeRequest()) : await f.Execute(new DecisionRequest
        {
            Model = "AuditAgent", State = "request-evidence",
            Questions = new() { ["eligible"] = new DecisionBooleanQuestion { Instructions = "Does this qualify?" } }
        });
        var calls = f.Sent.ToArray();
        Assert.Equal(3, calls.Length);
        Assert.All(calls, call => Assert.Equal("openai/answer-model", call.Body.GetProperty("model").GetString()));
        var firstUserMessages = UserTexts(calls[0].Body);
        Assert.Equal(2, firstUserMessages.Length);
        Assert.All(calls, call =>
        {
            Assert.Equal(firstUserMessages, UserTexts(call.Body));
            Assert.Equal("auto", call.Body.GetProperty("tool_choice").GetString());
            Assert.Contains("activate_skill", call.Body.GetProperty("tools").GetRawText());
            Assert.Equal("agent_decision", call.Body.GetProperty("text").GetProperty("format").GetProperty("name").GetString());
            Assert.Equal(calls[0].Body.GetProperty("text").GetRawText(), call.Body.GetProperty("text").GetRawText());
            Assert.Equal(calls[0].Body.GetProperty("instructions").GetString(), call.Body.GetProperty("instructions").GetString());
        });
        Assert.All(calls.Skip(1), call => Assert.Contains("SKILL_EVIDENCE_42", call.Body.GetProperty("input").GetRawText()));
        Assert.Contains("call_skill_0", calls[2].Body.GetProperty("input").GetRawText());
        Assert.Contains("call_skill_1", calls[2].Body.GetProperty("input").GetRawText());
        Assert.Equal(2, calls[2].Body.GetProperty("input").EnumerateArray()
            .Count(item => item.GetProperty("type").GetString() == "function_call_output"));
        if (result is OpenAIDecisionResponse openAI)
        {
            Assert.Equal(30, openAI.Usage.InputTokens);
            Assert.Equal(0.8, Assert.IsType<OpenAIDecisionPredicateAnswer>(Assert.Single(openAI.Answers)).Probability);
        }
        else
        {
            var vercel = Assert.IsType<DecisionResponse>(result);
            Assert.Equal(30, vercel.Usage!.InputTokens);
            Assert.Equal(0.8, Assert.IsType<DecisionBooleanAnswer>(vercel.Answers["eligible"]).Probability);
        }
    }

    [Fact]
    public async Task Question_messages_are_plain_text_and_no_behavioral_prompts_are_added_to_agent_instructions()
    {
        var f = new Fixture();
        await f.Execute(NativeRequest());
        var calls = f.Sent.ToArray();
        Assert.Single(calls);
        using var ordinary = f.CreateClient(f.Agent);
        var expectedInstructions = ordinary.GetComposedInstructions().Split("\n\nCurrent date/time:")[0];
        Assert.All(calls, call => Assert.Equal(expectedInstructions,
            call.Body.GetProperty("instructions").GetString()!.Split("\n\nCurrent date/time:")[0]));
        var questionText = UserTexts(calls[0].Body)[1].Replace("\r\n", "\n");
        Assert.Equal("Question q0 (predicate)\nInstructions: Does this qualify?", questionText);
        Assert.Equal(["request-evidence", UserTexts(calls[0].Body)[1]], UserTexts(calls[0].Body));
        Assert.True(IsDecisionRequest(calls[0].Body));
    }

    [Fact]
    public async Task Plain_text_questions_preserve_typed_choices_labels_names_and_descriptions()
    {
        var f = new Fixture { FinalPayload = """
            {"answers":{"q0":{"type":"choice","probabilities":{"0":0.5,"1":0.5}},
             "q1":{"type":"score","probabilities":{"0":0.4,"1":0.6}}}}
            """ };
        var request = NativeRequest();
        request.Questions =
        [
            new OpenAIDecisionChoiceQuestion { Name = "typed", Instructions = "Choose", Choices =
                [new() { Value = "true", Description = "text option" }, new() { Value = true, Description = "boolean option" }] },
            new OpenAIDecisionScoreQuestion { Name = "severity", Instructions = "Rate", Levels =
                [new() { Label = "low", Description = "minor issue" }, new() { Label = "high", Description = "severe issue" }] }
        ];
        await f.Execute(request);
        var text = UserTexts(f.Sent.First().Body)[1];
        Assert.StartsWith("Question q0 (choice)", text);
        Assert.Contains("Name: typed", text);
        Assert.Contains("value: \"true\" (string)", text);
        Assert.Contains("value: true (boolean)", text);
        Assert.Contains("description: text option", text);
        Assert.Contains("description: boolean option", text);
        Assert.Contains("Question q1 (score)", text);
        Assert.Contains("label: high", text);
        Assert.Contains("description: severe issue", text);
        Assert.DoesNotContain("\"choices\":", text);
        Assert.DoesNotContain("\"levels\":", text);
    }

    [Fact]
    public async Task Plain_text_sdk_criteria_preserve_order_nested_values_and_explicit_nulls()
    {
        var f = new Fixture { FinalPayload = """
            {"answers":{"q0":{"type":"choice","probabilities":{"0":0.25,"1":0.75}},
             "q1":{"type":"predicate","probability":0.8},
             "q2":{"type":"score","probabilities":{"0":0.2,"1":0.3,"2":0.5}}}}
            """ };
        await f.Execute(JsonSerializer.Deserialize<DecisionRequest>("""
            {"model":"AuditAgent","state":"input","questions":{
             "route":{"type":"choice","criteria":{"standard":null,"priority":{"rule":"urgent","checks":["shipment","policy"]}}},
             "eligible":{"type":"boolean","criteria":{"true":{"policy":"refund"},"false":null}},
             "rating":{"type":"score","criteria":["low",null,"high"]}}}
            """, JsonSerializerOptions.Web)!);
        var text = UserTexts(f.Sent.First().Body)[1];
        Assert.Contains("Name: route", text);
        Assert.Contains("0 (standard): (null)", text);
        Assert.Contains("1 (priority):", text);
        Assert.Contains("rule: urgent", text);
        Assert.Contains("0: shipment", text);
        Assert.Contains("1: policy", text);
        Assert.Contains("true:", text);
        Assert.Contains("policy: refund", text);
        Assert.Contains("false: (null)", text);
        Assert.Contains("1: (null)", text);
        Assert.DoesNotContain("\"criteria\":", text);
    }

    [Theory]
    [InlineData("web_search_call")]
    [InlineData("file_search_call")]
    [InlineData("code_interpreter_call")]
    public async Task Completed_hosted_tool_items_can_accompany_the_single_structured_answer(string toolType)
    {
        var f = new Fixture { HostedToolType = toolType };
        var result = await f.Execute(NativeRequest());
        Assert.Equal(0.8, Assert.IsType<OpenAIDecisionPredicateAnswer>(Assert.Single(result.Answers)).Probability);
        Assert.True(IsDecisionRequest(Assert.Single(f.Sent).Body));
    }

    [Fact]
    public async Task Incomplete_hosted_tool_output_is_not_accepted_or_retried()
    {
        var f = new Fixture { HostedToolType = "web_search_call", HostedToolStatus = "in_progress" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Execute(NativeRequest()));
        Assert.Single(f.Sent);
    }

    private static string[] UserTexts(JsonElement body) => body.GetProperty("input").EnumerateArray()
        .Where(item => item.TryGetProperty("role", out var role) && role.GetString() == "user")
        .SelectMany(item => item.GetProperty("content").EnumerateArray())
        .Where(part => part.GetProperty("type").GetString() == "input_text")
        .Select(part => part.GetProperty("text").GetString()!).ToArray();

    private static bool IsDecisionRequest(JsonElement body)
        => body.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.Object
            && text.TryGetProperty("format", out var format) && format.TryGetProperty("name", out var name)
            && name.GetString() == "agent_decision";

    private static OpenAIDecisionRequest NativeRequest() => new()
    {
        Model = "AuditAgent", Input = "request-evidence", Questions = [new OpenAIDecisionPredicateQuestion { Instructions = "Does this qualify?" }]
    };

    private sealed record SentRequest(JsonElement Body, Dictionary<string, string> Headers);

    private sealed class Fixture : IHttpClientFactory, IModelCatalog
    {
        public bool UseSkill { get; init; }
        public int ToolIterations { get; init; }
        public bool RouteModel { get; init; }
        public int DiscoveryCalls;
        public int RoutingCalls;
        public bool Refuse { get; init; }
        public string FinalPayload { get; init; } = """{"answers":{"q0":{"type":"predicate","probability":0.8}}}""";
        public string FinalStatus { get; init; } = "completed";
        public Action? AfterRequest { get; init; }
        public HttpStatusCode? ProviderErrorStatus { get; init; }
        public string? HostedToolType { get; init; }
        public string HostedToolStatus { get; init; } = "completed";
        public ConcurrentQueue<SentRequest> Sent { get; } = new();
        public Agent Agent { get; } = JsonSerializer.Deserialize<Agent>("""
            {"name":"AuditAgent","description":"Audit evidence.","instructions":"Always inspect the evidence.",
             "model":{"id":"openai/answer-model","providerHeaders":{"X-Provider-Key":"configured-secret"},
              "providerMetadata":{"response_format":{"type":"json_object"},"reasoning":{"effort":"low"}}},
             "responseFormat":{"json_schema":{"name":"catalog_format","strict":true,"schema":{"type":"object","properties":{},"additionalProperties":false}}}}
            """, JsonSerializerOptions.Web)!;

        public AgentDecisionService Service => new(new ChatRuntimeOrchestrator(new StreamingContentMapper(), this), this);
        public Task<OpenAIDecisionResponse> Execute(OpenAIDecisionRequest request, CancellationToken ct = default)
            => Service.ExecuteAsync(request, new DefaultHttpContext().Response, CreateClient, ct);
        public Task<DecisionResponse> Execute(DecisionRequest request, CancellationToken ct = default)
            => Service.ExecuteAsync(request, new DefaultHttpContext().Response, CreateClient, ct);
        public AgentChatClient CreateClient(Agent agent) => new(CreateClient(""), this, agent, new Dictionary<string, string?>());

        public HttpClient CreateClient(string name)
        {
            var inferenceCalls = 0;
            return new(new Handler(async (request, ct) =>
            {
                if (RouteModel && request.RequestUri!.AbsolutePath == "/v1/models")
                {
                    Interlocked.Increment(ref DiscoveryCalls);
                    return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { data = Agent.Models!.Select(model => new { id = model.Id, type = model.Type }) }) };
                }
                if (RouteModel && request.RequestUri!.AbsolutePath == "/api/decisions")
                {
                    Interlocked.Increment(ref RoutingCalls);
                    return new(HttpStatusCode.OK) { Content = new StringContent("""{"answers":{"model":{"type":"choice","choice":"anthropic/selected"}}}""", Encoding.UTF8, "application/json") };
                }
                Assert.Equal("/v1/responses", request.RequestUri!.AbsolutePath);
                var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(ct));
                Sent.Enqueue(new(body, request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase)));
                var structured = IsDecisionRequest(body);
                var iteration = Interlocked.Increment(ref inferenceCalls) - 1;
                AfterRequest?.Invoke();
                if (structured && ProviderErrorStatus is { } errorStatus)
                    return new(errorStatus) { Content = new StringContent("""{"error":{"code":"unsupported_response_format","message":"Structured output is not supported; provider-secret"}}""", Encoding.UTF8, "application/json") };
                object[] output;
                if (structured && iteration < Math.Max(UseSkill ? 1 : 0, ToolIterations))
                    output = [new { type = "function_call", id = "fc_skill_" + iteration, call_id = "call_skill_" + iteration, name = "activate_skill", arguments = "{\"skill_id\":\"audit-skill\"}", status = "completed" }];
                else
                {
                    object part = structured && Refuse ? new { type = "refusal", refusal = "Cannot answer safely." }
                        : new { type = "output_text", text = structured ? FinalPayload : "ordinary-chat-answer", annotations = Array.Empty<object>() };
                    output = [new { type = "message", id = "msg_" + Guid.NewGuid().ToString("N"), role = "assistant", status = "completed", content = new[] { part } }];
                    if (HostedToolType is not null)
                        output = [new { type = HostedToolType, id = "hosted_tool", status = HostedToolStatus }, .. output];
                }
                return new(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        id = "resp_test", @object = "response", created_at = 1, status = FinalStatus,
                        model = "openai/answer-model", output,
                        usage = new { input_tokens = 10, output_tokens = 5, total_tokens = 15,
                            input_tokens_details = new { cached_tokens = 2 }, output_tokens_details = new { reasoning_tokens = 1 } }
                    })
                };
            })) { BaseAddress = new("https://gateway.test/") };
        }

        public Task<Agent?> ResolveAgentAsync(string modelId, CancellationToken cancellationToken = default)
        {
            if ((UseSkill || ToolIterations > 0) && Agent.Skills is null) Agent.Skills = [Skill()];
            return Task.FromResult(modelId == Agent.Name ? Agent : null);
        }
        public Task<IReadOnlyList<Agent>> ResolveAgentsAsync(IEnumerable<string> modelIds, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Agent>>(modelIds.Contains(Agent.Name) ? [Agent] : []);
        public Task<ModelListResponse> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ModelListResponse { Data = [] });

        private static AISkill Skill()
        {
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            using (var writer = new StreamWriter(zip.CreateEntry("audit-skill/SKILL.md").Open()))
                writer.Write("---\nname: audit-skill\ndescription: Audit records\n---\nSKILL_EVIDENCE_42: qualifies under the policy.");
            return new() { Name = "Audit skill", Description = "Audit records", Source = new() { Data = Convert.ToBase64String(stream.ToArray()) } };
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
