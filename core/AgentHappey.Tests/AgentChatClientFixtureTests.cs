using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.Abstractions.Http;
using AgentHappey.Common.Extensions;
using AgentHappey.Common.Models;
using AgentHappey.Core;
using AgentHappey.Core.ChatClient;
using AIHappey.Vercel.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentHappey.Tests;

public sealed class AgentChatClientFixtureTests
{
    private const string StreamingFixturePath = "Fixtures/responses/raw/basic-response-stream.jsonl";
    private const string StructuredFixturePath = "Fixtures/responses/raw/structured-response-non-streaming.json";
    private const string GoogleEmptyReasoningFixturePath = "Fixtures/responses/raw/google-with-reasoning-responses-stream.jsonl";
    private const string OpenAiEmptyReasoningFixturePath = "Fixtures/responses/raw/openai-with-reasoning-responses-stream.jsonl";
    private const string OpenAiReasoningSummaryFixturePath = "Fixtures/responses/raw/openai-with-reasoning-summaries-responses-stream.jsonl";

    [Fact]
    public async Task Streaming_responses_are_captured_when_configured_in_agent_provider_metadata()
    {
        var captureRoot = Path.Combine(Path.GetTempPath(), "agenthappey-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(captureRoot);

        ProviderBackendCapture.Configure(new ProviderBackendCaptureOptions
        {
            Enabled = true,
            DevelopmentOnly = false,
            RootDirectory = captureRoot
        });

        try
        {
            var fixture = LoadFixture(StreamingFixturePath);

            using var httpClient = CreateHttpClient(_ => CreateStreamingResponse(fixture));
            using var client = CreateClient(
                httpClient,
                CreateAgent(new Dictionary<string, object>
                {
                    ["capture"] = new Dictionary<string, object?>
                    {
                        ["enabled"] = true,
                        ["relativeDirectory"] = "agents/tests",
                        ["fileName"] = "basic-response-stream"
                    }
                }));

            var updates = await CollectAsync(client.GetStreamingResponseAsync(CreateUserMessages("Say hello")));

            Assert.NotEmpty(updates);

            var capturePath = Path.Combine(captureRoot, "agents", "tests", "basic-response-stream.jsonl");
            Assert.True(File.Exists(capturePath));

            var captured = await File.ReadAllTextAsync(capturePath);
            Assert.Contains("event: response.created", captured);
            Assert.Contains("data: [DONE]", captured);
        }
        finally
        {
            ProviderBackendCapture.Disable();

            if (Directory.Exists(captureRoot))
                Directory.Delete(captureRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Streaming_text_response_roundtrips_to_text_ui_part()
    {
        var fixture = LoadFixture(StreamingFixturePath);

        using var httpClient = CreateHttpClient(_ => CreateStreamingResponse(fixture));
        using var client = CreateClient(httpClient, CreateAgent());

        var updates = await CollectAsync(client.GetStreamingResponseAsync(CreateUserMessages("Say hello")));

        var textContent = updates
            .SelectMany(update => update.Contents)
            .OfType<TextContent>()
            .Single(content => content.Text == "Hello world");

        var uiPart = Assert.IsType<TextUIPart>(textContent.ToUiPart());

        Assert.Equal("Hello world", uiPart.Text);
        Assert.Contains(updates, update => update.FinishReason is not null);
    }

    [Fact]
    public async Task Non_streaming_structured_response_roundtrips_to_data_ui_part()
    {
        var fixture = LoadFixture(StructuredFixturePath);

        using var httpClient = CreateHttpClient(_ => CreateJsonResponse(fixture));
        using var client = CreateClient(
            httpClient,
            CreateAgent(outputSchema: new OutputSchema
            {
                Properties = new Dictionary<string, Property>
                {
                    ["summary"] = new() { Type = "string", Required = true, Description = "Short summary" },
                    ["tone"] = new() { Type = "string", Description = "Detected tone" }
                }
            }));

        var response = await client.GetResponseAsync(CreateUserMessages("Return a structured response"));

        var dataContent = Assert.Single(response.Messages.SelectMany(message => message.Contents).OfType<DataContent>());

        Assert.Equal("structuredagent_output", dataContent.Name);
        Assert.Equal("application/json", dataContent.MediaType);

        var uiPart = dataContent.ToDataUIPart();
        var data = Assert.IsType<JsonElement>(uiPart.Data);

        Assert.Equal("data-structuredagent_output", uiPart.Type);
        Assert.Equal("hello", data.GetProperty("summary").GetString());
        Assert.Equal("friendly", data.GetProperty("tone").GetString());
    }

    [Fact]
    public async Task Streaming_google_empty_reasoning_roundtrips_to_reasoning_ui_parts_with_provider_metadata()
    {
        var uiParts = await CollectUiPartsAsync(GoogleEmptyReasoningFixturePath, "google/gemini-fixture");

        Assert.Single(uiParts.OfType<ReasoningStartUIPart>());
        Assert.Empty(uiParts.OfType<ReasoningDeltaUIPart>());
        Assert.Single(uiParts.OfType<ReasoningEndUIPart>());

        var reasoningStartPart = Assert.IsType<ReasoningStartUIPart>(uiParts.Single(part => part.Type == "reasoning-start"));
        Assert.Null(reasoningStartPart.ProviderMetadata);

        var reasoningEndPart = Assert.IsType<ReasoningEndUIPart>(uiParts.Single(part => part.Type == "reasoning-end"));
        var providerMetadata = Assert.Contains("StructuredAgent", reasoningEndPart.ProviderMetadata ?? []);

        Assert.Equal("EjQKMgEMOdbHfAqbDQ0MSWh7OEk5TFqB6kwRrBRu5Ab6tKOZZemhjJdsZRnbtIBISGPMr664", Assert.IsType<string>(providerMetadata["signature"]));
    }

    [Fact]
    public async Task Streaming_openai_empty_reasoning_roundtrips_to_reasoning_ui_parts_without_reasoning_delta_text()
    {
        var uiParts = await CollectUiPartsAsync(OpenAiEmptyReasoningFixturePath, "openai/gpt-fixture");

        Assert.Single(uiParts.OfType<ReasoningStartUIPart>());
        Assert.Empty(uiParts.OfType<ReasoningDeltaUIPart>());
        Assert.Single(uiParts.OfType<ReasoningEndUIPart>());

        var reasoningEndPart = Assert.IsType<ReasoningEndUIPart>(uiParts.Single(part => part.Type == "reasoning-end"));
        var providerMetadata = Assert.Contains("StructuredAgent", reasoningEndPart.ProviderMetadata ?? []);

        Assert.Equal("rs_075d7856f483de1b0169e7b03774108195b3cde6d425e20f38", Assert.IsType<string>(providerMetadata["item_id"]));
        Assert.True(providerMetadata.ContainsKey("encrypted_content"));
        Assert.True(providerMetadata.ContainsKey("summary"));

        var summary = Assert.IsAssignableFrom<IEnumerable<object?>>(providerMetadata["summary"]);
        Assert.Empty(summary);
    }

    [Fact]
    public async Task Streaming_openai_reasoning_summaries_continue_to_emit_reasoning_text_and_end_metadata()
    {
        var uiParts = await CollectUiPartsAsync(OpenAiReasoningSummaryFixturePath, "openai/gpt-fixture");

        Assert.Single(uiParts.OfType<ReasoningStartUIPart>());
        Assert.NotEmpty(uiParts.OfType<ReasoningDeltaUIPart>());
        Assert.Single(uiParts.OfType<ReasoningEndUIPart>());

        var reasoningText = string.Concat(uiParts.OfType<ReasoningDeltaUIPart>().Select(part => part.Delta));
        Assert.Contains("Responding to user casually", reasoningText);

        var reasoningEndPart = Assert.IsType<ReasoningEndUIPart>(uiParts.Single(part => part.Type == "reasoning-end"));
        var providerMetadata = Assert.Contains("StructuredAgent", reasoningEndPart.ProviderMetadata ?? []);

        Assert.True(providerMetadata.ContainsKey("encrypted_content"));

        var summary = Assert.IsAssignableFrom<IEnumerable<object?>>(providerMetadata["summary"]);
        Assert.NotEmpty(summary);
    }

    [Fact]
    public async Task Reasoning_ui_parts_roundtrip_back_to_responses_input_when_agent_name_matches()
    {
        var requestBody = await CaptureRequestBodyAsync(
            [new UIMessage
            {
                Id = "assistant-1",
                Role = Role.assistant,
                Parts =
                [
                    new ReasoningStartUIPart { Id = "reasoning-1" },
                    new ReasoningDeltaUIPart { Id = "reasoning-1", Delta = "Visible reasoning summary" },
                    new ReasoningEndUIPart
                    {
                        Id = "reasoning-1",
                        ProviderMetadata = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal)
                        {
                            ["StructuredAgent"] = new(StringComparer.Ordinal)
                            {
                                ["encrypted_content"] = "encrypted-payload"
                            }
                        }
                    }
                ]
            }],
            activeAgentNames: ["StructuredAgent"]);

        Assert.Contains("\"type\":\"reasoning\"", requestBody);
        Assert.Contains("\"encrypted_content\":\"encrypted-payload\"", requestBody);
        Assert.Contains("Visible reasoning summary", requestBody);
        Assert.DoesNotContain("reasoning-lifecycle", requestBody);
    }

    [Fact]
    public async Task Reasoning_ui_parts_are_dropped_on_roundtrip_when_agent_name_does_not_match()
    {
        var requestBody = await CaptureRequestBodyAsync(
            [new UIMessage
            {
                Id = "assistant-1",
                Role = Role.assistant,
                Parts =
                [
                    new ReasoningStartUIPart { Id = "reasoning-1" },
                    new ReasoningDeltaUIPart { Id = "reasoning-1", Delta = "Visible reasoning summary" },
                    new ReasoningEndUIPart
                    {
                        Id = "reasoning-1",
                        ProviderMetadata = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal)
                        {
                            ["OtherAgent"] = new(StringComparer.Ordinal)
                            {
                                ["encrypted_content"] = "encrypted-payload"
                            }
                        }
                    }
                ]
            }],
            activeAgentNames: ["StructuredAgent"]);

        Assert.DoesNotContain("\"type\":\"reasoning\"", requestBody);
        Assert.DoesNotContain("\"encrypted_content\":\"encrypted-payload\"", requestBody);
    }

    private static IEnumerable<ChatMessage> CreateUserMessages(string text)
        =>
        [
            new ChatMessage(ChatRole.User, [new TextContent(text)])
        ];

    private static AgentChatClient CreateClient(HttpClient httpClient, Agent agent)
        => new(httpClient, new StaticHttpClientFactory(httpClient), agent, new Dictionary<string, string?>());

    private static Agent CreateAgent(
        Dictionary<string, object>? providerMetadata = null,
        OutputSchema? outputSchema = null,
        string modelId = "openai/gpt-fixture")
        => new()
        {
            Name = "StructuredAgent",
            Description = "Fixture test agent",
            Instructions = "Return concise answers.",
            OutputSchema = outputSchema,
            Model = new Common.Models.AIModel
            {
                Id = modelId,
                ProviderMetadata = providerMetadata,
                Options = new AIModelOptions
                {
                    Temperature = 0
                }
            }
        };

    private static HttpClient CreateHttpClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new StaticResponseHttpMessageHandler(responder))
        {
            BaseAddress = new Uri("https://example.test/")
        };

    private static HttpResponseMessage CreateStreamingResponse(string body)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
        };

        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        return response;
    }

    private static HttpResponseMessage CreateJsonResponse(string body)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return response;
    }

    private static string LoadFixture(string relativePath)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> source, CancellationToken cancellationToken = default)
    {
        var items = new List<T>();

        await foreach (var item in source.WithCancellation(cancellationToken))
            items.Add(item);

        return items;
    }

    private static async Task<List<UIMessagePart>> CollectUiPartsAsync(string fixturePath, string modelId)
    {
        var fixture = LoadFixture(fixturePath);

        using var httpClient = CreateHttpClient(_ => CreateStreamingResponse(fixture));
        using var client = CreateClient(httpClient, CreateAgent(modelId: modelId));

        var agent = new ChatClientAgent(
            client,
            instructions: "Fixture test instructions",
            name: "FixtureAgent",
            description: "Fixture test agent");

        var mapper = new StreamingContentMapper();
        var updates = agent.RunStreamingAsync(CreateUserMessages("Say hello"));

        return await CollectAsync(mapper.MapAsync(updates));
    }

    private static async Task<string> CaptureRequestBodyAsync(
        IEnumerable<UIMessage> uiMessages,
        IEnumerable<string> activeAgentNames)
    {
        var fixture = LoadFixture(StructuredFixturePath);
        string requestBody = string.Empty;

        using var httpClient = CreateHttpClient(request =>
        {
            requestBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            return CreateJsonResponse(fixture);
        });

        using var client = CreateClient(httpClient, CreateAgent(modelId: "openai/gpt-fixture"));

        var messages = uiMessages.ToMessages(activeAgentNames).ToList();
        await client.GetResponseAsync(messages);

        return requestBody;
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = responder(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
