using AgentHappey.Common.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http.Features;

namespace AgentHappey.AzureAuth.Controllers;

[ApiController]
[Route("v1/models")]
[Authorize]
public class ModelsController(IModelCatalog modelCatalog) : ControllerBase
{
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
