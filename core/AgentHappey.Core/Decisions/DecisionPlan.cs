using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.AI;

namespace AgentHappey.Core.DecisionRuntime;

internal sealed record DecisionQuestionPlan(string Key, string? Name, string Type, int OptionCount, JsonElement Evidence);

internal sealed record DecisionPlan(string Model, IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<DecisionQuestionPlan> Questions, int? MaxOutputTokens = null, string? SafetyIdentifier = null)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerOptions.Web);

    public static DecisionPlan Create(OpenAIDecisionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireModel(request.Model);
        ArgumentNullException.ThrowIfNull(request.Input);
        if (request.AdditionalProperties is { Count: > 0 })
            throw new NotSupportedException("Additional OpenAI decision request fields are not supported by agents.");
        RequireQuestions(request.Questions?.Count ?? 0);
        var questions = request.Questions!.Select((question, index) =>
        {
            if (question is null || string.IsNullOrWhiteSpace(question.Instructions))
                throw new ArgumentException("Each decision question requires instructions.");
            var count = question switch
            {
                OpenAIDecisionPredicateQuestion => 0,
                OpenAIDecisionChoiceQuestion choice => ValidateChoices(choice),
                OpenAIDecisionScoreQuestion score => ValidateLevels(score),
                _ => throw new NotSupportedException("Unsupported OpenAI decision question.")
            };
            return new DecisionQuestionPlan(Key(index), question.Name, question.Type, count,
                JsonSerializer.SerializeToElement(question, Json));
        }).ToArray();
        var messages = new List<ChatMessage>();
        if (request.Input.Text is { } text) messages.Add(new(ChatRole.User, text));
        else
        {
            if (request.Input.Messages is not { Count: > 0 }) throw new ArgumentException("Input must contain user messages.");
            var imageCount = 0;
            foreach (var message in request.Input.Messages)
            {
                if (message?.Content is null) throw new ArgumentException("Input messages require content.");
                var parts = new List<AIContent>();
                if (message.Content.Text is { } content) parts.Add(new TextContent(content));
                else
                    foreach (var part in message.Content.Parts ?? [])
                    {
                        switch (part)
                        {
                            case OpenAIDecisionInputText inputText when inputText.Text is not null:
                                parts.Add(new TextContent(inputText.Text)); break;
                            case OpenAIDecisionInputImage image:
                                if (++imageCount > 128) throw new ArgumentException("At most 128 inline images are supported.");
                                parts.Add(InlineImage(image.ImageUrl, image.Detail)); break;
                            default: throw new ArgumentException("Unsupported or invalid input part.");
                        }
                    }
                if (parts.Count == 0) throw new ArgumentException("Input messages must not be empty.");
                messages.Add(new(ChatRole.User, parts));
            }
        }
        return new(request.Model, AddQuestions(messages, questions), questions, SafetyIdentifier: request.SafetyIdentifier);
    }

    public static DecisionPlan Create(DecisionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireModel(request.Model);
        ArgumentNullException.ThrowIfNull(request.State);
        RequireQuestions(request.Questions?.Count ?? 0);
        if (request.StateFormat is not (null or "sdk")) throw new ArgumentException("stateFormat must be omitted or 'sdk'.");
        if (request.MaxOutputTokens is <= 0) throw new ArgumentException("maxOutputTokens must be positive.");
        if (request.Reasoning is not null) throw new NotSupportedException("Agent decisions do not support request-level reasoning settings.");
        if (request.ProviderOptions is { Count: > 0 }) throw new NotSupportedException("Agent decisions use the agent's configured provider options.");
        if (request.Headers is { Count: > 0 }) throw new NotSupportedException("Use HTTP headers for agent credentials; body headers are not supported.");
        var questions = request.Questions!.Select((entry, index) =>
        {
            if (string.IsNullOrWhiteSpace(entry.Key) || entry.Value is null)
                throw new ArgumentException("Each question requires a nonempty ID and a question.");
            var count = entry.Value switch
            {
                DecisionBooleanQuestion boolean when boolean.Instructions is not null || boolean.Criteria?.True is not null || boolean.Criteria?.False is not null => 0,
                DecisionChoiceQuestion choice when choice.Criteria is { Count: > 0 } && choice.Criteria.Keys.All(k => !string.IsNullOrWhiteSpace(k)) => choice.Criteria.Count,
                DecisionScoreQuestion score when score.Criteria is { Count: > 0 } => score.Criteria.Count,
                DecisionBoxQuestion box when box.Instructions is not null => 0,
                _ => throw new ArgumentException("Questions require valid instructions or criteria; box questions require instructions.")
            };
            return new DecisionQuestionPlan(Key(index), entry.Key,
                entry.Value.Type == "boolean" ? "predicate" : entry.Value.Type, count,
                JsonSerializer.SerializeToElement(entry.Value, Json));
        }).ToArray();
        List<AIContent> state = request.StateFormat == "sdk"
            ? ReadSdkState(request.State.Value)
            : [new TextContent(request.State.Value.ValueKind == JsonValueKind.String
                ? request.State.Value.GetString()! : request.State.Value.GetRawText())];
        return new(request.Model, AddQuestions([new(ChatRole.User, state)], questions), questions, request.MaxOutputTokens);
    }

    private static IReadOnlyList<ChatMessage> AddQuestions(List<ChatMessage> messages, IReadOnlyList<DecisionQuestionPlan> questions)
    {
        messages.Add(new(ChatRole.User, string.Join("\n\n", questions.Select(FormatQuestion))));
        return messages;
    }

    private static string FormatQuestion(DecisionQuestionPlan question)
    {
        var text = new StringBuilder($"Question {question.Key} ({question.Type})\n");
        if (question.Name is not null)
            AppendValue(text, "Name", JsonSerializer.SerializeToElement(question.Name), 0);
        foreach (var field in question.Evidence.EnumerateObject())
        {
            if (field.Name is "type" or "name") continue;
            var label = char.ToUpperInvariant(field.Name[0]) + field.Name[1..];
            if (field.Name == "criteria" && question.Type == "choice" && field.Value.ValueKind == JsonValueKind.Object)
            {
                text.AppendLine("Criteria:");
                var index = 0;
                foreach (var criterion in field.Value.EnumerateObject())
                    AppendValue(text, $"{Index(index++)} ({criterion.Name})", criterion.Value, 2);
            }
            else AppendValue(text, label, field.Value, 0);
        }
        return text.ToString().TrimEnd();
    }

    private static void AppendValue(StringBuilder text, string label, JsonElement value, int indent)
    {
        var padding = new string(' ', indent);
        text.Append(padding).Append(label).Append(':');
        if (value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Any())
        {
            text.AppendLine();
            foreach (var property in value.EnumerateObject())
                AppendValue(text, property.Name, property.Value, indent + 2);
        }
        else if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0)
        {
            text.AppendLine();
            var index = 0;
            foreach (var item in value.EnumerateArray()) AppendValue(text, Index(index++), item, indent + 2);
        }
        else
        {
            var display = value.ValueKind switch
            {
                JsonValueKind.String when label == "value" => $"\"{value.GetString()}\" (string)",
                JsonValueKind.String => value.GetString() is { Length: > 0 } content ? content : "(empty string)",
                JsonValueKind.True => "true (boolean)",
                JsonValueKind.False => "false (boolean)",
                JsonValueKind.Null => "(null)",
                JsonValueKind.Object => "(empty object)",
                JsonValueKind.Array => "(empty array)",
                _ => value.GetRawText()
            };
            text.Append(' ').AppendLine(display.Replace("\n", "\n" + padding + "  ", StringComparison.Ordinal));
        }
    }

    private static int ValidateChoices(OpenAIDecisionChoiceQuestion question)
    {
        if (question.Choices is not { Count: > 0 }) throw new ArgumentException("Choice questions require choices.");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var choice in question.Choices)
        {
            if (choice is null || choice.Value.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.True or JsonValueKind.False)
                || !keys.Add(choice.Value.Value.GetRawText()))
                throw new ArgumentException("Choices must contain unique string or boolean values.");
        }
        return question.Choices.Count;
    }

    private static int ValidateLevels(OpenAIDecisionScoreQuestion question)
    {
        if (question.Levels is not { Count: > 0 } || question.Levels.Any(l => string.IsNullOrWhiteSpace(l?.Label))
            || question.Levels.Select(l => l.Label).Distinct(StringComparer.Ordinal).Count() != question.Levels.Count)
            throw new ArgumentException("Score questions require uniquely labeled levels.");
        return question.Levels.Count;
    }

    private static void RequireModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("model must identify a catalog agent.");
    }

    private static void RequireQuestions(int count)
    {
        if (count == 0) throw new ArgumentException("At least one decision question is required.");
    }

    internal static string Key(int index) => "q" + index.ToString(CultureInfo.InvariantCulture);
    internal static string Index(int index) => index.ToString(CultureInfo.InvariantCulture);

    private static DataContent InlineImage(string url, string? detail = null)
    {
        if (detail is not (null or "auto" or "low" or "high" or "original")) throw new ArgumentException("Invalid image detail.");
        if (url is null || !url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Decision images must be inline data URLs, not remote URLs or file IDs.");
        var separator = url.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
        if (separator < 0) throw new ArgumentException("Decision images require base64 data.");
        try
        {
            if (Convert.FromBase64String(url[(separator + 8)..]).Length == 0) throw new FormatException();
        }
        catch (FormatException ex) { throw new ArgumentException("Invalid base64 image data.", ex); }
        return new(url, url[5..separator])
        {
            RawRepresentation = new AIHappey.Responses.InputImagePart { ImageUrl = url, Detail = detail }
        };
    }

    private static List<AIContent> ReadSdkState(JsonElement state)
    {
        if (state.ValueKind != JsonValueKind.Array || state.GetArrayLength() == 0)
            throw new ArgumentException("SDK state must be a nonempty array of typed parts.");
        var content = new List<AIContent>();
        var imageCount = 0;
        foreach (var part in state.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object) throw new ArgumentException("SDK state requires typed parts.");
            if (part.TryGetProperty("providerOptions", out var options) && options.ValueKind != JsonValueKind.Null
                && (options.ValueKind != JsonValueKind.Object || options.EnumerateObject().Any()))
                throw new NotSupportedException("State-part provider options are not supported.");
            switch (RequiredString(part, "type"))
            {
                case "text":
                    RequireOnly(part, "type", "text", "providerOptions");
                    content.Add(new TextContent(RequiredString(part, "text"))); break;
                case "json":
                    RequireOnly(part, "type", "value");
                    if (!part.TryGetProperty("value", out var value)) throw new ArgumentException("JSON parts require a value.");
                    content.Add(new TextContent(value.GetRawText())); break;
                case "file":
                    RequireOnly(part, "type", "data", "mediaType", "filename", "providerOptions");
                    var media = RequiredString(part, "mediaType");
                    if (part.TryGetProperty("filename", out _)) content.Add(new TextContent("File: " + RequiredString(part, "filename")));
                    if (!part.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                        throw new ArgumentException("File parts require tagged data.");
                    switch (RequiredString(data, "type"))
                    {
                        case "text" when media.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || media is "text" or "application/json":
                            RequireOnly(data, "type", "text");
                            content.Add(new TextContent(RequiredString(data, "text"))); break;
                        case "data" when media.StartsWith("image/", StringComparison.OrdinalIgnoreCase):
                            RequireOnly(data, "type", "data");
                            if (++imageCount > 128) throw new ArgumentException("At most 128 inline images are supported.");
                            content.Add(InlineImage($"data:{media};base64,{RequiredString(data, "data")}")); break;
                        case "url" when media.StartsWith("image/", StringComparison.OrdinalIgnoreCase):
                            RequireOnly(data, "type", "url", "originalUrl");
                            var url = RequiredString(data, "url");
                            if (data.TryGetProperty("originalUrl", out _)) url = RequiredString(data, "originalUrl");
                            if (!url.StartsWith($"data:{media};base64,", StringComparison.OrdinalIgnoreCase))
                                throw new NotSupportedException("Only matching inline image URLs are supported.");
                            if (++imageCount > 128) throw new ArgumentException("At most 128 inline images are supported.");
                            content.Add(InlineImage(url)); break;
                        default: throw new NotSupportedException("Unsupported SDK file media type or data source.");
                    }
                    break;
                default: throw new NotSupportedException("Unsupported SDK decision state part.");
            }
        }
        return content;
    }

    private static string RequiredString(JsonElement value, string name)
        => value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String
            ? field.GetString()! : throw new ArgumentException($"SDK part requires string '{name}'.");

    private static void RequireOnly(JsonElement value, params string[] fields)
    {
        if (value.EnumerateObject().Any(p => !fields.Contains(p.Name, StringComparer.Ordinal)))
            throw new NotSupportedException("Unrecognized SDK state fields cannot be mapped without loss.");
    }

    public JsonElement CreateSchema()
    {
        var properties = new JsonObject();
        foreach (var question in Questions)
        {
            var fields = new JsonObject { ["type"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(question.Type) } };
            if (question.Type == "predicate") fields["probability"] = ProbabilitySchema();
            else if (question.Type == "box")
                fields["box"] = ObjectSchema(new JsonObject
                {
                    ["top"] = ProbabilitySchema(),
                    ["bottom"] = ProbabilitySchema(),
                    ["left"] = ProbabilitySchema(),
                    ["right"] = ProbabilitySchema()
                });
            else
            {
                var distribution = new JsonObject();
                for (var i = 0; i < question.OptionCount; i++) distribution[Index(i)] = ProbabilitySchema();
                fields["probabilities"] = ObjectSchema(distribution);
            }
            properties[question.Key] = new JsonObject
            {
                ["anyOf"] = new JsonArray(ObjectSchema(fields), ObjectSchema(new JsonObject
                {
                    ["type"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("refusal") }
                }))
            };
        }
        return JsonSerializer.SerializeToElement(ObjectSchema(new JsonObject { ["answers"] = ObjectSchema(properties) }));
    }

    private static JsonObject ProbabilitySchema() => new() { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1 };
    private static JsonObject ObjectSchema(JsonObject properties) => new()
    {
        ["type"] = "object",
        ["properties"] = properties,
        ["required"] = new JsonArray(properties.Select(p => (JsonNode?)JsonValue.Create(p.Key)).ToArray()),
        ["additionalProperties"] = false
    };
}
