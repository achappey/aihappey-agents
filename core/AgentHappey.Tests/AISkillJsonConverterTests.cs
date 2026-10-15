using System.Text.Json;
using AgentHappey.Common.Models;

namespace AgentHappey.Tests;

public sealed class AISkillJsonConverterTests
{
    [Theory]
    [InlineData("""{"type":true,"name":"test","description":"test","source":{}}""")]
    [InlineData("""{"type":1,"name":"test","description":"test","source":{}}""")]
    [InlineData("""{"type":"skill_reference","skill_id":"skill-a","version":1}""")]
    [InlineData("""{"type":"skill_reference","skill_id":"skill-a","version":false}""")]
    public void Deserialize_rejects_non_string_skill_type_and_version(string json)
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<AISkill>(json, JsonSerializerOptions.Web));
    }

    [Fact]
    public void Deserialize_preserves_null_skill_reference_version()
    {
        var skill = JsonSerializer.Deserialize<AISkill>(
            """{"type":"skill_reference","skill_id":"skill-a","version":null}""",
            JsonSerializerOptions.Web);

        var reference = Assert.IsType<SkillReference>(skill);
        Assert.Null(reference.Version);
    }
}
