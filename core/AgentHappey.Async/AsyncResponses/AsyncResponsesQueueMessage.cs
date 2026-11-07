using System.Text.Json.Serialization;
using AgentHappey.Common.Models;

namespace AgentHappey.AsyncResponses;

public sealed class AsyncResponsesQueueMessage
{
    [JsonPropertyName("response_id")]
    public string ResponseId { get; set; } = default!;

    [JsonPropertyName("created_at")]
    public long CreatedAt { get; set; }

    [JsonPropertyName("request")]
    public AgentResponseRequest Request { get; set; } = default!;

    [JsonPropertyName("context")]
    public AsyncResponsesRequestContext Context { get; set; } = new();
}
