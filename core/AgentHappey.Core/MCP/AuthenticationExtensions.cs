using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Identity.Client;

namespace AgentHappey.Core.MCP;

public static class AuthenticationExtensions
{
   public static async Task<string?> GetMcpTokenAsync(this IServiceProvider services, string serverUrl,
        CancellationToken ct = default)
   {
      var context = services.GetRequiredService<IHttpContextAccessor>();
      var httpClientFactory = services.GetRequiredService<IHttpClientFactory>();
      var mcpConfig = services.GetRequiredService<McpConfig>();
      var conversationsConfig = services.GetService<ConversationsConfig>();
      var azureAd = services.GetRequiredService<AzureAd>();

      if (azureAd is null || context.HttpContext is null || mcpConfig is null)
         return null;

      var request = context.HttpContext.Request;
      var authorizationHeader = request.Headers.Authorization.FirstOrDefault();
      var userAccessToken = TryGetBearerToken(authorizationHeader);

      if (Uri.TryCreate(serverUrl, UriKind.Absolute, out var serverUri)
         && TryGetRequestOrigin(request, out var requestOrigin)
         && IsSameOrigin(serverUri, requestOrigin))
      {
         return userAccessToken;
      }

      return await httpClientFactory.GetMcpTokenAsync(serverUrl,
                        userAccessToken!,
                        azureAd, mcpConfig, conversationsConfig, ct);
   }

   public static async Task<string?> GetMcpTokenAsync(this IHttpClientFactory httpClientFactory, string serverUrl, string userAccessToken,
      AzureAd azureAd,
      McpConfig mcpConfig,
      CancellationToken ct = default)
      => await httpClientFactory.GetMcpTokenAsync(
         serverUrl,
         userAccessToken,
         azureAd,
         mcpConfig,
         conversationsConfig: null,
         ct);

   public static async Task<string?> GetMcpTokenAsync(this IHttpClientFactory httpClientFactory, string serverUrl, string userAccessToken,
      AzureAd azureAd,
      McpConfig mcpConfig,
      ConversationsConfig? conversationsConfig,
      CancellationToken ct = default)
   {
      if (IsConversationMcpServer(serverUrl, conversationsConfig))
      {
         if (string.IsNullOrEmpty(userAccessToken))
            throw new InvalidOperationException("No access token found in request.");

         if (string.IsNullOrWhiteSpace(conversationsConfig!.Scopes))
            throw new InvalidOperationException("ConversationsConfig:Scopes is required for conversation MCP OBO.");

         var conversationApp = CreateConfidentialClient(azureAd);
         var conversationToken = await conversationApp.AcquireTokenOnBehalfOf(
               [conversationsConfig.Scopes],
               new UserAssertion(userAccessToken))
            .ExecuteAsync(ct);

         return conversationToken.AccessToken;
      }

      HttpClient client = httpClientFactory.CreateClient();

      if (!new Uri(serverUrl).Host.Contains(new Uri(mcpConfig.McpBaseUrl).Host,
         StringComparison.OrdinalIgnoreCase))
         return null;

      if (string.IsNullOrEmpty(userAccessToken))
         throw new InvalidOperationException("No access token found in request.");

      var cca = CreateConfidentialClient(azureAd);

      /* --- 1.  Discover protected-resource metadata --------------- */
      var baseUri = new Uri(serverUrl);
      var prmUrl = $"{baseUri.Scheme}://{baseUri.Host}:{baseUri.Port}/" +
                    $".well-known/oauth-protected-resource{baseUri.AbsolutePath}";

      //  var _http = httpClientFactory.CreateClient();
      using var prm = await client.GetAsync(prmUrl, ct);
      prm.EnsureSuccessStatusCode();

      using var prmDoc = JsonDocument.Parse(await prm.Content.ReadAsStreamAsync(ct));
      string resource = prmDoc.RootElement.GetProperty("resource").GetString()!;
      string scopes = string.Join(' ',
                           prmDoc.RootElement.GetProperty("scopes_supported")
                                 .EnumerateArray()
                                 .Select(e => e.GetString()!));

      /* ---------- 2. CHECK CACHE ---------- */
      string cacheKey = McpTokenCacheKey.Make(resource, scopes, userAccessToken);

      if (McpTokenCache.TryGet(cacheKey, out var cached))
         return cached;

      /* --- 3.  Discover *authorization-server* token endpoint ----- */
      var authMetaUrl = prmDoc.RootElement
                              .GetProperty("authorization_servers")[0]
                              .GetString()!;              // could already be …/.well-known/openid-configuration

      var asMeta = await client.GetFromJsonAsync<JsonDocument>(authMetaUrl, ct);
      string tokenEndpoint = asMeta!.RootElement.GetProperty("token_endpoint").GetString()!;

      var mcpTokenForMcp = await cca.AcquireTokenOnBehalfOf(
              [mcpConfig.Scopes],      // aud = MCP
              new UserAssertion(userAccessToken))        // token-A
          .ExecuteAsync();


      /* --- 4.  RFC 8693 token-exchange ---------------------------- */
      var form = new Dictionary<string, string?>
      {
         ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
         ["subject_token_type"] = "urn:ietf:params:oauth:token-type:access_token",
         ["subject_token"] = mcpTokenForMcp.AccessToken,   // token-B
         ["act_token"] = userAccessToken,               // token-A
         ["resource"] = resource,      // cf. RFC 8707
         ["scope"] = scopes,
         ["client_id"] = azureAd.ClientId
      };

      if (!string.IsNullOrEmpty(azureAd.ClientSecret))
         form["client_secret"] = azureAd.ClientSecret;

      using var res = await client.PostAsync(tokenEndpoint,
                                            new FormUrlEncodedContent(form), ct);
      var body = await res.Content.ReadAsStringAsync(ct);
      if (!res.IsSuccessStatusCode)
         throw new HttpRequestException($"Token-exchange failed → {body}");

      var access = JsonDocument.Parse(body).RootElement.GetProperty("access_token").GetString()!;

      var jwt = new JwtSecurityTokenHandler().ReadJwtToken(access);
      // default: 30-minute max
      var hardMax = DateTime.UtcNow.AddMinutes(30);
      // real expiry from JWT minus 60 seconds
      var realExp = jwt.ValidTo - TimeSpan.FromSeconds(60);
      var expires = realExp < hardMax ? realExp : hardMax;

      McpTokenCache.Set(cacheKey, access, expires);

      return access;
   }

   private static IConfidentialClientApplication CreateConfidentialClient(AzureAd azureAd)
      => ConfidentialClientApplicationBuilder
         .Create(azureAd.ClientId)
         .WithClientSecret(azureAd.ClientSecret)
         .WithAuthority($"https://login.microsoftonline.com/{azureAd.TenantId}")
         .Build();

   private static bool IsConversationMcpServer(string serverUrl, ConversationsConfig? conversationsConfig)
      => conversationsConfig is not null
         && Uri.TryCreate(serverUrl, UriKind.Absolute, out var serverUri)
         && Uri.TryCreate(conversationsConfig.McpBaseUrl, UriKind.Absolute, out var conversationsUri)
         && IsSameOrigin(serverUri, conversationsUri);

   private static bool IsSameOrigin(Uri left, Uri right)
      => string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
         && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)
         && left.Port == right.Port;

   private static bool TryGetRequestOrigin(HttpRequest request, out Uri origin)
      => Uri.TryCreate($"{request.Scheme}://{request.Host}", UriKind.Absolute, out origin!);

   private static string? TryGetBearerToken(string? authorizationHeader)
   {
      if (!AuthenticationHeaderValue.TryParse(authorizationHeader, out var authorization)
         || !string.Equals(authorization.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
         || string.IsNullOrWhiteSpace(authorization.Parameter))
      {
         return null;
      }

      return authorization.Parameter;
   }
}
