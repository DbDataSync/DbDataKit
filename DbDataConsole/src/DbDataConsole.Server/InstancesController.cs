using Microsoft.AspNetCore.Mvc;

namespace DbDataConsole.Server;

public sealed record PairRequest(string Url, string Code, string? Name);

public sealed record InstanceSummary(string Id, string Name, string Url, string? Product, string? Version, int? ApiVersion, bool Reachable, bool? Ready, string? Problem, DateTimeOffset? PairedAt);

/// <summary>
/// The servers this console manages: list them with how each is, pair another, forget one (plan 13, U12). Administrators only, by the fallback policy: pairing gives this console's signing key
/// to another machine.
/// </summary>
[ApiController]
[Route("api/instances")]
public sealed class InstancesController(ConsoleInstances instances) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<InstanceSummary>>> List(CancellationToken cancellationToken) =>
        Ok((await instances.StatusOfAllAsync(cancellationToken)).Select(Summarise).ToList());

    [HttpPost]
    public async Task<ActionResult<InstanceSummary>> Pair([FromBody] PairRequest request, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(request.Url?.Trim(), UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
            return BadRequest(new { error = "The address is an https address, such as https://build.example:8470." });
        try
        {
            var paired = await instances.PairAsync(url, request.Code ?? "", request.Name, cancellationToken);
            return Ok(Summarise(await instances.StatusAsync(paired, cancellationToken)));
        }
        catch (DbDataKit.Web.Trust.PairingFailedException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (DbDataKit.Web.Trust.IncompatibleServerException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("{id}")]
    public IActionResult Forget(string id) => instances.Remove(id) ? NoContent() : NotFound(new { error = "No such server is registered." });

    private static InstanceSummary Summarise(InstanceStatus s) => new(
        s.Instance.InstanceId, s.Instance.Name, s.Instance.Url.AbsoluteUri, s.Product, s.Version, s.ApiVersion ?? s.Instance.ApiVersion, s.Reachable, s.Ready, s.Problem, s.Instance.PairedAt);
}
