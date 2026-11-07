using System.Text.Json;
using AgentHappey.AsyncResponses;
using AgentHappey.Common.Models;
using AgentHappey.Core;
using AgentHappey.HeaderAuth;
using AIHappey.Responses;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using AzureController = AgentHappey.AzureAuth.Controllers.ResponsesController;
using HeaderController = AgentHappey.HeaderAuth.Controllers.ResponsesController;

namespace AgentHappey.Tests;

public sealed class ResponsesRoutingTests
{
    [Theory]
    [InlineData("\"model\":\"catalog/model\"")]
    [InlineData("\"models\":[\"catalog/one\",\"catalog/two\"]")]
    [InlineData("\"agent\":{\"name\":\"Single\",\"instructions\":\"Answer\",\"model\":{\"id\":\"test/model\"}}")]
    [InlineData("\"agents\":[{\"name\":\"First\"},{\"name\":\"Second\"}]")]
    [InlineData("\"agent\":{\"name\":\"First\"},\"agents\":[{\"name\":\"Second\"}]")]
    public async Task Both_controllers_accept_root_selectors_before_checking_background_availability(string selectors)
    {
        var request = Read("{\"input\":\"Hello\",\"background\":true," + selectors + "}");
        foreach (var result in await PostBothAsync(request))
            Assert.Equal(503, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    [Theory]
    [InlineData("\"model\":\"catalog/model\"")]
    [InlineData("\"models\":[\"catalog/model\"]")]
    [InlineData("\"agent\":{\"name\":\"OldSingle\"}")]
    [InlineData("\"agents\":[{\"name\":\"OldPlural\"}]")]
    public async Task Both_controllers_reject_metadata_only_routing_with_root_field_guidance(string selectors)
    {
        var request = Read("{\"input\":\"Hello\",\"metadata\":{" + selectors + "}}");
        foreach (var result in await PostBothAsync(request))
        {
            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            var error = JsonSerializer.SerializeToElement(badRequest.Value).GetProperty("error").GetString();
            Assert.Equal("Provide 'model', 'models', 'agent', or 'agents' at the request root.", error);
        }
    }

    [Fact]
    public async Task Both_controllers_return_bad_request_for_null_agent_array_entries()
    {
        foreach (var result in await PostBothAsync(Read("""{"input":"Hello","agents":[null]}""")))
            Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void Both_endpoints_bind_the_backend_contract_not_the_shared_provider_contract()
    {
        foreach (var controller in new[] { typeof(HeaderController), typeof(AzureController) })
            Assert.Equal(typeof(AgentResponseRequest), controller.GetMethod("Post")!.GetParameters()[0].ParameterType);
    }

    private static async Task<IActionResult[]> PostBothAsync(AgentResponseRequest request)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var hostOptions = Options.Create(new HeaderAuthHostOptions());
        var disabled = new DisabledAsyncResponsesService();
        var header = new HeaderController(null!, Options.Create(new Config { AiConfig = new AiConfig() }), null!, null!, disabled, hostOptions,
            new ForegroundResponsePersistence(hostOptions, services))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        var azure = new AzureController(null!, Options.Create(new AgentHappey.AzureAuth.Config { AiConfig = new AiConfig() }),
            null!, null!, disabled, services, null!)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        return [await header.Post(request, default), await azure.Post(request, default)];
    }

    private static AgentResponseRequest Read(string json)
        => JsonSerializer.Deserialize<AgentResponseRequest>(json, ResponseJson.Default)!;
}
