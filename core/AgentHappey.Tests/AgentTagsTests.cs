using System.Text.Json;
using AgentHappey.Common.Models;
using AgentHappey.Agents.JSON;
using AgentHappey.Core.MCP.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace AgentHappey.Tests;

public sealed class AgentTagsTests
{
    [Theory]
    [InlineData("""{"name":"test"}""", null)]
    [InlineData("""{"name":"test","owned_by":null}""", null)]
    [InlineData("""{"name":"test","owned_by":""}""", "")]
    [InlineData("""{"name":"test","owned_by":"  "}""", "  ")]
    [InlineData("""{"name":"test","owned_by":" Support team "}""", " Support team ")]
    public async Task Owner_round_trips_and_projects_to_model_and_tool_listings(string json, string? expectedOwner)
    {
        var agent = JsonSerializer.Deserialize<Agent>(json, JsonSerializerOptions.Web)!;
        var stored = JsonSerializer.SerializeToElement(agent, JsonSerializerOptions.Web);
        Assert.Equal(expectedOwner is not null, stored.TryGetProperty("owned_by", out var owner));
        if (expectedOwner is not null) Assert.Equal(expectedOwner, owner.GetString());
        Assert.Equal(expectedOwner, stored.Deserialize<Agent>(JsonSerializerOptions.Web)!.OwnedBy);

        var directory = Path.Combine(Path.GetTempPath(), "agent-owner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "TestAgent.json"), stored.GetRawText());
            var model = directory.GetModels().Single();
            Assert.Equal(expectedOwner?.Trim() ?? "", model.OwnedBy);
            Assert.Equal(expectedOwner, model.Agent!.OwnedBy);
            Assert.Equal(expectedOwner, directory.GetAgents(null).Single().OwnedBy);
            using var services = new ServiceCollection().AddSingleton<IModelCatalog>(new ModelCatalog([new TestModelSource(agent)])).BuildServiceProvider();
            var listing = await AgentTools.Agents_List(services, null!);
            var listingJson = JsonSerializer.SerializeToElement(listing.StructuredContent, JsonSerializerOptions.Web);
            Assert.Equal(expectedOwner?.Trim() ?? "", listingJson.GetProperty("data")[0].GetProperty("owned_by").GetString());
            var detail = await AgentTools.Agents_Get(agent.Name, services, null!);
            Assert.Equal(expectedOwner, JsonSerializer.SerializeToElement(detail.StructuredContent, JsonSerializerOptions.Web).Deserialize<Agent>(JsonSerializerOptions.Web)!.OwnedBy);
        }
        finally { Directory.Delete(directory, true); }
    }

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
                [new Model { Id = agent.Name, Agent = agent, Tags = agent.Tags, OwnedBy = agent.OwnedBy?.Trim() ?? "" }]);

        public Task<IReadOnlyList<Agent>> GetAgentsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Agent>>([agent]);
    }
}
