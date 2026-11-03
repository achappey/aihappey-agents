using AgentHappey.Common.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http.Features;

namespace AgentHappey.HeaderAuth.Controllers;

[ApiController]
[Route("v1/models")]
public class ModelsController(IModelCatalog modelCatalog) : ControllerBase
{
    // Shared by the hosted HeaderAuth deployment and the standalone host.
    [HttpGet]
    public async Task<ActionResult<ModelListResponse>> Get(CancellationToken cancellationToken)
    {
        var response = await modelCatalog.ListAsync(cancellationToken);
        return Ok(ModelApi.Summary(response));
    }

    [HttpGet("{**modelId}")]
    public async Task<ActionResult<Model>> GetModel(string modelId, CancellationToken cancellationToken)
    {
        var id = ModelApi.DecodeId(modelId, HttpContext.Features.Get<IHttpRequestFeature>()?.RawTarget);
        var model = await modelCatalog.RetrieveAsync(id, cancellationToken);
        return model is null
            ? NotFound(new { error = new { message = $"Model '{id}' was not found.", type = "invalid_request_error", param = "model", code = "model_not_found" } })
            : Ok(model);
    }
}
