using DbDataKit.State;
using DbDataKit.Views;

namespace DbDataKit.ReportCatalogs;

/// <summary>The names a person goes by on a report server (plan 32 RC03), taken from what the identity home knows of them.</summary>
public static class ReportCatalogNames
{
    /// <summary>
    /// The person's names for <see cref="ReportCatalogAccess.Filter"/>: the external groups the sign-in recorded for them (<c>HomeDirectory.ExternalGroupsOf</c>: the same values <c>HomeDirectory.MapExternalGroup</c> maps to roles, so one record serves both), from the sources named
    /// (<c>windows</c> by default: a report server's policies name Windows users and groups; an Entra object id is no name there), and <paramref name="own"/>, the names the host knows the person by (<c>CORP\ana</c>). The home stores a group's value normalised (trimmed, lower case), and the
    /// filter compares ignoring case. <paramref name="adminGroups"/> are the groups an administrator named for a catalog that states no policies.
    /// </summary>
    public static ReportCatalogPrincipals For(IEnumerable<(string Source, string Group)> recordedGroups, IEnumerable<string>? own = null, IEnumerable<string>? sources = null, IEnumerable<string>? adminGroups = null)
    {
        var allowed = new HashSet<string>(sources ?? ["windows"], StringComparer.OrdinalIgnoreCase);
        var names = recordedGroups.Where(g => allowed.Contains(g.Source)).Select(g => g.Group).Concat(own ?? []).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new ReportCatalogPrincipals(names, adminGroups is null ? null : [.. adminGroups]);
    }
}

/// <summary>Where the latest snapshot of a catalog comes from; the registry in a host (<see cref="DefinitionStoreSnapshotSource"/>).</summary>
public interface IReportCatalogSnapshotSource
{
    /// <summary>The snapshot the channel serves, or null before the first read.</summary>
    ReportCatalogSnapshot? Latest();
}

/// <summary>The snapshot a channel serves, read from the registry and kept for <paramref name="keep"/> (a read of the state per request would be the slowest part of a browse).</summary>
public sealed class DefinitionStoreSnapshotSource(DefinitionStore store, string provider, string channel = DefinitionStore.Production, TimeSpan? keep = null, TimeProvider? time = null) : IReportCatalogSnapshotSource
{
    private readonly object gate = new();
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private ReportCatalogSnapshot? cached;
    private DateTimeOffset readAt = DateTimeOffset.MinValue;

    public ReportCatalogSnapshot? Latest()
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            if (now - readAt < (keep ?? TimeSpan.FromSeconds(15))) return cached;
            cached = store.Current(ReportCatalogDefinitions.Kind, provider, channel) is { } v ? ReportCatalogSnapshot.Parse(v.Content) : null;
            readAt = now;
            return cached;
        }
    }
}

/// <summary>
/// An external report server's catalog as the snapshot read it, filtered for the person asking (plan 32 RC03): browse, find, parameters, exports and permissions come from the snapshot, so no browse reaches the report server and one that is down does not hide the items; only
/// <b>open</b> goes to the server's provider, for an item the person's names are allowed to see (an item they may not see is <c>not_found</c>, as ours). The snapshot lists what the service account saw: the filter is <see cref="ReportCatalogAccess"/>, and the report server still
/// decides when the person's own browser asks it for the frame.
/// </summary>
/// <param name="provider">The name that prefixes the keys of the items.</param>
/// <param name="source">Where the latest snapshot comes from.</param>
/// <param name="principals">The person's names on the report server (<see cref="ReportCatalogNames"/>).</param>
/// <param name="live">The provider that builds the address to open (and reads nothing of the person); null: items are listed but not opened (<c>unavailable</c>).</param>
public sealed class SnapshotCatalogProvider(string provider, IReportCatalogSnapshotSource source, Func<ReportCatalogViewer, ReportCatalogPrincipals> principals, IReportCatalogProvider? live = null) : IReportCatalogProvider
{
    public string Provider => provider;

    private IReadOnlyList<ReportCatalogSnapshotItem> Visible(ReportCatalogViewer viewer)
    {
        if (source.Latest() is not { } snapshot) throw new ReportCatalogException("unavailable", $"The catalog '{provider}' has not been read yet.");
        return ReportCatalogAccess.Filter(snapshot, principals(viewer)).Items;
    }

    private ReportCatalogItem Of(ReportCatalogSnapshotItem i) => new(i.Id, i.Path, i.Name, i.Type, provider, i.Description, i.Owner, i.Modified);

    private static string Normalize(string? folder) => string.IsNullOrEmpty(folder) || folder == "/" ? "/" : "/" + folder.Trim('/');

    private static string ParentOf(string path) => path.LastIndexOf('/') is var slash and > 0 ? path[..slash] : "/";

    public Task<IReadOnlyList<ReportCatalogItem>> BrowseAsync(ReportCatalogViewer viewer, ReportCatalogQuery query, CancellationToken cancellationToken = default)
    {
        var items = Visible(viewer);
        if (!string.IsNullOrEmpty(query.Search))
            return Task.FromResult<IReadOnlyList<ReportCatalogItem>>([.. items.Where(i => Holds(i.Name, query.Search) || Holds(i.Path, query.Search) || (i.Description is not null && Holds(i.Description, query.Search))).Select(Of)]);
        var folder = Normalize(query.Folder);
        // a folder the person is not allowed anything in looks like a folder that is not there
        if (folder != "/" && !items.Any(i => i.Type == ReportItemTypes.Folder && string.Equals(i.Path, folder, StringComparison.OrdinalIgnoreCase)))
            throw new ReportCatalogException("not_found", $"There is no folder '{folder}' in the catalog '{provider}'.");
        return Task.FromResult<IReadOnlyList<ReportCatalogItem>>([.. items.Where(i => string.Equals(ParentOf(i.Path), folder, StringComparison.OrdinalIgnoreCase)).Select(Of)]);
    }

    private static bool Holds(string text, string search) => text.Contains(search, StringComparison.OrdinalIgnoreCase);

    private ReportCatalogSnapshotItem? Item(ReportCatalogViewer viewer, string id) => Visible(viewer).FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));

    private ReportCatalogSnapshotItem Require(ReportCatalogViewer viewer, string id) =>
        Item(viewer, id) ?? throw new ReportCatalogException("not_found", $"There is no item '{id}' in the catalog '{provider}'.");

    public Task<ReportCatalogItem?> FindAsync(ReportCatalogViewer viewer, string id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Item(viewer, id) is { } i ? Of(i) : null);

    public Task<IReadOnlyList<ReportCatalogParameter>> ParametersAsync(ReportCatalogViewer viewer, string id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Require(viewer, id).Parameters);

    public Task<IReadOnlyList<ReportCatalogExport>> ExportsAsync(ReportCatalogViewer viewer, string id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Require(viewer, id).Exports);

    public Task<ReportCatalogPermissions> PermissionsAsync(ReportCatalogViewer viewer, string id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Require(viewer, id).Permissions);

    public Task<ReportCatalogOpening> OpenAsync(ReportCatalogViewer viewer, string id, IReadOnlyDictionary<string, string?>? parameters = null, string? layout = null, CancellationToken cancellationToken = default)
    {
        var item = Require(viewer, id);
        if (live is null) throw new ReportCatalogException("unavailable", $"The catalog '{provider}' cannot open items here: no connection to its report server is configured.");
        // the export format is one the snapshot says the item offers
        if (parameters is not null && parameters.TryGetValue(ReportCatalogExportFormat.Parameter, out var format) && format is not null
            && !item.Exports.Any(e => e.Available && string.Equals(e.Format, format, StringComparison.OrdinalIgnoreCase)))
            throw new ReportCatalogException("invalid", $"The item '{item.Path}' is not offered in the format '{format}'.");
        return live.OpenAsync(viewer, id, parameters, layout, cancellationToken);
    }
}
