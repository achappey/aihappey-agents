using System.Text.Json;
using AgentHappey.Core.Responses;
using AIHappey.Decisions;
using AIHappey.Responses;
using Microsoft.Extensions.AI;

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

    internal JsonElement? GetDecisionGatewayMetadata(IEnumerable<ChatMessage> messages)
        => ResponsesNativeMapper.CollectGatewayMetadata(messages, GetProviderKey());

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
