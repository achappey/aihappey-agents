using System.IdentityModel.Tokens.Jwt;
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
      var azureAd = services.GetRequiredService<AzureAd>();

      if (azureAd is null || context.HttpContext?.User is null || mcpConfig is null)
         return null;

      return await httpClientFactory.GetMcpTokenAsync(serverUrl,
                        context.HttpContext?.Request.Headers.Authorization.FirstOrDefault()?.Split(" ").LastOrDefault()!,
                        azureAd, mcpConfig, ct);
   }

   public static async Task<string?> GetMcpTokenAsync(this IHttpClientFactory httpClientFactory, string serverUrl, string userAccessToken,
      AzureAd azureAd,
      McpConfig mcpConfig,
      CancellationToken ct = default)
   {
      HttpClient client = httpClientFactory.CreateClient();

      if (!new Uri(serverUrl).Host.Contains(new Uri(mcpConfig.McpBaseUrl).Host,
         StringComparison.OrdinalIgnoreCase))
         return null;

      if (string.IsNullOrEmpty(userAccessToken))
         throw new InvalidOperationException("No access token found in request.");

      var cca = ConfidentialClientApplicationBuilder
         .Create(azureAd.ClientId)
         .WithClientSecret(azureAd.ClientSecret)
         .WithAuthority($"https://login.microsoftonline.com/{azureAd.TenantId}")
         .Build();

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
}
