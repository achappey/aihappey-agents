
using AgentHappey.Core;

namespace AgentHappey.AzureAuth;

public class Config
{
    public string? AgentDatabase { get; set; }
    public AzureAd AzureAd { get; set; } = default!;
    public AiConfig AiConfig { get; set; } = default!;
    public McpConfig McpConfig { get; set; } = default!;
}
