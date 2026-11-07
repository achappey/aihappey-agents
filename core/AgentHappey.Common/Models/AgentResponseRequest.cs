using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Responses;

namespace AgentHappey.Common.Models;

/// <summary>
/// Backend Responses contract. Agent/workflow selection belongs here, not in
/// application metadata or the shared provider-facing Responses contract.
/// </summary>
public sealed class AgentResponseRequest
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("models")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Models { get; set; }

    [JsonPropertyName("agent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Agent? Agent { get; set; }

    [JsonPropertyName("agents")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<Agent>? Agents { get; set; }

    [JsonPropertyName("workflowType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WorkflowType { get; set; }

    [JsonPropertyName("workflow_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WorkflowTypeSnakeCase { get; set; }

    [JsonPropertyName("workflowMetadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorkflowMetadata? WorkflowMetadata { get; set; }

    [JsonPropertyName("workflow_metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorkflowMetadata? WorkflowMetadataSnakeCase { get; set; }

    [JsonPropertyName("instructions")]
    public string? Instructions { get; set; }

    [JsonPropertyName("input")]
    public ResponseInput? Input { get; set; }

    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    [JsonPropertyName("top_p")]
    public double? TopP { get; set; }

    [JsonPropertyName("truncation")]
    public TruncationStrategy? Truncation { get; set; }

    [JsonPropertyName("max_output_tokens")]
    public int? MaxOutputTokens { get; set; }

    [JsonPropertyName("top_logprobs")]
    public int? TopLogprobs { get; set; }

    [JsonPropertyName("parallel_tool_calls")]
    public bool? ParallelToolCalls { get; set; }

    [JsonPropertyName("stream")]
    public bool? Stream { get; set; }

    [JsonPropertyName("store")]
    public bool? Store { get; set; }

    [JsonPropertyName("previous_response_id")]
    public string? PreviousResponseId { get; set; }

    [JsonPropertyName("user")]
    public string? User { get; set; }

    [JsonPropertyName("safety_identifier")]
    public string? SafetyIdentifier { get; set; }

    [JsonPropertyName("prompt_cache_retention")]
    public string? PromptCacheRetention { get; set; }

    [JsonPropertyName("max_tool_calls")]
    public int? MaxToolCalls { get; set; }

    [JsonPropertyName("prompt")]
    public object? Prompt { get; set; }

    [JsonPropertyName("service_tier")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ServiceTier { get; set; }

    [JsonPropertyName("text")]
    public object? Text { get; set; }

    [JsonPropertyName("include")]
    public List<string>? Include { get; set; }

    /// <summary>Application metadata only; never used for runtime selection.</summary>
    [JsonPropertyName("metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object?>? Metadata { get; set; }

    [JsonIgnore]
    public Dictionary<string, string>? Headers { get; set; }

    [JsonIgnore]
    public Dictionary<string, object?>? ExecutionMetadata { get; set; }

    [JsonPropertyName("tools")]
    public List<ResponseToolDefinition>? Tools { get; set; }

    [JsonPropertyName("tool_choice")]
    public object? ToolChoice { get; set; }

    [JsonPropertyName("reasoning")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Reasoning? Reasoning { get; set; }

    [JsonPropertyName("context_management")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement[]? ContextManagement { get; set; }

    // Retain unrelated protocol extensions, but routing has explicit properties above.
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }

    [JsonPropertyName("background")]
    public bool? Background { get; set; }

    [JsonPropertyName("prompt_cache_key")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PromptCacheKey { get; set; }

    [JsonPropertyName("prompt_cache_options")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? PromptCacheOptions { get; set; }

    [JsonPropertyName("stream_options")]
    public object? StreamOptions { get; set; }

    [JsonPropertyName("moderation")]
    public object? Moderation { get; set; }

    [JsonPropertyName("conversation")]
    public object? Conversation { get; set; }

    [JsonPropertyName("access_programs")]
    public object? AccessPrograms { get; set; }

    /// <summary>Project protocol fields without leaking backend routing into extensions.</summary>
    public ResponseRequest ToResponseRequest() => new()
    {
        Model = Model,
        Instructions = Instructions,
        Input = Input,
        Temperature = Temperature,
        TopP = TopP,
        Truncation = Truncation,
        MaxOutputTokens = MaxOutputTokens,
        TopLogprobs = TopLogprobs,
        ParallelToolCalls = ParallelToolCalls,
        Stream = Stream,
        Store = Store,
        PreviousResponseId = PreviousResponseId,
        User = User,
        SafetyIdentifier = SafetyIdentifier,
        PromptCacheRetention = PromptCacheRetention,
        MaxToolCalls = MaxToolCalls,
        Prompt = Prompt,
        ServiceTier = ServiceTier,
        Text = Text,
        Include = Include,
        Metadata = Metadata,
        Headers = Headers,
        ExecutionMetadata = ExecutionMetadata,
        Tools = Tools,
        ToolChoice = ToolChoice,
        Reasoning = Reasoning,
        ContextManagement = ContextManagement,
        AdditionalProperties = AdditionalProperties?.Where(property => !IsRoutingProperty(property.Key))
            .ToDictionary(property => property.Key, property => property.Value),
        Background = Background,
        PromptCacheKey = PromptCacheKey,
        PromptCacheOptions = PromptCacheOptions,
        StreamOptions = StreamOptions,
        Moderation = Moderation,
        Conversation = Conversation,
        AccessPrograms = AccessPrograms
    };

    private static bool IsRoutingProperty(string name)
        => name.Equals("agent", StringComparison.OrdinalIgnoreCase)
            || name.Equals("agents", StringComparison.OrdinalIgnoreCase)
            || name.Equals("models", StringComparison.OrdinalIgnoreCase)
            || name.Equals("workflowType", StringComparison.OrdinalIgnoreCase)
            || name.Equals("workflow_type", StringComparison.OrdinalIgnoreCase)
            || name.Equals("workflowMetadata", StringComparison.OrdinalIgnoreCase)
            || name.Equals("workflow_metadata", StringComparison.OrdinalIgnoreCase);
}
