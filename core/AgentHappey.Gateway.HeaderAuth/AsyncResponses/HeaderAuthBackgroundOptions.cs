namespace AgentHappey.HeaderAuth.AsyncResponses;

/// <summary>Limits for process-local execution, not response retention.</summary>
public sealed class HeaderAuthBackgroundOptions
{
    public int Capacity { get; set; } = 64;
    // Includes time waiting in the local queue. Credentials are never replayed after restart.
    public TimeSpan ExecutionLifetime { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(30);
}
