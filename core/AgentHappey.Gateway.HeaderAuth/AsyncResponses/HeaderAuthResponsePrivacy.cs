using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Responses;
using AgentHappey.AsyncResponses;

namespace AgentHappey.HeaderAuth.AsyncResponses;

/// <summary>
/// Scoped persistence projection, not a general DLP filter. Execution objects are never serialized to storage.
/// Removes auth structures and redacts exact known credential echoes, including JSON-encoded debug strings.
/// Cannot recognize arbitrary secrets supplied as prose or transformed by downstream services.
/// </summary>
public sealed class HeaderAuthResponsePrivacy
{
    private const string Redacted = "[redacted]";
    private readonly HashSet<string> credentials = new(StringComparer.Ordinal);

    public HeaderAuthResponsePrivacy(ResponseRequest request, AsyncResponsesRequestContext context)
    {
        foreach (var value in context.Headers.Values) Remember(value);
        Remember(context.UserAccessToken);
        Collect(JsonSerializer.SerializeToNode(request, ResponseJson.Default));
    }

    private HeaderAuthResponsePrivacy(ResponseResult response)
        => Collect(JsonSerializer.SerializeToNode(response, ResponseJson.Default));

    public static HeaderAuthResponsePrivacy ForResponse(ResponseResult response) => new(response);

    public ResponseResult Sanitize(ResponseResult response)
    {
        var node = JsonSerializer.SerializeToNode(response, ResponseJson.Default)!;
        Collect(node); // Also catches auth returned by tools/providers rather than supplied by the caller.
        Clean(node);
        var safe = node.Deserialize<ResponseResult>(ResponseJson.Default)
            ?? throw new InvalidOperationException("Unable to create a safe background response.");
        // Protocol identity/lifecycle is assigned by our service, not taken from request auth data.
        safe.Id = response.Id;
        safe.Object = response.Object;
        safe.Status = response.Status;
        safe.CreatedAt = response.CreatedAt;
        safe.CompletedAt = response.CompletedAt;
        return safe;
    }

    private static bool IsCredentialField(string name)
    {
        var key = new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return key is "headers" or "providerheaders" or "auth" or "authentication" or "authorization"
            or "proxyauthorization" or "credentials" or "credential" or "password" or "passwd"
            or "secret" or "secrets" or "clientsecret" or "clientsecrets" or "token" or "accesstoken"
            or "refreshtoken" or "idtoken" or "useraccesstoken" or "cookie" or "setcookie"
            or "apikey" or "apikeys" or "accountkey" or "sharedaccesssignature"
            || key.EndsWith("headers", StringComparison.Ordinal)
            || key.EndsWith("apikey", StringComparison.Ordinal)
            || key.EndsWith("authorization", StringComparison.Ordinal);
    }

    // A declaration of an auth-shaped function argument is not itself a credential.
    private static bool IsSchemaDeclaration(JsonNode? node)
        => node is JsonObject obj && obj["type"] is JsonValue type && type.TryGetValue<string>(out var value)
            && value is "string" or "number" or "integer" or "boolean" or "object" or "array" or "null";

    private void Remember(string? value)
    {
        if (string.IsNullOrEmpty(value) || value == Redacted) return;
        credentials.Add(value);
        // A provider may echo only the parameter of an Authorization header.
        if (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            var parameter = value[(value.IndexOf(' ') + 1)..];
            if (parameter.Length > 0) credentials.Add(parameter);
        }
    }

    private void RememberLeaves(JsonNode? node, bool authStructure = false)
    {
        if (node is JsonObject obj)
            foreach (var property in obj)
            {
                // Auth mechanism descriptors are not credential values. Still remove the entire auth object.
                if (authStructure && property.Key is "type" or "scheme" or "method") continue;
                RememberLeaves(property.Value, authStructure);
            }
        else if (node is JsonArray array)
            foreach (var item in array) RememberLeaves(item, authStructure);
        else if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            Remember(text);
            if (TryJson(text, out var embedded)) Collect(embedded);
            CollectUrlCredentials(text);
        }
    }

    private void Collect(JsonNode? node, bool schemaProperties = false)
    {
        if (node is JsonObject obj)
            foreach (var property in obj)
            {
                if (IsCredentialField(property.Key))
                {
                    if (schemaProperties && IsSchemaDeclaration(property.Value))
                    {
                        foreach (var example in ((JsonObject)property.Value!).Where(item => IsSchemaValue(item.Key)))
                            RememberLeaves(example.Value);
                        Collect(property.Value);
                    }
                    else RememberLeaves(property.Value, property.Key.ToLowerInvariant() is "auth" or "authentication" or "credentials");
                }
                else Collect(property.Value, property.Key is "properties" or "$defs" or "definitions");
            }
        else if (node is JsonArray array)
            foreach (var item in array) Collect(item);
        else if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            if (TryJson(text, out var embedded)) Collect(embedded);
            CollectUrlCredentials(text);
        }
    }

    private void Clean(JsonNode? node, bool schemaProperties = false)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToList())
            {
                if (credentials.Any(secret => property.Key.Contains(secret, StringComparison.Ordinal))) obj.Remove(property.Key);
                else if (IsCredentialField(property.Key))
                {
                    if (schemaProperties && IsSchemaDeclaration(property.Value))
                    {
                        var declaration = (JsonObject)property.Value!;
                        foreach (var key in declaration.Select(item => item.Key).Where(IsSchemaValue).ToList()) declaration.Remove(key);
                        Clean(declaration);
                    }
                    else obj.Remove(property.Key);
                }
                else if (property.Value is JsonValue value && value.TryGetValue<string>(out var text))
                    obj[property.Key] = CleanString(text);
                else Clean(property.Value, property.Key is "properties" or "$defs" or "definitions");
            }
        }
        else if (node is JsonArray array)
            for (var i = 0; i < array.Count; i++)
                if (array[i] is JsonValue value && value.TryGetValue<string>(out var text)) array[i] = CleanString(text);
                else Clean(array[i]);
    }

    private static bool IsSchemaValue(string key) => key is "default" or "example" or "examples" or "const" or "enum";

    private string CleanString(string text)
    {
        if (TryJson(text, out var embedded))
        {
            Clean(embedded);
            text = embedded!.ToJsonString(ResponseJson.Default);
        }
        foreach (var credential in credentials.OrderByDescending(value => value.Length))
            text = text.Replace(credential, Redacted, StringComparison.Ordinal);
        return text;
    }

    private void CollectUrlCredentials(string text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            Remember(uri.UserInfo);
            Remember(Uri.UnescapeDataString(uri.UserInfo));
            var separator = uri.UserInfo.IndexOf(':');
            if (separator >= 0) Remember(Uri.UnescapeDataString(uri.UserInfo[(separator + 1)..]));
        }
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            var name = Uri.UnescapeDataString(pair[0]);
            if (pair.Length == 2 && (IsCredentialField(name) || name is "key" or "sig"))
            {
                Remember(pair[1]);
                Remember(Uri.UnescapeDataString(pair[1].Replace('+', ' ')));
            }
        }
    }

    private static bool TryJson(string text, out JsonNode? node)
    {
        node = null;
        text = text.TrimStart();
        if (!(text.StartsWith('{') || text.StartsWith('['))) return false;
        try { node = JsonNode.Parse(text); return node is JsonObject or JsonArray; }
        catch (JsonException) { return false; }
    }
}
