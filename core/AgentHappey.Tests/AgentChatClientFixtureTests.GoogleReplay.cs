using System.Text.Json;
using AgentHappey.Common.Models;
using AgentHappey.Core;
using AgentHappey.Core.ChatRuntime;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;

namespace AgentHappey.Tests;

public sealed partial class AgentChatClientFixtureTests
{
    [Theory]
    [InlineData("models")]
    [InlineData("model")]
    [InlineData("alias")]
    [InlineData("inline")]
    public async Task Google_catalog_continuation_preserves_signature_before_client_call_and_result(string selection)
    {
        const string signature = "opaque+google/signature==";
        const string toolName = "mcp_github_rest_countries_search_codes_223eb8ccbbcfd74a";
        var agent = CreateAgent(modelId: "google/gemini-flash-latest");
        agent.Name = "GoogleMapsAgent";
        var catalog = new GoogleReplayModelCatalog(agent);
        // JSON round-trip reproduces the UI contract: empty reasoning text and
        // agent-scoped native reasoning metadata, not a direct google scope.
        var request = JsonSerializer.Deserialize<AgentRequest>("""
            {"models":["GoogleMapsAgent"],"messages":[
              {"role":"user","parts":[{"type":"text","text":"Show Poland"}]},
              {"role":"assistant","metadata":{"model":"GoogleMapsAgent"},"parts":[
                {"type":"reasoning","text":"","providerMetadata":{
                  "GoogleMapsAgent":{"id":"interactions-content-0","type":"reasoning","status":"completed",
                    "content":[{"type":"reasoning_text","text":""}],
                    "provider_metadata":{"google":{"type":"thought_signature","signature":"opaque+google/signature=="}},
                    "encrypted_content":"opaque+google/signature=="},
                  "UnselectedAgent":{"id":"foreign-reasoning","type":"reasoning","encrypted_content":"must-not-leak"}}},
                {"type":"tool-mcp_github_rest_countries_search_codes_223eb8ccbbcfd74a","toolCallId":"call_652898",
                  "state":"output-available","providerExecuted":false,"input":{"name":"Poland"},
                  "output":{"content":[],"structuredContent":{"countries":[{"cca2":"PL"}]}}}]}]}
            """, JsonSerializerOptions.Web)!;
        if (selection == "model")
        {
            request.Models = null;
            request.Model = "GoogleMapsAgent";
        }
        else if (selection == "alias")
            request.Models = ["catalog/agent-id-123"];
        else if (selection == "inline")
        {
            request.Models = null;
            request.Agents = [agent];
        }

        string? captured = null;
        using var http = CreateHttpClient(message =>
        {
            captured = message.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return CreateJsonResponse(LoadFixture(StructuredFixturePath));
        });
        using var client = CreateClient(http, agent);
        var response = new DefaultHttpContext().Response;
        response.Body = new MemoryStream();
        IReadOnlyList<ChatMessage>? configuredMessages = null;
        var context = await new ChatRuntimeOrchestrator(new StreamingContentMapper(), catalog).PrepareAsync(
            response, request, _ => client, (_, messages) => configuredMessages = messages);

        var reasoning = Assert.Single(context.Messages.SelectMany(message => message.Contents).OfType<TextReasoningContent>());
        Assert.Equal(signature, reasoning.ProtectedData);
        Assert.Equal(string.Empty, reasoning.Text);
        Assert.Same(context.Messages, configuredMessages);
        Assert.Equal(selection == "inline" ? 0 : 1, catalog.ResolutionCalls);
        if (selection == "alias")
            Assert.Equal("catalog/agent-id-123", Assert.Single(catalog.RequestedIds));

        await client.GetResponseAsync(context.Messages);
        using var body = JsonDocument.Parse(captured!);
        var input = body.RootElement.GetProperty("input").EnumerateArray().ToArray();
        Assert.Equal(new[] { "message", "reasoning", "function_call", "function_call_output" },
            input.Select(item => item.GetProperty("type").GetString()));
        Assert.Equal(signature, input[1].GetProperty("encrypted_content").GetString());
        Assert.Equal("interactions-content-0", input[1].GetProperty("id").GetString());
        Assert.Equal(signature, input[1].GetProperty("provider_metadata").GetProperty("google").GetProperty("signature").GetString());
        Assert.Equal(toolName, input[2].GetProperty("name").GetString());
        Assert.Equal("call_652898", input[2].GetProperty("call_id").GetString());
        Assert.Equal("call_652898", input[3].GetProperty("call_id").GetString());
        Assert.Contains("PL", input[3].GetProperty("output").GetString());
        Assert.DoesNotContain("must-not-leak", captured);
    }

    private sealed class GoogleReplayModelCatalog(Agent resolvedAgent) : IModelCatalog
    {
        public int ResolutionCalls { get; private set; }
        public List<string> RequestedIds { get; } = [];

        public Task<ModelListResponse> ListAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<Agent?> ResolveAgentAsync(string modelId, CancellationToken cancellationToken = default)
        {
            ResolutionCalls++;
            RequestedIds.Add(modelId);
            return Task.FromResult<Agent?>(resolvedAgent);
        }

        public Task<IReadOnlyList<Agent>> ResolveAgentsAsync(IEnumerable<string> modelIds, CancellationToken cancellationToken = default)
        {
            ResolutionCalls++;
            RequestedIds.AddRange(modelIds);
            return Task.FromResult<IReadOnlyList<Agent>>([resolvedAgent]);
        }
    }
}
