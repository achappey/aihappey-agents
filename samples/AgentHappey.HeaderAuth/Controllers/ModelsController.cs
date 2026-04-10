using AgentHappey.Common.Models;
using Microsoft.AspNetCore.Mvc;

namespace AgentHappey.HeaderAuth.Controllers;

[ApiController]
[Route("v1/models")]
public class ModelsController(IModelCatalog modelCatalog) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ModelListResponse>> Get(CancellationToken cancellationToken)
    {
        var response = await modelCatalog.ListAsync(cancellationToken);
        return Ok(response);
    }
}
