using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;

namespace AgentHappey.Core;


public class SimpleBearerAuthenticationProvider : IAuthenticationProvider
{
    private readonly string _token;

    public SimpleBearerAuthenticationProvider(string accessToken)
    {
        _token = accessToken;
    }
   
    public Task AuthenticateRequestAsync(RequestInformation request, Dictionary<string, object>? additionalAuthenticationContext = null, CancellationToken cancellationToken = default)
    {
        request.Headers["Authorization"] = [$"Bearer {_token}"];
        return Task.CompletedTask;
    }
}