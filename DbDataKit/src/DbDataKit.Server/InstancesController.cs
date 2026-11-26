using Microsoft.AspNetCore.Mvc;

namespace DbDataKit.Server;

public sealed record PairRequest(string Url, string Code, string? Name);

/// <param name="From">The server that will publish (a Build server): its id or name.</param>
/// <param name="To">The server it publishes to (a Gateway): its id or name.</param>
/// <param name="Name">What the publisher calls the other server; the other server's name, made into an id, when empty.</param>
public sealed record LinkRequest(string From, string To, string? Name);

public sealed record PendingSummary(string Id, string Name, string Product, string Url, DateTimeOffset LastSeen);

/// <param name="Generation">The compatibility generation the server reported, or the one last recorded for it (24a UC05); null when it states none.</param>
/// <param name="OutsideSupport">Set when the server is further from this console than N and N-1: the words that say how far apart they are and which side to upgrade.</param>
/// <param name="GroupJoin">On a pairing of a server whose group takes its key from the console (a runner, plan 28c E20d): null when the instance was given the group's key, else why not (the pairing stands; join it again from the server's page).</param>
public sealed record InstanceSummary(string Id, string Name, string Url, string? Product, string? Version, int? ApiVersion, bool Reachable, bool? Ready, string? Problem, DateTimeOffset? PairedAt, int? Generation = null, string? OutsideSupport = null, string? GroupJoin = null);

/// <param name="Url">The address of another instance of the group; empty to hand the key to the instance at the registered address again.</param>
/// <param name="Code">A pairing code made on that instance (or any of the group): it proves the instance serves the group before the key is sent.</param>
public sealed record JoinRequest(string? Url, string? Code);

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
            return Ok(await JoinedAsync(paired, cancellationToken));
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
        return Ok(list);
    }

    [HttpPost]
    public async Task<ActionResult<InstanceSummary>> Pair([FromBody] PairRequest request, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(request.Url?.Trim(), UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
            return BadRequest(new { error = "The address is an https address, such as https://runner.example:7840." });
        try
        {
            var paired = await instances.PairAsync(url, request.Code ?? "", request.Name, cancellationToken);
            return Ok(await JoinedAsync(paired, cancellationToken));
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
    /// Pairs two servers this console manages (plan 13, T11; plan 24a UC13): the Gateway makes a single-use code, and the runner pairs with it, both as the signed-in person, so no code is typed; the runner then tells the Gateway's cache when
    /// it wrote data. The Gateway's address is the one this console uses; the runner must be able to reach it too.
    /// </summary>
    [HttpPost("link")]
    public async Task<IActionResult> Link([FromBody] LinkRequest request, CancellationToken cancellationToken)
    {
        if (instances.Registry.Find(request.From ?? "") is not { } publisher || instances.Registry.Find(request.To ?? "") is not { } target)
            return NotFound(new { error = "Both servers must be registered here." });
        if (publisher.InstanceId == target.InstanceId) return BadRequest(new { error = "A server cannot be paired with itself." });
        var (from, to) = (await instances.StatusAsync(publisher, cancellationToken), await instances.StatusAsync(target, cancellationToken));
        // what each kind of server is told to pair with a gateway through (plan 13 T11; plan 24a UC13: a runner pairs only to tell the gateway's cache that data changed)
        if (from.Product is null || !LinkRoutes.TryGetValue(from.Product, out var route) || to.Product != ServerProducts.Gateway)
            return BadRequest(new { error = $"Linking pairs a runner with a gateway; {publisher.Name} is {from.Product ?? "not reachable"} and {target.Name} is {to.Product ?? "not reachable"}." });

        var name = string.IsNullOrWhiteSpace(request.Name) ? Slug(target.Name) : request.Name.Trim();
        var (codeStatus, code) = await relay.CallAsync(target, HttpMethod.Post, DbDataKit.Web.Trust.PairingCodeIssuer.Path, new { name = $"{publisher.Name} ({LinkLabels[from.Product]})", minutes = 5 }, cancellationToken);
        if (codeStatus != 200 || code?["code"]?.GetValue<string>() is not { } text)
            return StatusCode(codeStatus is >= 400 and < 600 ? codeStatus : 502, new { error = $"{target.Name} would not make a pairing code: {code?["error"]?.GetValue<string>() ?? "no answer"}" });

        var (status, answer) = await relay.CallAsync(publisher, HttpMethod.Post, route, new { name, url = target.Url.AbsoluteUri.TrimEnd('/'), code = text }, cancellationToken);
        return status == 200
            ? Ok(new { from = publisher.Name, to = target.Name, name })
            : StatusCode(status is >= 400 and < 600 ? status : 502, new { error = $"{publisher.Name} could not pair with {target.Name}: {answer?["error"]?.GetValue<string>() ?? "no answer"}" });
    }

    /// <summary>The call that makes a server pair with a gateway, by product: the pairing a runner keeps to tell a gateway's cache that data changed.</summary>
    private static readonly Dictionary<string, string> LinkRoutes = new(StringComparer.Ordinal) { [ServerProducts.Runner] = "/api/v1/gateways" };

    private static readonly Dictionary<string, string> LinkLabels = new(StringComparer.Ordinal) { [ServerProducts.Runner] = "Runner" };

    private static string Slug(string name)
    {
        var slug = new string([.. name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-')]).Trim('-');
        return slug.Length == 0 ? "gateway" : slug;
    }

    [HttpDelete("{id}")]
    public IActionResult Forget(string id) => instances.Remove(id) ? NoContent() : NotFound(new { error = "No such server is registered." });

    /// <summary>The summary of a server just paired, after it was joined to its group when its group takes its key from the console (plan 28c E20d).</summary>
    private async Task<InstanceSummary> JoinedAsync(DbDataKit.Web.Trust.RegisteredInstance paired, CancellationToken ct)
    {
        var join = await services.GetRequiredService<ConsoleGroupKeys>().JoinPairedAsync(paired, ct);
        return Summarise(await instances.StatusAsync(paired, ct)) with { GroupJoin = join is { Ok: false } ? join.Error : null };
    }

    /// <summary>
    /// Joins an instance to the group of a registered server (plan 28c E20d): hands it the group's key, which the console keeps, so what is sent to any instance of the group is read by every one. With an address and a code, the
    /// instance at that address (another instance on the group's state database) is paired first, which proves it serves the group; without, the instance at the registered address is given the key again.
    /// </summary>
    [HttpPost("{id}/join")]
    public async Task<IActionResult> Join(string id, [FromBody] JoinRequest request, CancellationToken cancellationToken)
    {
        if (instances.Registry.Find(id) is not { } group) return NotFound(new { error = "No such server is registered." });
        if (!ConsoleGroupKeys.TakesKey((await instances.StatusAsync(group, cancellationToken)).Product))
            return BadRequest(new { error = $"{group.Name} is not a server whose group takes its key from the console (a runner)." });
        Uri? url = null;
        if (!string.IsNullOrWhiteSpace(request.Url) && !Uri.TryCreate(request.Url.Trim(), UriKind.Absolute, out url)) return BadRequest(new { error = "The address is an https address, such as https://runner-2.example:7840." });
        var join = await services.GetRequiredService<ConsoleGroupKeys>().JoinAsync(group, url, request.Code, cancellationToken);
        return join.Ok ? Ok(new { server = group.Name, instance = join.Instance, already = join.Already }) : StatusCode(join.Status is >= 400 and < 600 ? join.Status : 502, new { error = join.Error });
    }

    private static InstanceSummary Summarise(InstanceStatus s) => new(
        s.Instance.InstanceId, s.Instance.Name, s.Instance.Url.AbsoluteUri, s.Product, s.Version, s.ApiVersion ?? s.Instance.ApiVersion, s.Reachable, s.Ready, s.Problem, s.Instance.PairedAt, Generation: s.KnownGeneration, OutsideSupport: s.OutsideSupport);
}
