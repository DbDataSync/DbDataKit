using System.Security.Cryptography.X509Certificates;
using DbDataKit.Hosting;
using DbDataKit.State;
using DbDataKit.Views.Export;

namespace DbDataKit.Views.Hosting;

/// <summary>Where the module listens when it has a listener of its own (J28's design: REST is its own listener, off unless enabled). Port 0 takes a free one (<see cref="ViewsModule.Address"/> says which).</summary>
/// <param name="Address">The address to bind: loopback by default.</param>
/// <param name="Port">The port; 0 takes a free one.</param>
/// <param name="Certificate">TLS; null serves plain HTTP, which only a loopback address or a test should do.</param>
public sealed record ViewsListen(string Address = "127.0.0.1", int Port = 0, X509Certificate2? Certificate = null);

/// <summary>What the module is configured with. The defaults suit an instance; a test sets the clock and the intervals.</summary>
public sealed class ViewsOptions
{
    /// <summary>The module's own listener; null opens none (a host mounts <see cref="ViewsModule.Map"/> on its own).</summary>
    public ViewsListen? Listen { get; init; } = new();

    /// <summary>The channel the module answers from (plan 21).</summary>
    public string Channel { get; init; } = DefinitionStore.Production;

    /// <summary>Limits of a resolution (rows, widgets, parallelism, a widget's time). The cache is the module's (<see cref="CacheTtl"/>), not part of this.</summary>
    public ResolverOptions Resolver { get; init; } = new();

    /// <summary>How long the rows of a request are kept for the viewer who asked (their access, the channel and the request are the key). Zero turns the cache off.</summary>
    public TimeSpan CacheTtl { get; init; } = TimeSpan.FromSeconds(30);

    public int CacheEntries { get; init; } = 2000;

    /// <summary>The largest request body (parameters and a layout name are small).</summary>
    public int MaxBodyBytes { get; init; } = 64 * 1024;

    /// <summary>The largest preview request (plan 32 DBR28): a whole draft document travels in it, so it is larger than a render request; one over it is refused with 413 and nothing is read.</summary>
    public int MaxPreviewBytes { get; init; } = 256 * 1024;

    /// <summary>Whether the module follows the group's definitions itself (a <see cref="DefinitionFollower"/> polled every <see cref="PollInterval"/>). A host that already runs one follower adds <see cref="ViewsPublications.Subscribers"/> to it and sets this false.</summary>
    public bool Follow { get; init; } = true;

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a drain waits for requests in flight.</summary>
    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The module's drain: one coordinator shared with the host, so its drain stops this module too. Null makes the module's own.</summary>
    public IDrainCoordinator? Drain { get; init; }

    /// <summary>The exporter (plan 32 DBR15, <c>views.export</c>); null leaves export off, the module says so and keeps serving screens. The module checks it at start and reports it in the health.</summary>
    public IReportExporter? Exporter { get; init; }

    /// <summary>The folder with <c>print.js</c> and <c>print.css</c>, the visuals' print page (built from <c>web/visuals</c>); the module serves it to the page and to the browser's own print route. Null: the print page is not there and export is unavailable, saying so.</summary>
    public string? PageAssets { get; init; }

    /// <summary>The apps this host serves, for custom views (plan 32 DBR31, DBR32): the manifest entry of a custom widget's component for the resolver and the app's built files for the export. Null: a custom widget is an error entry that says this host serves no app.</summary>
    public ICustomViewApps? CustomApps { get; init; }

    /// <summary>The most rows a CSV or xlsx export holds in all (the rows of every sheet): a larger one is refused, in words, with 413. Not a browser's limit, so it does not come from <c>views.export</c>'s exporter settings.</summary>
    public long MaxExportRows { get; init; } = 100_000;

    /// <summary>The store of people's own dashboards and reports (plan 32 DBR12, the state module <c>user_views</c>); null leaves the <c>/api/v1/my/...</c> routes answering 404 and says why. The host opens the module and passes the store.</summary>
    public UserViewStore? UserViews { get; init; }

    /// <summary>The merged report catalog (plan 32 RC03): our own dashboards and reports (<see cref="ViewsCatalog"/>) beside the external report servers' (the host adds a provider per <c>report-catalog</c> connection). Null leaves <c>/api/v1/catalog</c> answering 404 and saying why.</summary>
    public ReportCatalogs? Catalogs { get; init; }

    /// <summary>What runs an action link's change or task as the viewer (plan 32 DBR53): in the Gateway the group's Store. Null leaves the action routes answering 501 `actions_unavailable`.</summary>
    public IViewsActions? Actions { get; init; }

    /// <summary>The most changes a staged set holds (plan 32 DBR53b, a setting): a batch with more lines is refused before anything is derived.</summary>
    public int MaxBatchChanges { get; init; } = 1000;

    /// <summary>The largest batch request body: a set of 1,000 staged rows with their values is a few hundred kilobytes.</summary>
    public int MaxBatchBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>How long a finished action's answer is kept for its idempotency key: a call repeated with the key in this time gets the same answer and does nothing again.</summary>
    public TimeSpan ActionRetention { get; init; } = TimeSpan.FromMinutes(10);

    public TimeProvider Time { get; init; } = TimeProvider.System;
}
