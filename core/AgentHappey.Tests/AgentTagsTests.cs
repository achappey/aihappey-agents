using System.Text.Json;
using AgentHappey.Common.Models;
using AgentHappey.Core.MCP.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace AgentHappey.Tests;

public sealed class AgentTagsTests
{
    [Theory]
    [InlineData("""{"name":"test"}""", null)]
    [InlineData("""{"name":"test","tags":null}""", null)]
    [InlineData("""{"name":"test","tags":[]}""", "[]")]
    [InlineData("""{"name":"test","tags":["support","internal"]}""", """["support","internal"]""")]
    public async Task Tags_round_trip_through_storage_listings_and_details(string json, string? expectedTags)
    {
        var agent = JsonSerializer.Deserialize<Agent>(json, JsonSerializerOptions.Web)!;
        var stored = JsonSerializer.SerializeToElement(agent, JsonSerializerOptions.Web);
        AssertTags(stored, expectedTags);

        var restored = stored.Deserialize<Agent>(JsonSerializerOptions.Web)!;
        Assert.Equal(agent.Tags, restored.Tags);

        using var services = new ServiceCollection()
            .AddSingleton<IModelCatalog>(new ModelCatalog([new TestModelSource(restored)]))
            .BuildServiceProvider();

        var catalog = services.GetRequiredService<IModelCatalog>();
        var models = JsonSerializer.SerializeToElement(await catalog.ListAsync(), JsonSerializerOptions.Web);
        AssertTags(models.GetProperty("data")[0], expectedTags);
        AssertTags(models.GetProperty("data")[0].GetProperty("agent"), expectedTags);

        var listing = await AgentTools.Agents_List(services, null!);
        var listingJson = JsonSerializer.SerializeToElement(listing.StructuredContent, JsonSerializerOptions.Web);
        AssertTags(listingJson.GetProperty("data")[0], expectedTags);
        Assert.False(listingJson.GetProperty("data")[0].TryGetProperty("agent", out _));

        var detail = await AgentTools.Agents_Get(restored.Name, services, null!);
        AssertTags(JsonSerializer.SerializeToElement(detail.StructuredContent, JsonSerializerOptions.Web), expectedTags);
    }

    private static void AssertTags(JsonElement element, string? expectedTags)
    {
        if (expectedTags is null)
            Assert.False(element.TryGetProperty("tags", out _));
        else
            Assert.Equal(expectedTags, element.GetProperty("tags").GetRawText());
    }

    private sealed class TestModelSource(Agent agent) : IModelSource
    {
        public Task<IReadOnlyList<Model>> GetModelsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Model>>(
                [new Model { Id = agent.Name, Agent = agent, Tags = agent.Tags }]);

        public Task<IReadOnlyList<Agent>> GetAgentsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Agent>>([agent]);
    }
}
