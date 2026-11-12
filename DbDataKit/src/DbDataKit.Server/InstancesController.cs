using Microsoft.AspNetCore.Mvc;

namespace DbDataKit.Server;

public sealed record PairRequest(string Url, string Code, string? Name);

/// <param name="From">The server that will publish (a Build server): its id or name.</param>
/// <param name="To">The server it publishes to (a Gateway): its id or name.</param>
/// <param name="Name">What the publisher calls the other server; the other server's name, made into an id, when empty.</param>
public sealed record LinkRequest(string From, string To, string? Name);

public sealed record PendingSummary(string Id, string Name, string Product, string Url, DateTimeOffset LastSeen);

/// <param name="Embedded">True for the runner built into this console (plan 28a R20): it is listed only when the console runs it, has no pairing and cannot be forgotten.</param>
/// <param name="Generation">The compatibility generation the server reported, or the one last recorded for it (24a UC05); null when it states none.</param>
/// <param name="OutsideSupport">Set when the server is further from this console than N and N-1: the words that say how far apart they are and which side to upgrade.</param>
public sealed record InstanceSummary(string Id, string Name, string Url, string? Product, string? Version, int? ApiVersion, bool Reachable, bool? Ready, string? Problem, DateTimeOffset? PairedAt, bool Embedded = false, int? Generation = null, string? OutsideSupport = null);

/// <summary>
/// The servers this console manages: list them with how each is, pair another, forget one (plan 13, U12). Administrators only, by the fallback policy: pairing gives this console's signing key
/// to another machine.
/// </summary>
[ApiController]
[Route("api/instances")]
public sealed class InstancesController(ConsoleInstances instances, ConsoleRelay relay, PendingInstances pending, IServiceProvider services) : ControllerBase
{
    /// <summary>The servers that announced themselves and wait to be confirmed (never their codes).</summary>
    [HttpGet("pending")]
    public ActionResult<IReadOnlyList<PendingSummary>> Pending() =>
        Ok(pending.All().Select(p => new PendingSummary(p.InstanceId, p.Name, p.Product, p.Url.AbsoluteUri, p.LastSeen)).ToList());

    /// <summary>Pairs with a waiting server, as an administrator's confirmation.</summary>
    [HttpPost("pending/{id}/confirm")]
    public async Task<ActionResult<InstanceSummary>> Confirm(string id, CancellationToken cancellationToken)
    {
        try
        {
            var paired = await instances.ConfirmAsync(pending, id, cancellationToken);
            return Ok(Summarise(await instances.StatusAsync(paired, cancellationToken)));
        }
        catch (DbDataKit.Web.Trust.PairingFailedException ex) { return BadRequest(new { error = ex.Message }); }
        catch (DbDataKit.Web.Trust.IncompatibleServerException ex) { return BadRequest(new { error = ex.Message }); }
    }

    /// <summary>Forgets a waiting server without pairing; it will be listed again while it keeps announcing.</summary>
    [HttpDelete("pending/{id}")]
    public IActionResult Reject(string id) => pending.Remove(id) ? NoContent() : NotFound(new { error = "No such server is waiting." });

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<InstanceSummary>>> List(CancellationToken cancellationToken)
    {
        var list = (await instances.StatusOfAllAsync(cancellationToken)).Select(Summarise).ToList();
        // the runner built into this console, when it runs one: first, because it is this console's own
        if (services.GetService(typeof(ConsoleRunner)) is ConsoleRunner runner)
            list.Insert(0, new InstanceSummary(ConsoleRunnerSettings.ServerId, "Console runner", $"{Request.Scheme}://{Request.Host}/", ServerProducts.Runner, runner.Version, null,
                runner.Host is not null, runner.Host is not null, runner.Host is null ? "the embedded runner is not running" : null, null, Embedded: true));
        return Ok(list);
    }

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
    /// Pairs two servers this console manages (plan 13, T11): the Gateway makes a single-use code for a publisher, and the Build server pairs with it, both as the signed-in person, so no code is typed. The Gateway's address is the one this console uses; the Build
    /// server must be able to reach it too.
    /// </summary>
    [HttpPost("link")]
    public async Task<IActionResult> Link([FromBody] LinkRequest request, CancellationToken cancellationToken)
    {
        if (instances.Registry.Find(request.From ?? "") is not { } publisher || instances.Registry.Find(request.To ?? "") is not { } target)
            return NotFound(new { error = "Both servers must be registered here." });
        if (publisher.InstanceId == target.InstanceId) return BadRequest(new { error = "A server cannot be paired with itself." });
        var (from, to) = (await instances.StatusAsync(publisher, cancellationToken), await instances.StatusAsync(target, cancellationToken));
        // what each kind of server is told to pair with a gateway through (plan 13 T11; plan 24a UC13 adds Sync and the Runner, which pair only to tell the gateway's cache that data changed)
        if (from.Product is null || !LinkRoutes.TryGetValue(from.Product, out var route) || to.Product != "dbdatagateway")
            return BadRequest(new { error = $"Linking pairs a Build, Sync or Runner server with a gateway; {publisher.Name} is {from.Product ?? "not reachable"} and {target.Name} is {to.Product ?? "not reachable"}." });

        var name = string.IsNullOrWhiteSpace(request.Name) ? Slug(target.Name) : request.Name.Trim();
        var (codeStatus, code) = await relay.CallAsync(target, HttpMethod.Post, DbDataKit.Web.Trust.PairingCodeIssuer.Path, new { name = $"{publisher.Name} ({LinkLabels[from.Product]})", minutes = 5 }, cancellationToken);
        if (codeStatus != 200 || code?["code"]?.GetValue<string>() is not { } text)
            return StatusCode(codeStatus is >= 400 and < 600 ? codeStatus : 502, new { error = $"{target.Name} would not make a pairing code: {code?["error"]?.GetValue<string>() ?? "no answer"}" });

        var (status, answer) = await relay.CallAsync(publisher, HttpMethod.Post, route, new { name, url = target.Url.AbsoluteUri.TrimEnd('/'), code = text }, cancellationToken);
        return status == 200
            ? Ok(new { from = publisher.Name, to = target.Name, name })
            : StatusCode(status is >= 400 and < 600 ? status : 502, new { error = $"{publisher.Name} could not pair with {target.Name}: {answer?["error"]?.GetValue<string>() ?? "no answer"}" });
    }

    /// <summary>The call that makes a server pair with a gateway, by product: Build's publishing pairing, and the pairings Sync and the Runner keep to tell a gateway's cache that data changed.</summary>
    private static readonly Dictionary<string, string> LinkRoutes = new(StringComparer.Ordinal)
    {
        ["dbdatabuild"] = "/api/v1/proxies", ["dbdatasync"] = "/api/gateways", ["dbdatarunner"] = "/api/v1/gateways",
    };

    private static readonly Dictionary<string, string> LinkLabels = new(StringComparer.Ordinal) { ["dbdatabuild"] = "Build", ["dbdatasync"] = "Sync", ["dbdatarunner"] = "Runner" };

    private static string Slug(string name)
    {
        var slug = new string([.. name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-')]).Trim('-');
        return slug.Length == 0 ? "gateway" : slug;
    }

    [HttpDelete("{id}")]
    public IActionResult Forget(string id) => id == ConsoleRunnerSettings.ServerId ? BadRequest(new { error = "The console's embedded runner is part of the console and cannot be forgotten; turn it off with runner.enabled in settings.yaml." }) : instances.Remove(id) ? NoContent() : NotFound(new { error = "No such server is registered." });

    private static InstanceSummary Summarise(InstanceStatus s) => new(
        s.Instance.InstanceId, s.Instance.Name, s.Instance.Url.AbsoluteUri, s.Product, s.Version, s.ApiVersion ?? s.Instance.ApiVersion, s.Reachable, s.Ready, s.Problem, s.Instance.PairedAt, Generation: s.KnownGeneration, OutsideSupport: s.OutsideSupport);
}
