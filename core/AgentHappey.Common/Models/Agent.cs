
using System.Text.Json.Serialization;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace AgentHappey.Common.Models;

public class Agent
{
    [JsonPropertyName("model")]
    public AIModel Model { get; set; } = null!;

    [JsonPropertyName("name")]
    public string Name { get; set; } = null!;

    [JsonPropertyName("description")]
    public string Description { get; set; } = null!;

    [JsonPropertyName("instructions")]
    public string Instructions { get; set; } = null!;

    [JsonPropertyName("argumentHint")]
    public string? ArgumentHint { get; set; }

    [JsonPropertyName("outputSchema")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OutputSchema? OutputSchema { get; set; }

    [JsonPropertyName("mcpServers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, McpServer>? McpServers { get; set; }

    [JsonPropertyName("mcpClient")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public McpClient? McpClient { get; set; }

    [JsonPropertyName("skills")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IEnumerable<AISkill>? Skills { get; set; }

    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IEnumerable<AgentTool>? Tools { get; set; }

    [JsonPropertyName("icons")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IEnumerable<Icon>? Icons { get; set; }

}

public class AgentTool
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = null!;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public class OutputSchema
{
    [JsonPropertyName("properties")]
    public Dictionary<string, Property> Properties { get; set; } = [];
}

public class Property
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = null!;

    [JsonPropertyName("required")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Required { get; set; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }
}


public class McpClient
{
    [JsonPropertyName("policy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public McpPolicy? Policy { get; set; }

    [JsonPropertyName("capabilities")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ClientCapabilities? Capabilities { get; set; }
}

[JsonConverter(typeof(AISkillJsonConverter))]
public class AISkill
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("type")]
    public virtual string Type { get; set; } = "inline";

    [JsonPropertyName("source")]
    public AISkillSource? Source { get; set; }
}

public sealed class SkillReference : AISkill
{
    public override string Type { get; set; } = "skill_reference";

    [JsonPropertyName("skill_id")]
    public string SkillId { get; set; } = null!;

    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; set; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SkillId) || SkillId.Length is < 1 or > 64)
            throw new InvalidOperationException("Referenced skill_id must contain between 1 and 64 characters.");

        if (Version is null)
            return;

        if (string.Equals(Version, "latest", StringComparison.Ordinal))
            return;
    }
}

public sealed class AISkillJsonConverter : JsonConverter<AISkill>
{
    public override AISkill? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("An agent skill must be a JSON object.");

        var discriminator = TryGetProperty(root, "type", out var typeElement)
            ? typeElement.GetString()
            : "inline";

        if (string.Equals(discriminator, "skill_reference", StringComparison.Ordinal))
        {
            var reference = new SkillReference
            {
                SkillId = GetRequiredString(root, "skill_id"),
                Version = TryGetProperty(root, "version", out var versionElement)
                    && versionElement.ValueKind != JsonValueKind.Null
                        ? versionElement.GetString()
                        : null
            };

            try
            {
                reference.Validate();
            }
            catch (InvalidOperationException exception)
            {
                throw new JsonException(exception.Message, exception);
            }

            return reference;
        }

        if (!string.Equals(discriminator, "inline", StringComparison.Ordinal))
            throw new JsonException($"Unsupported agent skill type '{discriminator}'. Expected 'inline' or 'skill_reference'.");

        var inline = new AISkill
        {
            Type = "inline",
            Name = GetRequiredString(root, "name"),
            Description = GetRequiredString(root, "description")
        };

        if (!TryGetProperty(root, "source", out var sourceElement)
            || sourceElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"Inline skill '{inline.Name}' is missing its source payload.");
        }

        inline.Source = new AISkillSource
        {
            Data = GetRequiredString(sourceElement, "data"),
            MediaType = GetRequiredString(sourceElement, "media_type"),
            Type = GetRequiredString(sourceElement, "type")
        };

        return inline;
    }

    public override void Write(Utf8JsonWriter writer, AISkill value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        if (value is SkillReference reference)
        {
            reference.Validate();
            writer.WriteString("skill_id", reference.SkillId);
            writer.WriteString("type", "skill_reference");

            if (reference.Version is not null)
                writer.WriteString("version", reference.Version);

            writer.WriteEndObject();
            return;
        }

        writer.WriteString("description", value.Description);
        writer.WriteString("name", value.Name);

        writer.WritePropertyName("source");
        JsonSerializer.Serialize(writer, value.Source, options);

        writer.WriteString("type", "inline");
        writer.WriteEndObject();
    }

    private static string GetRequiredString(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            throw new JsonException($"Agent skill property '{propertyName}' must be a string.");
        }

        return property.GetString()!;
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}

public class AISkillSource
{
    [JsonPropertyName("data")]
    public string Data { get; set; } = null!;

    [JsonPropertyName("media_type")]
    public string MediaType { get; set; } = "application/zip";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "base64";
}

public class AIModel
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = null!;

    [JsonPropertyName("options")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AIModelOptions? Options { get; set; }

    [JsonPropertyName("providerMetadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object>? ProviderMetadata { get; set; }

    [JsonPropertyName("providerHeaders")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? ProviderHeaders { get; set; }
}

public class AIModelOptions
{
    [JsonPropertyName("temperature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? Temperature { get; set; }
}

public class McpServer
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "http";

    [JsonPropertyName("url")]
    public string Url { get; set; } = null!;

    [JsonPropertyName("disabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Disabled { get; set; }

    [JsonPropertyName("defer_loading")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? DeferLoading { get; set; }

    [JsonPropertyName("namespace")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Namespace { get; set; }

    [JsonPropertyName("allowed_callers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IEnumerable<string>? AllowedCallers { get; set; }

    [JsonPropertyName("headers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object>? Headers { get; set; }
}

public class McpPolicy
{
    [JsonPropertyName("readOnlyHint")]
    public bool? ReadOnly { get; set; }

    [JsonPropertyName("idempotentHint")]
    public bool? Idempotent { get; set; }

    [JsonPropertyName("openWorldHint")]
    public bool? OpenWorld { get; set; }

    [JsonPropertyName("destructiveHint")]
    public bool? Destructive { get; set; }
}
