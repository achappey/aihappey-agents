using System.Text.Json;
using AIHappey.Core.Models;
using AIHappey.Responses;
using AIHappey.Vercel.Models;

namespace AgentHappey.Core.DecisionRuntime;

internal static class DecisionAnswerMapping
{
    internal static IReadOnlyList<JsonElement> Read(ResponseResult response, IReadOnlyList<DecisionQuestionPlan> questions)
    {
        var texts = new List<string>();
        var refusal = false;
        foreach (var item in response.Output)
        {
            var json = JsonSerializer.SerializeToElement(item, ResponseJson.Default);
            var type = json.TryGetProperty("type", out var kind) ? kind.GetString() : null;
            if (type == "reasoning") continue;
            // Completed hosted tools may accompany the answer in the same provider response.
            // Actionable function/custom calls must have been resolved by the agent tool loop.
            if (type is "web_search_call" or "file_search_call" or "code_interpreter_call" or "image_generation_call"
                or "mcp_call" or "mcp_list_tools" or "tool_search_output" or "program_output"
                && json.TryGetProperty("status", out var toolStatus) && toolStatus.GetString() == "completed") continue;
            if (type != "message") throw new InvalidOperationException("Decision execution returned an unresolved or unsupported output item.");
            if (json.TryGetProperty("status", out var status) && status.GetString() is not (null or "completed"))
                throw new InvalidOperationException("Decision execution returned an incomplete message.");
            if (!json.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Decision execution returned invalid content.");
            foreach (var part in content.EnumerateArray())
                switch (part.GetProperty("type").GetString())
                {
                    case "refusal": refusal = true; break;
                    case "output_text": texts.Add(part.GetProperty("text").GetString()!); break;
                    default: throw new InvalidOperationException("Decision execution returned unsupported content.");
                }
        }
        if (refusal)
            return questions.Select(_ => JsonSerializer.SerializeToElement(new { type = "refusal" })).ToArray();
        var text = texts.Count > 0 ? string.Concat(texts) : response.OutputText;
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Decision execution returned no answer payload.");
        using var document = JsonDocument.Parse(text);
        UniqueProperties(document.RootElement);
        RequireFields(document.RootElement, "answers");
        var answers = document.RootElement.GetProperty("answers");
        RequireFields(answers, questions.Select(q => q.Key).ToArray());
        return questions.Select(question =>
        {
            var answer = answers.GetProperty(question.Key);
            if (!answer.TryGetProperty("type", out var type)) throw new InvalidOperationException("Decision answer has no type.");
            if (type.GetString() == "refusal") RequireFields(answer, "type");
            else
            {
                if (type.GetString() != question.Type) throw new InvalidOperationException("Decision answer type does not match its question.");
                switch (question.Type)
                {
                    case "predicate": RequireFields(answer, "type", "probability"); Probability(answer.GetProperty("probability")); break;
                    case "box":
                        RequireFields(answer, "type", "box");
                        var box = answer.GetProperty("box");
                        RequireFields(box, "top", "bottom", "left", "right");
                        foreach (var field in box.EnumerateObject()) Probability(field.Value);
                        if (box.GetProperty("top").GetDouble() > box.GetProperty("bottom").GetDouble()
                            || box.GetProperty("left").GetDouble() > box.GetProperty("right").GetDouble())
                            throw new InvalidOperationException("Decision box edges are inverted.");
                        break;
                    default:
                        RequireFields(answer, "type", "probabilities");
                        var distribution = answer.GetProperty("probabilities");
                        RequireFields(distribution, Enumerable.Range(0, question.OptionCount).Select(DecisionPlan.Index).ToArray());
                        var sum = distribution.EnumerateObject().Sum(p => Probability(p.Value));
                        if (Math.Abs(sum - 1) > 0.000001) throw new InvalidOperationException("Decision probabilities must sum to one.");
                        break;
                }
            }
            return answer.Clone();
        }).ToArray();
    }

    private static double Probability(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number)
            || !double.IsFinite(number) || number is < 0 or > 1)
            throw new InvalidOperationException("Decision probabilities and coordinates must be finite numbers in [0,1].");
        return number;
    }

    private static void RequireFields(JsonElement value, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != fields.Length
            || fields.Any(field => !value.TryGetProperty(field, out _)))
            throw new InvalidOperationException("Decision output does not match the required answer schema.");
    }

    private static void UniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!keys.Add(property.Name)) throw new InvalidOperationException("Decision output contains duplicate keys.");
                UniqueProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) UniqueProperties(item);
    }

    private static double[] Distribution(JsonElement answer)
        => Enumerable.Range(0, answer.GetProperty("probabilities").EnumerateObject().Count())
            .Select(index => answer.GetProperty("probabilities").GetProperty(DecisionPlan.Index(index)).GetDouble()).ToArray();

    internal static OpenAIDecisionAnswer ToOpenAI(OpenAIDecisionQuestion question, JsonElement answer)
    {
        OpenAIDecisionAnswer result;
        if (answer.GetProperty("type").GetString() == "refusal") result = new OpenAIDecisionRefusalAnswer();
        else if (question is OpenAIDecisionPredicateQuestion)
            result = new OpenAIDecisionPredicateAnswer { Probability = answer.GetProperty("probability").GetDouble() };
        else
        {
            var probabilities = Distribution(answer);
            var maximum = probabilities.Max();
            if (question is OpenAIDecisionChoiceQuestion choice)
                result = new OpenAIDecisionChoiceAnswer
                {
                    Choice = choice.Choices[Array.IndexOf(probabilities, maximum)].Value,
                    Confidence = maximum,
                    Probabilities = probabilities.Select((p, index) => new OpenAIDecisionChoiceProbability
                        { Value = choice.Choices[index].Value, Probability = p }).ToArray()
                };
            else
            {
                var score = (OpenAIDecisionScoreQuestion)question;
                // Native contract uses a normalized scale; Vercel uses zero-based positions.
                double Value(int index) => probabilities.Length == 1 ? 0 : (double)index / (probabilities.Length - 1);
                result = new OpenAIDecisionScoreAnswer
                {
                    Score = probabilities.Select((p, index) => p * Value(index)).Sum(),
                    Confidence = maximum,
                    Probabilities = probabilities.Select((p, index) => new OpenAIDecisionScoreProbability
                        { Label = score.Levels[index].Label, Value = Value(index), Probability = p }).ToArray()
                };
            }
        }
        result.Name = question.Name;
        return result;
    }

    internal static DecisionAnswer ToVercel(DecisionQuestion question, JsonElement answer)
    {
        if (answer.GetProperty("type").GetString() == "refusal") return new DecisionRefusalAnswer();
        if (question is DecisionBooleanQuestion) return new DecisionBooleanAnswer { Probability = answer.GetProperty("probability").GetDouble() };
        if (question is DecisionBoxQuestion) return new DecisionBoxAnswer { Box = answer.GetProperty("box").Deserialize<DecisionBox>(DecisionPlan.Json)! };
        var probabilities = Distribution(answer);
        if (question is DecisionChoiceQuestion choice)
        {
            var keys = choice.Criteria.Keys.ToArray();
            return new DecisionChoiceAnswer
            {
                Choice = keys[Array.IndexOf(probabilities, probabilities.Max())],
                Probabilities = keys.Select((key, index) => (key, p: probabilities[index])).ToDictionary(e => e.key, e => e.p, StringComparer.Ordinal)
            };
        }
        return new DecisionScoreAnswer
        {
            Score = probabilities.Select((p, index) => p * index).Sum(),
            Probabilities = probabilities.Select((p, index) => (key: DecisionPlan.Index(index), p)).ToDictionary(e => e.key, e => e.p, StringComparer.Ordinal)
        };
    }
}
