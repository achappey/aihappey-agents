using System.Text.Json;
using AgentHappey.Common.Extensions;
using AgentHappey.Core.Responses;
using AIHappey.Responses;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.AI;

namespace AgentHappey.Tests;

public sealed partial class AgentChatClientFixtureTests
{
    public static IEnumerable<object[]> UrlAttachmentCases()
    {
        foreach (var streaming in new[] { false, true })
        foreach (var (url, mediaType, filename) in new[]
        {
            ("https://example.com/elephant.jpg", "image/jpeg", "elephant.jpg"),
            ("http://example.com/elephant.jpg", "image/jpeg", "elephant.jpg"),
            ("https://example.com/download?signature=a%2Fb&key=1", "image/png", "picture.png"),
            ("https://example.com/report.pdf", "application/pdf", "report.pdf"),
            ("http://example.com/report.pdf", "application/pdf", "report.pdf"),
            ("https://example.com/document", "text/plain", "notes.txt"),
            ("https://example.com/no-name.pdf", "application/pdf", (string?)null),
            ("data:image/png;base64,aGVsbG8=", "image/png", "picture.png"),
            ("data:application/pdf;base64,aGVsbG8=", "application/pdf", "report.pdf")
        })
            yield return [url, mediaType, filename!, streaming];
    }

    [Theory]
    [MemberData(nameof(UrlAttachmentCases))]
    public async Task Chat_url_attachments_reach_responses_without_being_decoded(
        string url, string mediaType, string? filename, bool streaming)
    {
        var messages = new[]
        {
            new UIMessage
            {
                Id = "user-attachment",
                Role = Role.user,
                Parts = [new FileUIPart { Url = url, MediaType = mediaType, Filename = filename }]
            }
        }.ToMessages().ToList();

        AssertAttachmentContent(Assert.Single(Assert.Single(messages).Contents), url, mediaType, filename);
        await AssertAttachmentRequestAsync(messages, url, mediaType, filename, streaming);
    }

    [Theory]
    [MemberData(nameof(UrlAttachmentCases))]
    public async Task Responses_url_attachments_reach_downstream_responses_without_being_decoded(
        string url, string mediaType, string? filename, bool streaming)
    {
        var image = mediaType.StartsWith("image/");
        ResponseContentPart part = image
            ? new InputImagePart { ImageUrl = url }
            : new InputFilePart
            {
                FileUrl = url.StartsWith("data:") ? null : url,
                FileData = url.StartsWith("data:") ? url : null,
                Filename = filename
            };
        var requestJson = JsonSerializer.Serialize(new
        {
            model = "openai/gpt-fixture",
            input = new[] { new { role = "user", content = new[] { part } } }
        }, ResponseJson.Default);
        var request = JsonSerializer.Deserialize<ResponseRequest>(requestJson, ResponseJson.Default)!;
        var mapped = request.ToChatRuntimeRequest();

        // Responses image inputs carry their kind rather than a required MIME type.
        var expectedMediaType = url.StartsWith("data:") ? mediaType : image ? "image/*" : "application/octet-stream";
        AssertAttachmentContent(Assert.Single(Assert.Single(mapped.Messages).Contents),
            url, expectedMediaType, image ? null : filename);
        await AssertAttachmentRequestAsync(mapped.Messages, url, mediaType, image ? null : filename, streaming);
    }

    [Theory]
    [InlineData("https://example.com/image.png", "image/png", false)]
    [InlineData("https://example.com/report.pdf", "application/pdf", false)]
    [InlineData("https://example.com/image.png", "image/png", true)]
    [InlineData("https://example.com/report.pdf", "application/pdf", true)]
    public async Task Remote_attachments_are_rejected_for_providers_without_native_url_support(
        string url, string mediaType, bool streaming)
    {
        var messages = new[]
        {
            new UIMessage
            {
                Id = "user-attachment",
                Role = Role.user,
                Parts = [new FileUIPart { Url = url, MediaType = mediaType }]
            }
        }.ToMessages().ToList();

        var requestSent = false;
        using var httpClient = CreateHttpClient(_ =>
        {
            requestSent = true;
            return CreateJsonResponse(LoadFixture(StructuredFixturePath));
        });
        using var client = CreateClient(httpClient, CreateAgent(modelId: "anthropic/model"));

        if (streaming)
        {
            await Assert.ThrowsAsync<NotSupportedException>(async () =>
                await CollectAsync(client.GetStreamingResponseAsync(messages)));
        }
        else
        {
            await Assert.ThrowsAsync<NotSupportedException>(async () =>
                await client.GetResponseAsync(messages));
        }

        Assert.False(requestSent);
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/report.pdf")]
    [InlineData("javascript:alert(1)")]
    public void Unsupported_attachment_url_schemes_are_rejected(string url)
        => Assert.Throws<NotSupportedException>(() => url.ToFileContent("application/octet-stream"));

    private static void AssertAttachmentContent(AIContent content, string url, string mediaType, string? filename)
    {
        if (url.StartsWith("data:"))
        {
            var data = Assert.IsType<DataContent>(content);
            Assert.Equal(url, data.Uri);
            Assert.Equal(mediaType, data.MediaType);
            Assert.Equal(filename, data.Name);
        }
        else
        {
            var remote = Assert.IsType<UriContent>(content);
            Assert.Equal(url, remote.Uri.OriginalString);
            Assert.Equal(mediaType, remote.MediaType);
            Assert.Equal(filename, remote.AdditionalProperties?.GetValueOrDefault("filename"));
        }
    }

    private static async Task AssertAttachmentRequestAsync(
        IEnumerable<ChatMessage> messages, string url, string mediaType, string? filename, bool streaming)
    {
        string? captured = null;
        using var httpClient = CreateHttpClient(request =>
        {
            Assert.Null(captured); // Only the inference request is made; attachments are not downloaded.
            captured = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return streaming
                ? CreateStreamingResponse(LoadFixture(StreamingFixturePath))
                : CreateJsonResponse(LoadFixture(StructuredFixturePath));
        });
        using var client = CreateClient(httpClient, CreateAgent(modelId: "openai/gpt-fixture"));

        if (streaming)
            await CollectAsync(client.GetStreamingResponseAsync(messages));
        else
            await client.GetResponseAsync(messages);

        using var body = JsonDocument.Parse(Assert.IsType<string>(captured));
        var message = Assert.Single(body.RootElement.GetProperty("input").EnumerateArray());
        Assert.Equal("user", message.GetProperty("role").GetString());
        var part = Assert.Single(message.GetProperty("content").EnumerateArray());
        var image = mediaType.StartsWith("image/");
        Assert.Equal(image ? "input_image" : "input_file", part.GetProperty("type").GetString());
        var payloadKey = image ? "image_url" : url.StartsWith("data:") ? "file_data" : "file_url";
        Assert.Equal(url, part.GetProperty(payloadKey).GetString());
        if (!image)
        {
            Assert.Equal(filename, part.TryGetProperty("filename", out var name) ? name.GetString() : null);
            var unusedKey = payloadKey == "file_data" ? "file_url" : "file_data";
            Assert.True(!part.TryGetProperty(unusedKey, out var unused) || unused.ValueKind == JsonValueKind.Null);
        }
    }
}
