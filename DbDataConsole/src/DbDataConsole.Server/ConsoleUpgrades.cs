using DbDataKit.Update;
using DbDataKit.Upgrade;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Mvc;

namespace DbDataConsole.Server;

/// <summary>A release the feed lists, for the screen.</summary>
public sealed record AvailableRelease(string Version, string Channel, DateTimeOffset? Published, string? NotesUrl, int? Generation);

/// <summary>What the feed offers of one product: its newest releases, and the generation the newest was built with when the release says so.</summary>
public sealed record ProductReleases(string Product, IReadOnlyList<AvailableRelease> Releases, string? Newest, int? NewestGeneration);

/// <summary>
/// One paired server against the feed (plan 24a UC08): what it runs, the newest release of its product, and, when that release states a compatibility generation this console is not within N and N-1 of, the words that say so
/// (<see cref="OutsideAfterRelease"/>: "outside N-1 after this release", naming both versions and generations and the side to upgrade).
/// </summary>
public sealed record ServerAvailability(string Id, string Name, string? Product, string? Version, int? Generation, string? Newest, bool NewerAvailable, int? NewestGeneration, string? NotesUrl, string? OutsideAfterRelease);

public sealed record AvailableUpgrades(string Feed, string Channel, bool Mirror, DateTimeOffset At, int ConsoleGeneration, string ConsoleVersion, IReadOnlyList<ProductReleases> Products, IReadOnlyList<ServerAvailability> Servers);

/// <summary>
/// What the console shows of upgrades (plan 24a UC08): the release feed (QU2) read with the <c>upgrade.feed</c> setting of <c>settings.yaml</c> (a mirror for a site without internet), the versions available per product, and each paired server's
/// current and newest version. It reads the feed, never a program: the upgrade of a server is the server's own call (<c>POST /api/v1/upgrade</c>, relayed), UC09. The answer is kept for five minutes; <c>refresh</c> asks the feed again.
/// </summary>
public sealed class ConsoleUpgrades(ConsoleSettings settings, ConsoleInstances instances, Func<UpgradeFeedSettings, ReleaseFeed>? feedFactory = null, TimeProvider? clock = null)
{
    public static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(5);
    private const int ListedPerProduct = 10;

    private readonly object _gate = new();
    private (DateTimeOffset At, string Key, IReadOnlyList<ProductReleases> Products)? _cached;

    public UpgradeFeedSettings Feed() => UpgradeFeedSettings.Load(settings.SettingsPath);

    /// <summary>Throws <see cref="FeedException"/> (naming the source) when the feed cannot be read or is not what it should be.</summary>
    public async Task<AvailableUpgrades> AvailableAsync(bool refresh, CancellationToken ct)
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        UpgradeFeedSettings feedSettings;
        try { feedSettings = Feed(); }
        catch (InvalidOperationException ex) { throw new FeedException(ex.Message, ex); }
        var feed = (feedFactory ?? (s => ReleaseFeed.Create(s)))(feedSettings);
        var statuses = await instances.StatusOfAllAsync(ct);
        var products = statuses.Select(s => s.Product).Where(p => p is not null).Select(p => p!).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();

        IReadOnlyList<ProductReleases> listed;
        var key = feed.Source + "|" + feedSettings.Channel + "|" + string.Join(',', products);
        lock (_gate)
            listed = !refresh && _cached is { } cached && cached.Key == key && now - cached.At < KeepFor ? cached.Products : [];
        if (listed.Count == 0 && products.Count > 0)
        {
            var read = new List<ProductReleases>();
            foreach (var product in products)
            {
                var releases = (await feed.ListAsync(product, ct)).Take(ListedPerProduct).ToList();
                int? newestGeneration = null;
                if (releases.FirstOrDefault() is { } newest && newest.Asset("release.json") is not null) newestGeneration = (await feed.DetailsAsync(newest, ct)).Generation;
                read.Add(new ProductReleases(product, [.. releases.Select((r, i) => new AvailableRelease(r.Version, r.Channel.ToString().ToLowerInvariant(), r.Published, r.NotesUrl?.AbsoluteUri, i == 0 ? newestGeneration : null))],
                    releases.FirstOrDefault()?.Version, newestGeneration));
            }

            listed = read;
            lock (_gate) _cached = (now, key, read);
        }

        var servers = statuses.Select(s => Compare(s, listed.FirstOrDefault(p => string.Equals(p.Product, s.Product, StringComparison.OrdinalIgnoreCase)))).ToList();
        return new AvailableUpgrades(feed.Source, feedSettings.Channel, feedSettings.Mirror is not null, now, SuiteCompatibility.Generation, SuiteCompatibility.ProgramVersion, listed, servers);
    }

    /// <summary>A server against the newest release of its product: newer when the release is a higher version than the one running; marked when the release's generation is outside N and N-1 of this console.</summary>
    public static ServerAvailability Compare(InstanceStatus status, ProductReleases? product)
    {
        var newest = product?.Newest;
        var newer = newest is not null && ReleaseVersion.TryParse(newest, out var offered) && (!ReleaseVersion.TryParse(status.Version, out var running) || offered > running);
        string? outside = null;
        if (newer && product?.NewestGeneration is { } generation && !SuiteCompatibility.Supports(generation))
            outside = SuiteCompatibility.Incompatibility(generation, newest);
        var notes = product?.Releases.FirstOrDefault(r => r.Version == newest)?.NotesUrl;
        return new ServerAvailability(status.Instance.InstanceId, status.Instance.Name, status.Product, status.Version, status.KnownGeneration, newest, newer, product?.NewestGeneration, notes, outside);
    }
}

/// <summary>What the console shows of upgrades: the versions the feed offers and each server against them (24a UC08). Administrators only, by the fallback policy.</summary>
[ApiController]
[Route("api/upgrades")]
public sealed class UpgradesController(ConsoleUpgrades upgrades) : ControllerBase
{
    [HttpGet("available")]
    public async Task<ActionResult<AvailableUpgrades>> Available([FromQuery] bool refresh, CancellationToken cancellationToken)
    {
        try { return Ok(await upgrades.AvailableAsync(refresh, cancellationToken)); }
        catch (FeedException ex) { return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message }); }
    }
}
