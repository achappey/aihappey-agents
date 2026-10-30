using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Net.Http.Headers;
using AIHappey.Responses.Streaming;
using AIHappey.Responses;
using AIHappey.Responses.Diagnostics;

namespace AgentHappey.Core.ChatClient;

public static class HttpExtensions
{
    private static readonly MediaTypeWithQualityHeaderValue AcceptJson = new("application/json");
    private static readonly MediaTypeWithQualityHeaderValue AcceptSse = new("text/event-stream");

    /// <summary>
    /// POST JSON and deserialize JSON response into T (non-stream).
    /// </summary>
    public static async Task<ResponseResult> GetResponses(
        this HttpClient client,
        ResponseRequest options,
        string relativeUrl = "v1/responses",
        JsonElement? extraRootProperties = null,
        IReadOnlyDictionary<string, string>? providerHeaders = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (string.IsNullOrWhiteSpace(relativeUrl)) throw new ArgumentNullException(nameof(relativeUrl));

        using var req = new HttpRequestMessage(HttpMethod.Post, relativeUrl);
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(AcceptJson);
        var payload = BuildPayload(options, extraRootProperties);
        req.Content = new StringContent(payload.GetRawText(), Encoding.UTF8, "application/json");
        ApplyProviderHeaders(req, providerHeaders);
        ApplyRuntimeHeaders(req, options.Headers);
        SuppressUnrelatedDefaults(req, client, options.Model);

        var observer = ResponsesTransportScope.Current;
        var operation = CreateOperation(observer, options.Model, relativeUrl);
        if (observer is not null)
            await observer.OnBodyAsync(operation!, "request", payload.GetRawText(), "application/json", ct);

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        await ThrowIfNotSuccess(resp, ct, observer, operation);

        var body = await resp.Content.ReadAsStringAsync(ct);
        if (observer is not null)
            await observer.OnBodyAsync(operation!, "response", body,
                resp.Content.Headers.ContentType?.MediaType ?? "application/json", ct);

        var result = JsonSerializer.Deserialize<ResponseResult>(body, ResponseJson.Default);

        if (result is null)
            throw new InvalidOperationException($"Empty JSON response for {relativeUrl}.");
      
        return result;
    }

    /// <summary>
    /// POST JSON with stream=true and parse SSE "data: {json}" events into TEvent.
    /// Ends on "data: [DONE]".
    /// </summary>
    public static async IAsyncEnumerable<ResponseStreamPart> GetResponsesUpdates(
        this HttpClient client,
        ResponseRequest options,
        string relativeUrl = "v1/responses",
        JsonElement? extraRootProperties = null,
        IReadOnlyDictionary<string, string>? providerHeaders = null,
        [EnumeratorCancellation] CancellationToken ct = default
)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (string.IsNullOrWhiteSpace(relativeUrl)) throw new ArgumentNullException(nameof(relativeUrl));

        using var req = new HttpRequestMessage(HttpMethod.Post, relativeUrl);

        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(AcceptSse);
        req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        var payload = BuildPayload(options, extraRootProperties);

        req.Content = new StringContent(payload.GetRawText(), Encoding.UTF8, "application/json");
        ApplyProviderHeaders(req, providerHeaders);
        ApplyRuntimeHeaders(req, options.Headers);
        SuppressUnrelatedDefaults(req, client, options.Model);

        var observer = ResponsesTransportScope.Current;
        var operation = CreateOperation(observer, options.Model, relativeUrl);
        if (observer is not null)
            await observer.OnBodyAsync(operation!, "request", payload.GetRawText(), "application/json", ct);

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        await ThrowIfNotSuccess(resp, ct, observer, operation);

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var observed = observer?.ObserveStream(stream, operation!,
            resp.Content.Headers.ContentType?.MediaType ?? "text/event-stream");
        using var reader = new StreamReader(observed ?? stream);

        await foreach (var evt in ReadResponseSseEventsAsync(reader, ct))
            yield return evt;
    }

    internal static void SuppressUnrelatedDefaults(HttpRequestMessage request, HttpClient client, string? modelId)
    {
        foreach (var header in client.DefaultRequestHeaders)
            if (ProviderCredentialHeaders.IsProviderKey(header.Key)
                && !ProviderCredentialHeaders.IsRequired(header.Key, modelId)
                && !request.Headers.Contains(header.Key))
                // A concrete empty value suppresses default copying. An empty
                // collection is ignored by HttpHeaders and would leak the default.
                // No credential is transmitted and the shared client is unchanged.
                request.Headers.TryAddWithoutValidation(header.Key, string.Empty);
    }

    private static void ApplyRuntimeHeaders(HttpRequestMessage request, IReadOnlyDictionary<string, string>? headers)
    {
        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
            if (!request.Headers.Contains(name)) request.Headers.TryAddWithoutValidation(name, value);
    }

    private static void ApplyProviderHeaders(
        HttpRequestMessage request,
        IReadOnlyDictionary<string, string>? providerHeaders)
    {
        if (providerHeaders is null)
            return;

        foreach (var (name, value) in providerHeaders)
        {
            // Headers explicitly set by the runtime own the wire representation.
            // All other headers are request-scoped and override HttpClient defaults.
            if (request.Headers.Any(header => string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase))
                || request.Content?.Headers.Any(header => string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase)) == true
                || string.Equals(name, "X-Agent-Name", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            request.Headers.TryAddWithoutValidation(name, value);
        }
    }


    private static async IAsyncEnumerable<ResponseStreamPart> ReadResponseSseEventsAsync(
        StreamReader reader,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var dataBuilder = new StringBuilder();

        string? line;
        while (!ct.IsCancellationRequested &&
               (line = await reader.ReadLineAsync(ct)) != null)
        {
            if (line.Length == 0)
            {
                if (!TryTakeSseDataEvent(dataBuilder, out var data))
                    continue;

                if (string.Equals(data, "[DONE]", StringComparison.OrdinalIgnoreCase))
                    yield break;

                yield return ParseResponseSseEvent(data);
                continue;
            }

            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            AppendSseDataLine(dataBuilder, line);
        }

        if (TryTakeSseDataEvent(dataBuilder, out var trailingData)
            && !string.Equals(trailingData, "[DONE]", StringComparison.OrdinalIgnoreCase))
        {
            yield return ParseResponseSseEvent(trailingData);
        }
    }

    private static void AppendSseDataLine(StringBuilder builder, string line)
    {
        var data = line["data:".Length..];
        if (data.Length > 0 && data[0] == ' ')
            data = data[1..];

        if (builder.Length > 0)
            builder.Append('\n');

        builder.Append(data);
    }

    private static bool TryTakeSseDataEvent(StringBuilder builder, out string data)
    {
        if (builder.Length == 0)
        {
            data = string.Empty;
            return false;
        }

        data = builder.ToString().Trim();
        builder.Clear();
        return data.Length > 0;
    }

    private static ResponseStreamPart ParseResponseSseEvent(string data)
    {
        ResponseStreamPart? evt;
        try
        {
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            // A gateway failure may use the OpenAI error envelope instead of a
            // typed Responses event. It is a downstream error, not invalid JSON.
            if (!root.TryGetProperty("type", out _)
                && root.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object)
            {
                return new ResponseError
                {
                    Message = error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
                        ? message.GetString()! : "Responses stream failed.",
                    Code = error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String
                        ? code.GetString()! : null!,
                    Param = error.TryGetProperty("param", out var param) && param.ValueKind == JsonValueKind.String
                        ? param.GetString()! : null!
                };
            }

            evt = root.Deserialize<ResponseStreamPart>();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to parse SSE json event: {data}", ex);
        }

        if (evt is null)
            throw new InvalidOperationException($"Parsed SSE event was null: {data}");

        return evt;
    }


    private static ResponsesTransportOperation? CreateOperation(
        IResponsesTransportObserver? observer, string? model, string relativeUrl)
        => observer is null ? null : new(model?.Split('/', 2)[0], relativeUrl, Guid.NewGuid().ToString("n"));

    private static async Task ThrowIfNotSuccess(HttpResponseMessage resp, CancellationToken ct,
        IResponsesTransportObserver? observer, ResponsesTransportOperation? operation)
    {
        if (resp.IsSuccessStatusCode) return;

        var body = resp.Content is null ? "" : await resp.Content.ReadAsStringAsync(ct);
        if (observer is not null)
            await observer.OnBodyAsync(operation!, "response", body,
                resp.Content?.Headers.ContentType?.MediaType ?? "application/json", ct);
        throw new HttpRequestException($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {body}", null, resp.StatusCode);
    }

    private static JsonElement BuildPayload(ResponseRequest options, JsonElement? extraRootProperties)
    {
        var payload = JsonSerializer.SerializeToElement(options, ResponseJson.Default);

        if (extraRootProperties is not JsonElement extra)
            return payload;

        if (extra.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return payload;

        if (extra.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Extra responses root properties must be a JSON object.");

        var merged = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        foreach (var property in payload.EnumerateObject())
            merged[property.Name] = property.Value.Clone();

        foreach (var property in extra.EnumerateObject())
        {
            merged[property.Name] = property.Value.Clone();
        }

        return JsonSerializer.SerializeToElement(merged, ResponseJson.Default);
    }
}



