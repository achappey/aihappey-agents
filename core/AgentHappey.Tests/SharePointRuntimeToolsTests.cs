using AgentHappey.Core.MCP.Tools;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace AgentHappey.Tests;

public sealed class SharePointRuntimeToolsTests
{
    [Fact]
    public async Task Workflow_errors_do_not_expose_exception_details()
    {
        using var services = new ServiceCollection().BuildServiceProvider();

        var result = await SharePointRuntimeTools.AgentSharePointRuntime_RunWorkflow(
            [],
            string.Empty,
            string.Empty,
            services,
            requestContext: null!);

        Assert.True(result.IsError);
        Assert.Equal(
            "SharePoint workflow execution failed.",
            Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }
}
