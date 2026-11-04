using System.Net;
using System.Text.Json;

namespace AgentHappey.Core.ChatClient;

/// <summary>
/// Extracts display text from downstream failures. Raw bodies and exceptions belong
/// in transport diagnostics, never in a UI error message.
/// </summary>
public static class ProviderErrorMessages
{
    public const string DefaultMessage = "The model request failed. Please try again.";
    public const string ResponseBodyKey = "DownstreamResponseBody";

    public static string FromJson(JsonElement error, string fallback = DefaultMessage)
        => ReadMessage(error, 0) ?? fallback;

    public static string FromText(string? message, string fallback = DefaultMessage)
        => ReadText(message, 0) ?? fallback;

    public static string FromHttpResponse(string? body, HttpStatusCode statusCode, string? reasonPhrase)
        => FromText(body, $"The model request failed (HTTP {(int)statusCode}{(string.IsNullOrWhiteSpace(reasonPhrase) ? "" : " " + reasonPhrase)}).");

    public static string FromException(Exception exception)
    {
        if (exception is HttpRequestException http)
        {
            // Also accept the legacy transport exception format at output boundaries.
            if (http.Message.StartsWith("HTTP ", StringComparison.Ordinal)
                && http.Message.IndexOf(':') is var separator && separator >= 0)
                return FromHttpResponse(http.Message[(separator + 1)..],
                    http.StatusCode ?? HttpStatusCode.InternalServerError, null);

            return FromText(http.Message);
        }

        if (exception is JsonException || exception.InnerException is JsonException)
            return "The model response could not be read. Please try again.";

        if (exception is AggregateException aggregate && aggregate.InnerExceptions.Count == 1)
            return FromException(aggregate.InnerExceptions[0]);

        // Agent/workflow wrappers often replace a useful downstream error with a
        // generic execution failure. Keep the original exception intact for logs.
        if (exception.InnerException is { } inner)
            return FromException(inner);

        return FromText(exception.Message);
    }

    private static string? ReadMessage(JsonElement value, int depth)
    {
        if (depth > 8)
            return null;

        if (value.ValueKind == JsonValueKind.String)
            return ReadText(value.GetString(), depth + 1);

        if (value.ValueKind != JsonValueKind.Object)
            return null;

        // Some gateways send type:error with message:null and error:{message:...}.
        // Only traverse known error envelopes, not arbitrary response output/data.
        foreach (var name in new[] { "error", "response", "message", "detail" })
            if (value.TryGetProperty(name, out var property)
                && ReadMessage(property, depth + 1) is { } message)
                return message;

        return null;
    }

    private static string? ReadText(string? text, int depth)
    {
        if (depth > 8 || string.IsNullOrWhiteSpace(text))
            return null;

        text = text.Trim();
        if (text.StartsWith('<')) // Do not expose an HTML proxy/server error page.
            return null;

        if (text[0] is '{' or '[' or '"')
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                return ReadMessage(document.RootElement, depth + 1);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        return text;
    }
}
