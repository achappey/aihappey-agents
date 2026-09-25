using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AgentHappey.Common.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using LocalEvaluatorConfig = AgentHappey.Common.Models.LocalEvaluator;
using FrameworkLocalEvaluator = Microsoft.Agents.AI.LocalEvaluator;

namespace AgentHappey.Core.Evaluations;

internal static class LocalEvaluation
{
    private const string FinishMetadataName = "finish_metadata";

    internal static FrameworkLocalEvaluator? Create(LocalEvaluatorConfig? config)
    {
        if (config is null)
            return null;

        List<EvalCheck> checks = [];

        if (config.NonEmpty is not null)
            checks.Add(EvalChecks.NonEmpty(config.NonEmpty.MinLength ?? 1));

        if (config.KeywordCheck is { } keywordCheck)
        {
            var keywords = keywordCheck.Keywords
                .Where(keyword => !string.IsNullOrWhiteSpace(keyword))
                .ToArray();

            if (keywords.Length > 0)
                checks.Add(EvalChecks.KeywordCheck(keywordCheck.CaseSensitive ?? false, keywords));
        }

        if (config.ToolCallsPresent == true)
            checks.Add(EvalChecks.ToolCallsPresent());

        if (config.ToolCalledCheck is { } toolCalledCheck)
        {
            var toolNames = toolCalledCheck.ToolNames
                .Where(toolName => !string.IsNullOrWhiteSpace(toolName))
                .ToArray();

            if (toolNames.Length > 0)
                checks.Add(EvalChecks.ToolCalledCheck(ParseToolCalledMode(toolCalledCheck.Mode), toolNames));
        }

        if (config.HasImageContent == true)
            checks.Add(EvalChecks.HasImageContent());

        return checks.Count == 0 ? null : new FrameworkLocalEvaluator([.. checks]);
    }

    internal static string GetQuery(IReadOnlyList<ChatMessage> messages)
        => messages.LastOrDefault(message => message.Role == ChatRole.User)?.Text ?? string.Empty;

    internal static AgentEvaluationResults CreateFailedResult(Exception exception)
        => new("Local", [], [])
        {
            Status = "failed",
            Error = exception.Message
        };

    internal static AgentResponseUpdate CreateMetadataUpdate(AgentEvaluationResults result)
        => new(ChatRole.Assistant,
        [
            new DataContent(
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
                    new Dictionary<string, object?>
                    {
                        ["evaluations"] = new Dictionary<string, object?>
                        {
                            ["localEvaluator"] = result
                        }
                    },
                    JsonSerializerOptions.Web)),
                MediaTypeNames.Application.Json)
            {
                Name = FinishMetadataName
            }
        ]);

    private static ToolCalledMode ParseToolCalledMode(string? mode)
        => Enum.TryParse<ToolCalledMode>(mode, ignoreCase: true, out var parsed)
            ? parsed
            : ToolCalledMode.All;
}
