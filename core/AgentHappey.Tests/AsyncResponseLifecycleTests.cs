using System.Text.Json;
using AgentHappey.AsyncResponses;
using AIHappey.Responses;

namespace AgentHappey.Tests;

public sealed class AsyncResponseLifecycleTests
{
    [Fact]
    public void Queued_response_uses_typed_background_property_and_round_trips()
    {
        var response = AsyncResponseLifecycle.CreateQueuedResponse(new ResponseRequest
        {
            Model = "test-agent", Background = false, Store = false,
            Metadata = new() { ["business"] = "preserved" }
        });

        Assert.True(response.Background);
        Assert.False(response.Store);
        Assert.Equal("queued", response.Status);
        Assert.Equal("preserved", response.Metadata!["business"]);
        AssertNoBackgroundExtension(response);
        var stored = RoundTrip(response);
        Assert.Equal(response.Id, stored.Id);
        Assert.Equal(response.CreatedAt, stored.CreatedAt);
        Assert.True(stored.Background);
        AssertNoBackgroundExtension(stored);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public void Ensure_background_is_idempotent_and_removes_only_legacy_background_aliases(bool? background)
    {
        var response = new ResponseResult
        {
            Background = background,
            AdditionalProperties = new()
            {
                ["background"] = JsonSerializer.SerializeToElement(false),
                ["BACKGROUND"] = JsonSerializer.SerializeToElement(true),
                ["Background"] = JsonSerializer.SerializeToElement(false),
                ["provider_extension"] = JsonSerializer.SerializeToElement(new { retained = true })
            }
        };

        AsyncResponseLifecycle.EnsureBackgroundProperty(response);
        AsyncResponseLifecycle.EnsureBackgroundProperty(response);

        Assert.True(response.Background);
        AssertNoBackgroundExtension(response);
        Assert.Equal("provider_extension", Assert.Single(response.AdditionalProperties!).Key);
        Assert.True(response.AdditionalProperties!["provider_extension"].GetProperty("retained").GetBoolean());
        var stored = RoundTrip(response);
        AsyncResponseLifecycle.EnsureBackgroundProperty(stored);
        Assert.True(RoundTrip(stored).Background);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(null, true)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Normalizing_a_round_tripped_result_preserves_identity_and_extensions_without_duplicate_fields(
        bool? background, bool failed)
    {
        var result = JsonSerializer.Deserialize<ResponseResult>(JsonSerializer.Serialize(new
        {
            id = "resp_downstream", created_at = 1, completed_at = 2, background,
            provider_extension = new { retained = true },
            error = failed ? new { code = "test_failure", message = "safe failure" } : null
        }), ResponseJson.Default)!;
        var message = new AsyncResponsesQueueMessage
        {
            ResponseId = "resp_owned", CreatedAt = 123,
            Request = new ResponseRequest { Model = "test-agent" }
        };

        for (var pass = 0; pass < 3; pass++)
        {
            result = AsyncResponseLifecycle.NormalizeBackgroundResult(message, result);
            Assert.True(result.Background);
            AssertNoBackgroundExtension(result);
            result = RoundTrip(result);
            Assert.Equal(message.ResponseId, result.Id);
            Assert.Equal(message.CreatedAt, result.CreatedAt);
            Assert.Equal(2L, result.CompletedAt);
            Assert.Equal(failed ? "failed" : "completed", result.Status);
            Assert.Equal("test-agent", result.Model);
            Assert.True(result.AdditionalProperties!["provider_extension"].GetProperty("retained").GetBoolean());
            Assert.Equal(failed ? "test_failure" : null, result.Error?.Code);
        }
    }

    private static ResponseResult RoundTrip(ResponseResult response)
    {
        var json = JsonSerializer.Serialize(response, ResponseJson.Default);
        BackgroundResponseSerializationAssertions.AssertSingleBackgroundProperty(json);
        return JsonSerializer.Deserialize<ResponseResult>(json, ResponseJson.Default)!;
    }

    private static void AssertNoBackgroundExtension(ResponseResult response)
        => Assert.DoesNotContain(response.AdditionalProperties?.Keys ?? Enumerable.Empty<string>(),
            key => string.Equals(key, "background", StringComparison.OrdinalIgnoreCase));
}

internal static class BackgroundResponseSerializationAssertions
{
    internal static void AssertSingleBackgroundProperty(string json)
    {
        using var document = JsonDocument.Parse(json);
        var property = Assert.Single(document.RootElement.EnumerateObject(),
            item => string.Equals(item.Name, "background", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("background", property.Name);
        Assert.True(property.Value.GetBoolean());
    }
}
