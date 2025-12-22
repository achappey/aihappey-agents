
using System.Text.Json.Serialization;
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

    // [JsonPropertyName("mcp")]
    // [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    // public Mcp? Mcp { get; set; }

    [JsonPropertyName("outputSchema")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OutputSchema? OutputSchema { get; set; }

    [JsonPropertyName("mcpServers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, McpServer>? McpServers { get; set; }

    [JsonPropertyName("mcpClient")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public McpClient? McpClient { get; set; }
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

public class Mcp
{
    [JsonPropertyName("servers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IEnumerable<McpServer>? Servers { get; set; }

    [JsonPropertyName("policy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public McpPolicy? Policy { get; set; }

    [JsonPropertyName("clientCapabilities")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ClientCapabilities? ClientCapabilities { get; set; }
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
    public bool? Disabled { get; set; }

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