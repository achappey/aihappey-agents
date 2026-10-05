namespace AgentHappey.HeaderAuth;

/// <summary>Explicit host policies. Defaults preserve the existing hosted API.</summary>
public sealed class HeaderAuthHostOptions
{
    public bool PersistForegroundResponses { get; set; }
    public bool ListStoredResponses { get; set; }
    public bool PortableMcpOnly { get; set; }
}
