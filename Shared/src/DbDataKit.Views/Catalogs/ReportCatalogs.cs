using System.Globalization;

namespace DbDataKit.Views;

/// <summary>
/// The merged view over the providers of a host (plan 32 section 9e): our own dashboards and reports (<c>views</c>) beside an external report server's, one browse, one key for an item (<c>provider:id</c>) and one way to open it.
/// <list type="bullet">
/// <item><b>A provider that is down does not hide the others</b>: its answer is a <see cref="ReportCatalogProblem"/> in the listing (<c>unavailable</c>, and for anything unexpected only the kind of failure, never its words, which can name servers and files), and the items of the rest are given.</item>
/// <item><b>A refusal for this viewer is silence</b>: a provider that says <c>denied</c> or <c>not_found</c> to a browse contributes no items; the merged listing never says an item exists that the person may not see.</item>
/// <item><b>An external item is framed only from an approved origin</b> (the app instance's <c>frame</c> exception, plan 26 AP17, is where a host takes the list from): an opening a provider offers as <c>frame</c> whose address is not on an approved origin is handed on as a <c>link</c>, and an address that is not <c>http</c> or <c>https</c> is refused.</item>
/// </list>
/// </summary>
public sealed class ReportCatalogs
{
    private readonly List<IReportCatalogProvider> providers;
    private readonly HashSet<string> frameOrigins;

    /// <param name="providers">In the order their items are listed; each with its own name, which holds no colon.</param>
    /// <param name="frameOrigins">The origins (<c>https://reports.example.com</c>) an external report may be framed from; none: every external item is a link.</param>
    public ReportCatalogs(IEnumerable<IReportCatalogProvider> providers, IEnumerable<string>? frameOrigins = null)
    {
        this.providers = [.. providers];
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var provider in this.providers)
        {
            if (string.IsNullOrEmpty(provider.Provider) || provider.Provider.Contains(':', StringComparison.Ordinal))
                throw new ArgumentException($"A provider's name is a word with no colon (the key of its items is `name:id`), not '{provider.Provider}'.", nameof(providers));
            if (!names.Add(provider.Provider)) throw new ArgumentException($"Two providers are named '{provider.Provider}'.", nameof(providers));
        }
        this.frameOrigins = new HashSet<string>(StringComparer.Ordinal);
        foreach (var origin in frameOrigins ?? [])
            this.frameOrigins.Add(OriginOf(origin) ?? throw new ArgumentException($"'{origin}' is not an origin (scheme and host, like https://reports.example.com).", nameof(frameOrigins)));
    }

    public IReadOnlyList<string> Providers => [.. providers.Select(p => p.Provider)];

    /// <summary>The scheme, host and port of an http or https address, in lower case, or null for anything else.</summary>
    public static string? OriginOf(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0) return null;
        var port = uri.IsDefaultPort ? "" : ":" + uri.Port.ToString(CultureInfo.InvariantCulture);
        return $"{uri.Scheme}://{uri.Host.ToLowerInvariant()}{port}";
    }

    /// <summary>Splits <c>provider:id</c>; false for a key with no provider part or an empty id.</summary>
    public static bool TryParseKey(string key, out string provider, out string id)
    {
        var colon = key.IndexOf(':', StringComparison.Ordinal);
        provider = colon > 0 ? key[..colon] : "";
        id = colon > 0 ? key[(colon + 1)..] : "";
        return provider.Length > 0 && id.Length > 0;
    }

    // ---- browse ----

    public async Task<ReportCatalogListing> BrowseAsync(ReportCatalogViewer viewer, ReportCatalogQuery? query = null, CancellationToken cancellationToken = default)
    {
        query ??= new ReportCatalogQuery();
        var items = new List<ReportCatalogItem>();
        var problems = new List<ReportCatalogProblem>();
        foreach (var provider in providers)
        {
            try
            {
                var found = await provider.BrowseAsync(viewer, query, cancellationToken).ConfigureAwait(false);
                items.AddRange(found.Where(i => query.Wants(i.Type)).Select(i => i with { Provider = provider.Provider }).OrderBy(i => i.Path, StringComparer.Ordinal));
            }
            catch (ReportCatalogException e) when (e.Code is "denied" or "not_found")
            {
                // this person sees nothing of it here
            }
            catch (ReportCatalogException e)
            {
                problems.Add(new ReportCatalogProblem(provider.Provider, e.Code, e.Message));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
            {
                problems.Add(new ReportCatalogProblem(provider.Provider, "unavailable", $"The catalog '{provider.Provider}' failed ({e.GetType().Name})."));
            }
        }
        return new ReportCatalogListing(items, problems);
    }

    // ---- one item, by key ----

    private (IReportCatalogProvider Provider, string Id) Route(string key)
    {
        if (!TryParseKey(key, out var name, out var id))
            throw new ReportCatalogException("invalid", $"'{key}' is not an item's key: it is the provider's name, a colon and the item's id (`views:dashboard/sales`).");
        var provider = providers.FirstOrDefault(p => p.Provider == name) ?? throw new ReportCatalogException("not_found", $"There is no catalog named '{name}' here.");
        return (provider, id);
    }

    public async Task<ReportCatalogItem?> FindAsync(ReportCatalogViewer viewer, string key, CancellationToken cancellationToken = default)
    {
        var (provider, id) = Route(key);
        return await provider.FindAsync(viewer, id, cancellationToken).ConfigureAwait(false) is { } item ? item with { Provider = provider.Provider } : null;
    }

    public async Task<IReadOnlyList<ReportCatalogParameter>> ParametersAsync(ReportCatalogViewer viewer, string key, CancellationToken cancellationToken = default)
    {
        var (provider, id) = Route(key);
        return await provider.ParametersAsync(viewer, id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ReportCatalogExport>> ExportsAsync(ReportCatalogViewer viewer, string key, CancellationToken cancellationToken = default)
    {
        var (provider, id) = Route(key);
        return await provider.ExportsAsync(viewer, id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ReportCatalogPermissions> PermissionsAsync(ReportCatalogViewer viewer, string key, CancellationToken cancellationToken = default)
    {
        var (provider, id) = Route(key);
        return await provider.PermissionsAsync(viewer, id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Opens an item through its provider; an opening that frames is checked against the approved origins, and the opening's key is the merged one.</summary>
    public async Task<ReportCatalogOpening> OpenAsync(ReportCatalogViewer viewer, string key, IReadOnlyDictionary<string, string?>? parameters = null, string? layout = null, CancellationToken cancellationToken = default)
    {
        var (provider, id) = Route(key);
        var opening = await provider.OpenAsync(viewer, id, parameters, layout, cancellationToken).ConfigureAwait(false) with { Key = $"{provider.Provider}:{id}" };
        if (opening.Mode == ReportOpenModes.Document) return opening;
        if (opening.Mode is not (ReportOpenModes.Frame or ReportOpenModes.Link))
            throw new ReportCatalogException("unavailable", $"The catalog '{provider.Provider}' opened '{id}' in a way ('{opening.Mode}') that this host does not show.");
        var origin = opening.Url is null ? null : OriginOf(opening.Url);
        if (origin is null)
            throw new ReportCatalogException("invalid", $"The catalog '{provider.Provider}' gave no http or https address to {(opening.Mode == ReportOpenModes.Frame ? "frame" : "follow")} for '{id}'.");
        return opening.Mode == ReportOpenModes.Frame && !frameOrigins.Contains(origin) ? opening with { Mode = ReportOpenModes.Link } : opening;
    }
}
