using DbDataKit.Web.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbDataKit.Server;

/// <summary>
/// The checks of the suite's datasets (plan 23a FQ17): <c>GET /api/quality</c> is every dataset with the checks of every server that evaluates them, the latest result and the block of each, and what each server said. The history of one check and an
/// evaluation on request are the server's own routes through the relay (<c>/relay/{server}/api/quality/history</c>, <c>/api/quality/run</c>), where the server decides who may.
/// </summary>
[ApiController]
[Route("api/quality")]
public sealed class QualityController(ConsoleQuality quality) : ControllerBase
{
    [HttpGet]
    [Authorize(Policies.Viewer)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken) => Ok(await quality.OverviewAsync(cancellationToken));
}
