using AgentHappey.Agents.JSON;

namespace AgentHappey.Tests;

public sealed class JsonModelSourceTests
{
    [Fact]
    public async Task Get_models_observes_cancellation()
    {
        var source = new JsonModelSource(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), null);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => source.GetModelsAsync(cancellation.Token));
    }

    [Fact]
    public async Task Get_agents_observes_cancellation()
    {
        var source = new JsonModelSource(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), null);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => source.GetAgentsAsync(cancellation.Token));
    }
}
