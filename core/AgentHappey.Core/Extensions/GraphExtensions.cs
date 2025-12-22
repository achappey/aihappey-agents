using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Graph.Beta;
using Microsoft.Identity.Web;

namespace AgentHappey.Core.Extensions;

public static class GraphExtensions
{
    public static string EncodeSharingUrl(this string url)
    {
        var base64Value = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(url))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        return $"u!{base64Value}";
    }

    public static async Task<GraphServiceClient?> GetOboGraphClientAsync(
        this IServiceProvider services,
        string[] scopes,
        CancellationToken ct = default)
    {
        var http = services.GetRequiredService<IHttpContextAccessor>();
        var tokenAcquisition = services.GetService<ITokenAcquisition>();

        if (tokenAcquisition == null || http.HttpContext?.User == null)
            return null;

        // Get the downstream (OBO) token for Graph
        var token = await tokenAcquisition.GetAccessTokenForUserAsync(
            scopes: scopes,
            user: http.HttpContext.User);

        // Wrap it
        var authProvider = new SimpleBearerAuthenticationProvider(
            token
        );

        // Build the GraphServiceClient with that token
        return new GraphServiceClient(authProvider);
    }
}
