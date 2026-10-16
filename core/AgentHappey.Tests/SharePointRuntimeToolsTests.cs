using AgentHappey.Core.MCP.Tools;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace AgentHappey.Tests;

public sealed class SharePointRuntimeToolsTests
{
    [Fact]
    public async Task Workflow_errors_preserve_actionable_details_and_redact_secrets_and_stack_traces()
    {
        var services = new ExceptionServiceProvider(new InvalidOperationException(
            "Configuration rejected: Authorization: " + "Bearer " + "example-value; client_secret=\"example-secret\".\n   at Sensitive.Namespace.Method()"));

        var result = await SharePointRuntimeTools.AgentSharePointRuntime_RunWorkflow(
            [],
            string.Empty,
            string.Empty,
            services,
            requestContext: null!);

        Assert.True(result.IsError);
        Assert.Equal(
            "Configuration rejected: Authorization: [redacted]; client_secret=[redacted].",
            Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }

    private sealed class ExceptionServiceProvider(Exception exception) : IServiceProvider
    {
        public object? GetService(Type serviceType)
            => serviceType == typeof(ILogger<SharePointRuntimeTools>) ? null : throw exception;
    }
}
