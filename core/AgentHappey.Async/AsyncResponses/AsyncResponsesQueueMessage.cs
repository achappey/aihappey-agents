using System.Text.Json.Serialization;
using AIHappey.Responses;

namespace AgentHappey.AsyncResponses;

public sealed class AsyncResponsesQueueMessage
{
    [JsonPropertyName("response_id")]
    public string ResponseId { get; set; } = default!;

    [JsonPropertyName("created_at")]
    public long CreatedAt { get; set; }

    [JsonPropertyName("request")]
    public ResponseRequest Request { get; set; } = default!;

    [JsonPropertyName("context")]
    public AsyncResponsesRequestContext Context { get; set; } = new();
}
