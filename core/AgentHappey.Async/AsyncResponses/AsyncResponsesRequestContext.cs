using System.Text.Json.Serialization;

namespace AgentHappey.AsyncResponses;

public sealed class AsyncResponsesRequestContext
{
    [JsonPropertyName("user_id")]
    public string? UserId { get; set; }

    [JsonPropertyName("user_access_token")]
    public string? UserAccessToken { get; set; }

    [JsonPropertyName("headers")]
    public Dictionary<string, string?> Headers { get; set; } = [];

    [JsonPropertyName("correlation_id")]
    public string? CorrelationId { get; set; }

    [JsonPropertyName("ai_endpoint")]
    public string? AiEndpoint { get; set; }

    [JsonPropertyName("ai_scopes")]
    public string? AiScopes { get; set; }
}
