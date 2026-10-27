using Microsoft.AspNetCore.Mvc;

namespace DbDataConsole.Server;

public sealed record PairRequest(string Url, string Code, string? Name);

/// <param name="From">The server that will publish (a Build server): its id or name.</param>
/// <param name="To">The server it publishes to (a Proxy): its id or name.</param>
/// <param name="Name">What the publisher calls the other server; the other server's name, made into an id, when empty.</param>
public sealed record LinkRequest(string From, string To, string? Name);

public sealed record InstanceSummary(string Id, string Name, string Url, string? Product, string? Version, int? ApiVersion, bool Reachable, bool? Ready, string? Problem, DateTimeOffset? PairedAt);

/// <summary>
/// The servers this console manages: list them with how each is, pair another, forget one (plan 13, U12). Administrators only, by the fallback policy: pairing gives this console's signing key
/// to another machine.
/// </summary>
[ApiController]
[Route("api/instances")]
public sealed class InstancesController(ConsoleInstances instances, ConsoleRelay relay) : ControllerBase
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

    /// <summary>
    /// Pairs two servers this console manages (plan 13, T11): the Proxy makes a single-use code for a publisher, and the Build server pairs with it, both as the signed-in person, so no code is typed. The Proxy's address is the one this console uses; the Build
    /// server must be able to reach it too.
    /// </summary>
    [HttpPost("link")]
    public async Task<IActionResult> Link([FromBody] LinkRequest request, CancellationToken cancellationToken)
    {
        if (instances.Registry.Find(request.From ?? "") is not { } publisher || instances.Registry.Find(request.To ?? "") is not { } target)
            return NotFound(new { error = "Both servers must be registered here." });
        if (publisher.InstanceId == target.InstanceId) return BadRequest(new { error = "A server cannot be paired with itself." });
        var (from, to) = (await instances.StatusAsync(publisher, cancellationToken), await instances.StatusAsync(target, cancellationToken));
        if (from.Product != "dbdatabuild" || to.Product != "dbdataproxy")
            return BadRequest(new { error = $"Linking pairs a Build server with a proxy; {publisher.Name} is {from.Product ?? "not reachable"} and {target.Name} is {to.Product ?? "not reachable"}." });

        var name = string.IsNullOrWhiteSpace(request.Name) ? Slug(target.Name) : request.Name.Trim();
        var (codeStatus, code) = await relay.CallAsync(target, HttpMethod.Post, DbDataKit.Web.Trust.PairingCodeIssuer.Path, new { name = $"{publisher.Name} (Build)", minutes = 5 }, cancellationToken);
        if (codeStatus != 200 || code?["code"]?.GetValue<string>() is not { } text)
            return StatusCode(codeStatus is >= 400 and < 600 ? codeStatus : 502, new { error = $"{target.Name} would not make a pairing code: {code?["error"]?.GetValue<string>() ?? "no answer"}" });

        var (status, answer) = await relay.CallAsync(publisher, HttpMethod.Post, "/api/v1/proxies", new { name, url = target.Url.AbsoluteUri.TrimEnd('/'), code = text }, cancellationToken);
        return status == 200
            ? Ok(new { from = publisher.Name, to = target.Name, name })
            : StatusCode(status is >= 400 and < 600 ? status : 502, new { error = $"{publisher.Name} could not pair with {target.Name}: {answer?["error"]?.GetValue<string>() ?? "no answer"}" });
    }

    private static string Slug(string name)
    {
        var slug = new string([.. name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-')]).Trim('-');
        return slug.Length == 0 ? "proxy" : slug;
    }

    [HttpDelete("{id}")]
    public IActionResult Forget(string id) => instances.Remove(id) ? NoContent() : NotFound(new { error = "No such server is registered." });

    private static InstanceSummary Summarise(InstanceStatus s) => new(
        s.Instance.InstanceId, s.Instance.Name, s.Instance.Url.AbsoluteUri, s.Product, s.Version, s.ApiVersion ?? s.Instance.ApiVersion, s.Reachable, s.Ready, s.Problem, s.Instance.PairedAt);
}
