
using AgentHappey.Core;

namespace AgentHappey.HeaderAuth;

public class Config
{
    public string? AgentPluginExtensionNamespace { get; set; }
    public AiConfig AiConfig { get; set; } = default!;
    public McpConfig McpConfig { get; set; } = default!;
    public BlobAgentsConfig? BlobAgents { get; set; }
    public AsyncAgentsConfig? AsyncAgents { get; set; }
}

