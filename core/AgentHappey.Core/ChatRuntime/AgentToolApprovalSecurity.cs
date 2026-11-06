using System.Security.Cryptography;
using System.Text;
using AIHappey.Vercel.Security;
using Microsoft.Extensions.Options;

namespace AgentHappey.Core.ChatRuntime;

/// <summary>Bound independently from this application's EndUserIdHashing section.</summary>
public sealed class AgentToolApprovalOptions
{
    public string? SecretSalt { get; set; }
}

/// <summary>Chat-only host adapter. Never participates in Responses or background runs.</summary>
public sealed class AgentToolApprovalSecurity(IOptions<AgentToolApprovalOptions> options)
{
    public ToolApprovalSigner? ForHeaderAuth()
        => string.IsNullOrWhiteSpace(options.Value.SecretSalt)
            ? null : new ToolApprovalSigner(options.Value.SecretSalt);

    public ToolApprovalSigner? ForUser(string? objectId)
    {
        var secret = options.Value.SecretSalt?.Trim();
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(objectId)) return null;

        // Match the gateway's AzureEndUserIdResolver + EndUserIdHasher exactly.
        // Never trust a user ID supplied in the chat request body or headers.
        var user = Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(objectId.Trim()))).ToLowerInvariant();
        return new ToolApprovalSigner(secret, user);
    }
}
