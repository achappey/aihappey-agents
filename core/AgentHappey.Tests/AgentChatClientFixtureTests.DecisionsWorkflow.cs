using System.Text.Json;
using AgentHappey.Common.Models;
using AgentHappey.Core;
using AgentHappey.Core.ChatClient;
using AgentHappey.Core.ChatRuntime;
using Microsoft.AspNetCore.Http;

namespace AgentHappey.Tests;

public sealed partial class AgentChatClientFixtureTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Decisions_workflow_discovers_models_only_for_selected_multi_model_agent(bool streaming, bool selectedMultiModel)
    {
        var paths = new List<string>();
        using var http = CreateHttpClient(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            paths.Add(path);
            if (path == "/v1/models")
            {
                Assert.True(selectedMultiModel);
                return CreateJsonResponse(RoutingCatalog);
            }

            using var json = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var root = json.RootElement;
            if (path == "/api/decisions")
            {
                if (root.GetProperty("questions").TryGetProperty("agent", out var question))
                {
                    Assert.Equal(["/api/decisions"], paths); // No candidate model discovery before agent selection.
                    Assert.Equal("openai/workflow-router", root.GetProperty("model").GetString());
                    Assert.Equal(2, question.GetProperty("criteria").EnumerateObject().Count());
                    return CreateJsonResponse("""{"answers":{"agent":{"type":"choice","choice":"agent-1"}}}""");
                }

                Assert.True(selectedMultiModel);
                Assert.Equal("openai/router", root.GetProperty("model").GetString());
                Assert.Equal(2, root.GetProperty("questions").GetProperty("model").GetProperty("criteria").EnumerateObject().Count());
                return CreateJsonResponse("""{"answers":{"model":{"type":"choice","choice":"anthropic/second"}}}""");
            }

            Assert.Equal("/v1/responses", path);
            Assert.Equal("anthropic/second", root.GetProperty("model").GetString());
            Assert.Equal("SelectedAgent", Assert.Single(request.Headers.GetValues("X-Agent-Name")));
            return streaming ? CreateStreamingResponse(LoadFixture(StreamingFixturePath)) : CreateJsonResponse(LoadFixture(StructuredFixturePath));
        });
        var unselected = CreateAgent();
        unselected.Name = "UnselectedAgent";
        unselected.Models = [Answer("openai/router"), Answer("openai/first"), Answer("anthropic/second")];
        var selected = CreateAgent(modelId: "google/legacy");
        selected.Name = "SelectedAgent";
        selected.Models = selectedMultiModel
            ? [Answer("openai/router", "decision"), Answer("openai/first", "language"), Answer("anthropic/second", "language")]
            : [Answer("anthropic/second")];
        var original = JsonSerializer.Serialize(new[] { unselected, selected });
        var request = new ChatRuntimeRequest(
            Messages: CreateUserMessages("Choose the best agent").ToList(),
            Model: null,
            Models: null,
            Agents: [unselected, selected],
            WorkflowType: "decisions",
            WorkflowMetadata: new() { Decisions = new() { Model = "openai/workflow-router" } });
        var orchestrator = new ChatRuntimeOrchestrator(new StreamingContentMapper(), new ModelCatalog([]));
        var configured = new List<AgentChatClient>();
        var clients = new List<AgentChatClient>();
        try
        {
            var context = await orchestrator.PrepareAsync(new DefaultHttpContext().Response, request, agent =>
            {
                var client = CreateClient(http, agent);
                clients.Add(client);
                return client;
            }, (client, _) => configured.Add(client));

            Assert.Same(selected, Assert.Single(context.ResolvedAgents));
            Assert.Single(context.Agents);
            Assert.Equal(2, clients.Count); // Routing transport plus the selected runtime client only.
            Assert.Same(clients[1], Assert.Single(configured));
            if (streaming)
            {
                await foreach (var _ in orchestrator.StreamAgentAsync(context)) { }
            }
            else
                await orchestrator.RunAgentAsync(context);

            Assert.Equal(selectedMultiModel
                ? ["/api/decisions", "/v1/models", "/api/decisions", "/v1/responses"]
                : new[] { "/api/decisions", "/v1/responses" }, paths);
            Assert.Equal(original, JsonSerializer.Serialize(new[] { unselected, selected }));
        }
        finally
        {
            foreach (var client in clients) client.Dispose();
        }
    }
}
