using System.Text.Json;
using AIHappey.Core.Models;
using AIHappey.Responses;

namespace AgentHappey.Core.ChatClient;

public partial class AgentChatClient
{
    private bool decisionExecution;
    private readonly List<ResponseUsage> decisionInferenceUsage = [];
    private object? decisionResponseText;
    private int? decisionMaxOutputTokens;
    private string? decisionSafetyIdentifier;
    private ResponseResult? decisionResponse;

    /// <summary>Request-local structured output from the first inference through the normal tool loop.</summary>
    public void BeginDecisionExecution(JsonElement schema, int? maxOutputTokens, string? safetyIdentifier)
    {
        decisionExecution = true;
        decisionResponseText = new { format = new { type = "json_schema", name = "agent_decision", strict = true, schema = schema.Clone() } };
        decisionMaxOutputTokens = maxOutputTokens;
        decisionSafetyIdentifier = safetyIdentifier;
    }

    private Dictionary<string, object?>? BuildDecisionProviderMetadata()
    {
        var metadata = BuildResponsesProviderMetadata();
        if (metadata is null) return null;
        // Provider metadata is materialized after request fields by some gateway providers.
        // Keep normal tool configuration, but never overwrite the request-local output schema.
        var source = JsonSerializer.SerializeToElement(metadata[GetProviderKey()], JsonSerializerOptions.Web);
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "text", "response_format", "responseFormat"
        };
        if (decisionMaxOutputTokens is not null)
            excluded.UnionWith(["max_output_tokens", "maxOutputTokens"]);
        var safe = source.EnumerateObject().Where(p => !excluded.Contains(p.Name))
            .ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        return safe.Count == 0 ? null : new() { [GetProviderKey()] = JsonSerializer.SerializeToElement(safe) };
    }

    private void CaptureDecisionInference(ResponseResult response)
    {
        if (response.Error is not null || response.Status is not (null or "completed")
            || response.AdditionalProperties?.TryGetValue("incomplete_details", out var incomplete) == true
                && incomplete.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            throw new InvalidOperationException("The agent decision inference did not complete successfully.");
        if (response.Usage is not null)
        {
            var usage = response.NormalizedUsage ?? JsonSerializer.SerializeToElement(response.Usage, ResponseJson.Default)
                .Deserialize<ResponseUsage>(ResponseJson.Default);
            if (usage is not null) decisionInferenceUsage.Add(usage);
        }
        decisionResponse = response;
    }

    public ResponseResult GetDecisionResponse()
        => decisionResponse ?? throw new InvalidOperationException("The agent did not produce a decision response.");

    public OpenAIDecisionUsage GetDecisionInferenceUsage()
    {
        int Sum(Func<ResponseUsage, int?> select) => (int)Math.Min(int.MaxValue,
            decisionInferenceUsage.Sum(u => (long)Math.Max(0, select(u) ?? 0)));
        return new()
        {
            InputTokens = Sum(u => u.InputTokens),
            OutputTokens = Sum(u => u.OutputTokens),
            TotalTokens = (int)Math.Min(int.MaxValue, decisionInferenceUsage.Sum(u => Math.Max(0L,
                u.TotalTokens ?? ((long)u.InputTokens.GetValueOrDefault() + u.OutputTokens.GetValueOrDefault())))),
            InputTokensDetails = new()
            {
                CachedTokens = Sum(u => u.InputTokensDetails?.CachedTokens),
                CacheWriteTokens = Sum(u => u.InputTokensDetails?.CacheWriteTokens)
            },
            OutputTokensDetails = new() { ReasoningTokens = Sum(u => u.OutputTokensDetails?.ReasoningTokens) }
        };
    }
}
