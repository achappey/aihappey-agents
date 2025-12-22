
namespace AgentHappey.Core;

public class AiConfig
{
    public string AiEndpoint { get; set; } = null!;
    public string? AiScopes { get; set; }
}

public class AzureAd
{
    public string Instance { get; set; } = null!;
    public string TenantId { get; set; } = null!;
    public string ClientId { get; set; } = null!;
    public string ClientSecret { get; set; } = null!;
    public string Audience { get; set; } = null!;
}

public class McpConfig
{
    public string McpBaseUrl { get; set; } = null!;
    
    public string? Scopes { get; set; }

    public string? LightIcon { get; set; }

    public string? DarkIcon { get; set; }
}