using DbDataKit.State;
using DbDataKit.Web.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbDataKit.Server;

/// <summary>One item of a new release: a package on a runner (or a definition on a Gateway, <paramref name="Product"/> <c>gateway</c> with its <paramref name="Kind"/>) moved from one channel to the next; <paramref name="Source"/> is the server the version is read from (absent: the target itself).</summary>
public sealed record ReleaseItemBody(string Target, string Name, int Version, string FromChannel, string ToChannel, string? Source = null, string? Product = null, string? Kind = null);

public sealed record ReleaseBody(string Name, List<ReleaseItemBody>? Items);

/// <summary>
/// The releases of the console (plan 21 CH02): <c>GET api/releases[/{id}]</c> for everyone signed in; <c>POST api/releases</c> (a draft), <c>{id}/approve</c> (one approval, by someone who holds the administrator role on every item's target server, refused naming each
/// item that lacks it), <c>{id}/start</c>, <c>{id}/resume</c> and <c>{id}/abandon</c> for administrators. Start and resume run the items in order as the signed-in person and answer when the release has succeeded or stopped; it is a sequence, not a transaction.
/// </summary>
[ApiController]
[Route("api/releases")]
public sealed class ReleasesController(ConsoleReleases releases) : ControllerBase
{
    private string PersonId => User.FindFirst(ConsoleHost.Auth.UserIdClaim)?.Value ?? User.Identity?.Name ?? "unknown";
    private RelayPerson Person => new(PersonId, User.Identity?.Name, User.IsInRole(nameof(UserRole.Admin)) ? UserRole.Admin : UserRole.Viewer);

    private object View(ReleaseRecord r) => new
    {
        r.Id, r.Name, r.State, r.CreatedBy, createdByName = releases.NameOf(r.CreatedBy), r.CreatedAt, r.ApprovedBy, approvedByName = releases.NameOf(r.ApprovedBy), r.ApprovedAt, r.StoppedAt, r.StoppedReason, r.UpdatedAt,
        items = r.Items.Select(i => new { i.Position, i.Product, i.Target, i.Source, i.Kind, i.Name, i.Version, i.FromChannel, i.ToChannel, i.State, i.Error, i.FinishedAt }),
    };

    [HttpGet]
    [Authorize(Policies.Viewer)]
    public IActionResult List() => Ok(releases.Store.List().Select(View));

    [HttpGet("{id}")]
    [Authorize(Policies.Viewer)]
    public IActionResult Get(string id) => releases.Store.Get(id) is { } r ? Ok(View(r)) : NotFound(new { error = $"There is no release '{id}'." });

    [HttpPost]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> Create([FromBody] ReleaseBody body, CancellationToken ct)
    {
        var items = (body.Items ?? []).Select(i =>
        {
            var product = i.Product ?? ConsoleReleases.Product;
            return new ReleaseItem(0, product, i.Target, i.Kind ?? (product == ConsoleReleases.Product ? ConsoleReleases.Kind : ""), i.Name, i.Version, i.FromChannel, i.ToChannel, Source: string.IsNullOrWhiteSpace(i.Source) ? null : i.Source);
        }).ToList();
        for (var n = 0; n < items.Count; n++)
            if (await releases.ProblemAsync(items[n], ct) is { } problem) return BadRequest(new { error = $"Item {n + 1}: {problem}" });
        return Do(() => Created($"/api/releases/", View(releases.Store.Create(body.Name, PersonId, items))));
    }

    [HttpPost("{id}/approve")]
    [Authorize(Policies.Admin)]
    public IActionResult Approve(string id) => Do(() => Ok(View(releases.Approve(id, PersonId))));

    [HttpPost("{id}/abandon")]
    [Authorize(Policies.Admin)]
    public IActionResult Abandon(string id) => Do(() => Ok(View(releases.Store.Abandon(id))));

    [HttpPost("{id}/start")]
    [Authorize(Policies.Admin)]
    public Task<IActionResult> Start(string id, CancellationToken ct) => Run(id, false, ct);

    [HttpPost("{id}/resume")]
    [Authorize(Policies.Admin)]
    public Task<IActionResult> Resume(string id, CancellationToken ct) => Run(id, true, ct);

    private async Task<IActionResult> Run(string id, bool resume, CancellationToken ct)
    {
        try { return Ok(View(await releases.StartAsync(id, Person, resume, ct))); }
        catch (ReleaseRefusedException ex) { return Conflict(new { error = ex.Message }); }
        catch (OperationCanceledException) { return StatusCode(499, new { error = "The request was cancelled; the release is stopped at its item and can be resumed." }); }
    }

    private IActionResult Do(Func<IActionResult> act)
    {
        try { return act(); }
        catch (ReleaseRefusedException ex) { return Conflict(new { error = ex.Message }); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
    }
}
