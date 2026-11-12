using DbDataKit.Views;

namespace DbDataKit.Views.Tests;

/// <summary>A provider of a test: the items it holds, what it opens, and how it fails when it is told to.</summary>
public sealed class FakeCatalogProvider(string name, params ReportCatalogItem[] items) : IReportCatalogProvider
{
    public string Provider => name;

    /// <summary>When set, every call throws it (a report server that is down, a viewer who may not).</summary>
    public Exception? Fails { get; set; }

    public Func<string, ReportCatalogOpening>? Opens { get; set; }

    /// <summary>The kinds this fake makes (create by kind); none by default, as an external report server's provider.</summary>
    public IReadOnlyList<string> CreatableKinds { get; set; } = [];

    public Task<ReportCatalogItem> CreateAsync(ReportCatalogViewer viewer, string kind, string name, string? folder = null, CancellationToken cancellationToken = default)
    {
        Check();
        return Task.FromResult(new ReportCatalogItem($"{kind}/{name}", $"{folder ?? ""}/{name}", name, kind, name));
    }

    public List<string> Folders { get; } = [];

    private void Check() { if (Fails is not null) throw Fails; }

    public Task<IReadOnlyList<ReportCatalogItem>> BrowseAsync(ReportCatalogViewer viewer, ReportCatalogQuery query, CancellationToken cancellationToken = default)
    {
        Check();
        IReadOnlyList<ReportCatalogItem> found = [.. items.Where(i => query.Search is null ? (query.Folder ?? "/") == ParentOf(i.Path) : i.Name.Contains(query.Search, StringComparison.OrdinalIgnoreCase))];
        return Task.FromResult(found);
    }

    private static string ParentOf(string path) => path.LastIndexOf('/') is var slash and > 0 ? path[..slash] : "/";

    public Task<ReportCatalogItem?> FindAsync(ReportCatalogViewer viewer, string id, CancellationToken cancellationToken = default)
    {
        Check();
        return Task.FromResult(items.FirstOrDefault(i => i.Id == id));
    }

    public Task<IReadOnlyList<ReportCatalogParameter>> ParametersAsync(ReportCatalogViewer viewer, string id, CancellationToken cancellationToken = default)
    {
        Check();
        IReadOnlyList<ReportCatalogParameter> list = [new ReportCatalogParameter("Region", "String", ValidValues: [new ReportCatalogOption("East"), new ReportCatalogOption("West", "Western")], DependsOn: ["Country"])];
        return Task.FromResult(list);
    }

    public Task<ReportCatalogOpening> OpenAsync(ReportCatalogViewer viewer, string id, IReadOnlyDictionary<string, string?>? parameters = null, string? layout = null, CancellationToken cancellationToken = default)
    {
        Check();
        return Task.FromResult(Opens!(id));
    }

    public Task<IReadOnlyList<ReportCatalogExport>> ExportsAsync(ReportCatalogViewer viewer, string id, CancellationToken cancellationToken = default)
    {
        Check();
        IReadOnlyList<ReportCatalogExport> list = [new ReportCatalogExport("PDF"), new ReportCatalogExport("EXCELOPENXML")];
        return Task.FromResult(list);
    }

    public Task<ReportCatalogPermissions> PermissionsAsync(ReportCatalogViewer viewer, string id, CancellationToken cancellationToken = default)
    {
        Check();
        return Task.FromResult(new ReportCatalogPermissions(true, ["CORP\\Analysts"]));
    }
}
