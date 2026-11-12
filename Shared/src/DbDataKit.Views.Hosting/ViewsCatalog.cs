using System.Globalization;
using System.Text.Json.Nodes;
using DbDataKit.Apps;
using DbDataKit.State;

namespace DbDataKit.Views.Hosting;

/// <summary>
/// Our own dashboards and reports as a provider of the report catalog (plan 32 DBR19, section 9e): the published documents of both kinds that this instance serves, under two folders (<c>/Dashboards</c> and <c>/Reports</c>), browsed and searched,
/// with their parameters, opened (the document to render, the layouts it has, the parameters checked against its own), the formats they can be taken in and what the catalog says about who may see them. It reads the same published versions the routes of
/// <see cref="ViewsApi"/> serve and holds to the same rules: an app's token narrows it to the models the app's manifest names, a document the viewer may not open is not listed, and every browse and open is audited (<c>view.list</c> and <c>view.open</c>, with
/// <c>catalog</c> in the detail; a search text and a parameter's value are never recorded). Ids are <c>dashboard/&lt;name&gt;</c> and <c>report/&lt;name&gt;</c>, folders <c>folder/dashboards</c> and <c>folder/reports</c>; the key across providers is <c>views:&lt;id&gt;</c>.
/// <para>
/// The catalog does not render: what a person sees of an opened document is the views module's own render, export and print as that person. <b>Owner</b> is empty (the published version does not carry who published it into the serving copy), and the
/// permissions are stated as unknown, because who may see a document is decided when it is rendered, by the viewer's roles and attributes on every query.
/// </para>
/// </summary>
public sealed class ViewsCatalog(ViewsPublications published, ViewsOptions options, IViewsAudit audit) : IReportCatalogProvider
{
    public const string Name = "views";

    private const string DashboardsFolder = "/Dashboards";
    private const string ReportsFolder = "/Reports";
    private const string PagesFolder = "/Pages";

    public string Provider => Name;

    /// <summary>The viewer a principal of the module is for the catalog (the person, and the app's narrowing).</summary>
    public static ReportCatalogViewer Viewer(ViewsPrincipal who) => new(who.Caller, who.App?.Instance, who.App?.Models);

    private static string FolderPath(string kind) => kind == ViewDefinitions.Report ? ReportsFolder : kind == ViewsApi.PageKind ? PagesFolder : DashboardsFolder;

    private static string ItemType(string kind) => kind == ViewDefinitions.Report ? ReportItemTypes.Report : kind == ViewsApi.PageKind ? ReportItemTypes.Page : ReportItemTypes.Dashboard;

    private static ReportCatalogItem FolderItem(string kind) =>
        kind == ViewsApi.PageKind
            ? new("folder/pages", PagesFolder, "Pages", ReportItemTypes.Folder, Name, Description: "The custom pages of the apps this instance serves: code in an app's own document, opened at the apps host.")
            : new($"folder/{(kind == ViewDefinitions.Report ? "reports" : "dashboards")}", FolderPath(kind), kind == ViewDefinitions.Report ? "Reports" : "Dashboards", ReportItemTypes.Folder, Name,
                Description: kind == ViewDefinitions.Report ? "The reports published to this instance." : "The dashboards published to this instance.");

    // ---- custom pages (plan 32 DBR33): an app's `kind: page` views, listed by their manifest entries ----

    private static string PageId(CustomPageApp p) => $"page/{p.Instance}.{p.View.Name}";

    private static ReportCatalogItem PageItem(CustomPageApp p) =>
        new(PageId(p), $"{PagesFolder}/{p.Instance}.{p.View.Name}", p.View.Title ?? p.View.Name, ReportItemTypes.Page, Name, Description: $"A page of the app '{p.Files.App}' at the instance '{p.Instance}'.");

    /// <summary>The pages this viewer may see: those of instances they may open (an app's token only its own instance). Nothing when the host serves no apps.</summary>
    private IReadOnlyList<CustomPageApp> VisiblePages(ReportCatalogViewer viewer)
    {
        if (options.CustomApps is not { } apps) return [];
        var who = new ViewsPrincipal(viewer.Caller);
        return [.. apps.Pages().Where(p => (viewer.App is null || string.Equals(viewer.App, p.Instance, StringComparison.Ordinal)) && apps.MayOpen(who, p.Instance))];
    }

    private CustomPageApp? PageOf(ReportCatalogViewer viewer, string id) => id.StartsWith("page/", StringComparison.Ordinal) ? VisiblePages(viewer).FirstOrDefault(p => PageId(p) == id) : null;

    private static string PageFormatsReason(ViewsOptions o) =>
        o.Exporter is null ? "export unavailable: it is off on this host (the views.export setting is not set)" : o.Exporter.Availability.Available ? "" : o.Exporter.Availability.Reason ?? "";

    private static ReportCatalogItem ItemOf(PublishedView view) =>
        new($"{view.Kind}/{view.Name}", $"{FolderPath(view.Kind)}/{view.Name}", view.Document.Title, ItemType(view.Kind), Name, Modified: view.PublishedAt, Version: view.Version.ToString(CultureInfo.InvariantCulture));

    private static bool Allowed(ReportCatalogViewer viewer, PublishedView view) =>
        viewer.App is null || ViewDefinitions.Models(view.Document).Concat(ViewDefinitions.PinnedModels(view.DependsOn).Keys).Distinct(StringComparer.OrdinalIgnoreCase).All(viewer.AllowsModel);

    private IEnumerable<PublishedView> Visible(ReportCatalogViewer viewer, string kind) => published.For(kind).All.Where(v => Allowed(viewer, v));

    /// <summary>The kind a folder path stands for (<c>/Dashboards</c>, <c>/Reports</c>, case ignored, a trailing slash allowed), or null for the root; a folder that is not there is a <c>not_found</c>.</summary>
    private string? KindOfFolder(string? folder)
    {
        var path = (folder ?? "").Trim().TrimEnd('/');
        if (path.Length == 0) return null;
        if (string.Equals(path, DashboardsFolder, StringComparison.OrdinalIgnoreCase)) return ViewDefinitions.Dashboard;
        if (string.Equals(path, ReportsFolder, StringComparison.OrdinalIgnoreCase)) return ViewDefinitions.Report;
        if (string.Equals(path, PagesFolder, StringComparison.OrdinalIgnoreCase)) return ViewsApi.PageKind;
        throw new ReportCatalogException("not_found", $"There is no folder '{folder}' in the catalog 'views': its folders are '{DashboardsFolder}'{(options.CustomApps is null ? " and " : ", ")}'{ReportsFolder}'{(options.CustomApps is null ? "" : $" and '{PagesFolder}'")}.");
    }

    // ---- browse ----

    public Task<IReadOnlyList<ReportCatalogItem>> BrowseAsync(ReportCatalogViewer viewer, ReportCatalogQuery query, CancellationToken cancellationToken = default)
    {
        var kind = KindOfFolder(query.Folder);
        var searching = !string.IsNullOrWhiteSpace(query.Search);
        var items = new List<ReportCatalogItem>();
        if (!searching && kind is null)
        {
            // the root: the two folders
            if (query.Wants(ReportItemTypes.Folder)) items.AddRange(ViewDefinitions.Kinds.Select(FolderItem));
            if (query.Wants(ReportItemTypes.Folder) && VisiblePages(viewer).Count > 0) items.Add(FolderItem(ViewsApi.PageKind));
        }
        else
        {
            foreach (var each in kind is null ? [.. ViewDefinitions.Kinds, ViewsApi.PageKind] : new[] { kind })
                foreach (var item in each == ViewsApi.PageKind ? VisiblePages(viewer).Select(PageItem) : Visible(viewer, each).Select(ItemOf))
                {
                    if (!query.Wants(item.Type)) continue;
                    if (searching && !Matches(item, query.Search!.Trim())) continue;
                    items.Add(item);
                }
        }
        Record(viewer, ViewsAuditVerbs.List, kind ?? "catalog", null, null, AuditEvent.Ok, new JsonObject { ["catalog"] = true, ["count"] = items.Count, ["search"] = searching });
        return Task.FromResult<IReadOnlyList<ReportCatalogItem>>(items);
    }

    private static bool Matches(ReportCatalogItem item, string text) =>
        item.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || item.Path.Contains(text, StringComparison.OrdinalIgnoreCase) || (item.Description?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false);

    // ---- one item ----

    /// <summary>What an id is: a folder's kind, or a document the viewer may open; null when there is none (a document the viewer may not open looks the same as a missing one).</summary>
    private (string Kind, PublishedView? View)? Resolve(ReportCatalogViewer viewer, string id)
    {
        var slash = id.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0) return null;
        var head = id[..slash];
        var rest = id[(slash + 1)..];
        if (head == "folder")
            return rest switch { "dashboards" => (ViewDefinitions.Dashboard, null), "reports" => (ViewDefinitions.Report, null), "pages" => (ViewsApi.PageKind, null), _ => null };
        if (!ViewDefinitions.Kinds.Contains(head, StringComparer.Ordinal)) return null;
        return published.For(head).Find(rest) is { } view && Allowed(viewer, view) ? (head, view) : null;
    }

    private PublishedView Document(ReportCatalogViewer viewer, string id, string what)
    {
        if (Resolve(viewer, id) is not { } found) throw new ReportCatalogException("not_found", $"The catalog 'views' has no item '{id}'.");
        return found.View ?? throw new ReportCatalogException("invalid", $"'{id}' is a folder: it has no {what}; browse it with `folder={FolderPath(found.Kind)}`.");
    }

    public Task<ReportCatalogItem?> FindAsync(ReportCatalogViewer viewer, string id, CancellationToken cancellationToken = default) =>
        Task.FromResult<ReportCatalogItem?>(PageOf(viewer, id) is { } page ? PageItem(page) : Resolve(viewer, id) is { } found ? found.View is { } view ? ItemOf(view) : FolderItem(found.Kind) : null);

    public Task<IReadOnlyList<ReportCatalogParameter>> ParametersAsync(ReportCatalogViewer viewer, string id, CancellationToken cancellationToken = default)
    {
        if (PageOf(viewer, id) is { } page)
            return Task.FromResult<IReadOnlyList<ReportCatalogParameter>>([.. page.View.Params.Select(p => new ReportCatalogParameter(p.Name, p.Type, null, p.Many, p.Required, null))]);
        var view = Document(viewer, id, "parameters");
        IReadOnlyList<ReportCatalogParameter> list = [.. view.Document.Parameters.Select(p =>
            new ReportCatalogParameter(p.Name, p.Type, p.Label, p.Many, p.Required, p.HasDefault ? AsText(p.Default) : null, ValuesFrom: p.For))];
        return Task.FromResult(list);
    }

    private static string? AsText(JsonNode? value) => value is JsonValue v && v.TryGetValue<string>(out var s) ? s : value?.ToJsonString();

    public Task<ReportCatalogOpening> OpenAsync(ReportCatalogViewer viewer, string id, IReadOnlyDictionary<string, string?>? parameters = null, string? layout = null, CancellationToken cancellationToken = default)
    {
        if (PageOf(viewer, id) is { } page) return Task.FromResult(OpenPage(viewer, page, id, parameters, layout));
        var view = Document(viewer, id, "document");
        var layouts = view.Document.Layouts.Select(l => l.Name).ToList();
        if (layout is not null && !layouts.Contains(layout, StringComparer.Ordinal))
            throw new ReportCatalogException("invalid", $"The {view.Kind} '{view.Name}' has no layout '{layout}': its layouts are {string.Join(", ", layouts.Select(l => $"'{l}'"))}.");
        var known = view.Document.Parameters.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var unknown = (parameters?.Keys ?? []).Where(k => !known.Contains(k)).Order(StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
            throw new ReportCatalogException("invalid", $"The {view.Kind} '{view.Name}' has no parameter {string.Join(", ", unknown.Select(u => $"'{u}'"))}: its parameters are {(known.Count == 0 ? "none" : string.Join(", ", known.Order(StringComparer.Ordinal).Select(k => $"'{k}'")))}.");
        Record(viewer, ViewsAuditVerbs.Open, view.Kind, view.Name, view.Version, AuditEvent.Ok, new JsonObject
        {
            ["catalog"] = true, ["layout"] = layout, ["parameters"] = new JsonArray((parameters?.Keys ?? []).Order(StringComparer.Ordinal).Select(k => (JsonNode?)JsonValue.Create(k)).ToArray()),
        });
        var opening = new ReportCatalogOpening($"{Name}:{id}", ReportOpenModes.Document, view.Document.Title, view.Kind, view.Name, view.Version.ToString(CultureInfo.InvariantCulture), layouts, layout, Parameters: parameters);
        return Task.FromResult(opening);
    }

    /// <summary>A page opens as a <b>link</b>: its address on the apps host with the parameters in the query string (a page has no layouts; the apps host asks the person to sign in as it does for the app).</summary>
    private ReportCatalogOpening OpenPage(ReportCatalogViewer viewer, CustomPageApp page, string id, IReadOnlyDictionary<string, string?>? parameters, string? layout)
    {
        if (layout is not null) throw new ReportCatalogException("invalid", $"The page '{page.View.Name}' has no layouts: it is code in the app's own document.");
        var known = page.View.Params.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var unknown = (parameters?.Keys ?? []).Where(k => !known.Contains(k)).Order(StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
            throw new ReportCatalogException("invalid", $"The page '{page.View.Name}' has no parameter {string.Join(", ", unknown.Select(u => $"'{u}'"))}: its parameters are {(known.Count == 0 ? "none" : string.Join(", ", known.Select(k => $"'{k}'")))}.");
        var address = options.CustomApps?.PageAddress(page.Instance, page.View.Name)
            ?? throw new ReportCatalogException("unsupported", $"This host does not know where its apps are served, so the page '{page.View.Name}' cannot be opened from here: open it at the apps host, /apps/{page.Instance}/_views/{page.View.Name}.");
        var query = string.Join('&', (parameters ?? new Dictionary<string, string?>()).Where(p => p.Value is not null).OrderBy(p => p.Key, StringComparer.Ordinal)
            .SelectMany(p => p.Value!.Split(',').Select(v => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(v)}")));
        Record(viewer, ViewsAuditVerbs.Open, ViewsApi.PageKind, $"{page.Instance}.{page.View.Name}", null, AuditEvent.Ok, new JsonObject
        {
            ["catalog"] = true, ["parameters"] = new JsonArray((parameters?.Keys ?? []).Order(StringComparer.Ordinal).Select(k => (JsonNode?)JsonValue.Create(k)).ToArray()),
        });
        return new ReportCatalogOpening($"{Name}:{id}", ReportOpenModes.Link, page.View.Title ?? page.View.Name, ViewsApi.PageKind, $"{page.Instance}.{page.View.Name}", Url: query.Length == 0 ? address : address + "?" + query, Parameters: parameters);
    }

    public Task<IReadOnlyList<ReportCatalogExport>> ExportsAsync(ReportCatalogViewer viewer, string id, CancellationToken cancellationToken = default)
    {
        if (PageOf(viewer, id) is { } page)
        {
            var why = PageFormatsReason(options);
            return Task.FromResult<IReadOnlyList<ReportCatalogExport>>([.. (page.View.Formats.Count == 0 ? ViewsApi.PageFormats : page.View.Formats).Select(f => new ReportCatalogExport(f, why.Length == 0, why.Length == 0 ? null : why))]);
        }
        Document(viewer, id, "exports");
        var exporter = options.Exporter;
        var browser = exporter?.Availability;
        var reason = exporter is null ? "export unavailable: it is off on this host (the views.export setting is not set)" : browser!.Available ? null : browser.Reason;
        IReadOnlyList<ReportCatalogExport> formats = [.. ViewsApi.Formats.Select(f => f is "pdf" or "svg" ? new ReportCatalogExport(f, reason is null, reason) : new ReportCatalogExport(f))];
        return Task.FromResult(formats);
    }

    public Task<ReportCatalogPermissions> PermissionsAsync(ReportCatalogViewer viewer, string id, CancellationToken cancellationToken = default)
    {
        if (PageOf(viewer, id) is null && Resolve(viewer, id) is null) throw new ReportCatalogException("not_found", $"The catalog 'views' has no item '{id}'.");   // a folder has them too: the snapshot reader (RC02) asks for every entry
        return Task.FromResult(new ReportCatalogPermissions(false, [], "Who may see it is decided when it is rendered: the viewer's roles and attributes decide the rows of every widget, and an app's manifest narrows the models it may use."));
    }

    private void Record(ReportCatalogViewer viewer, string verb, string kind, string? name, int? version, string outcome, JsonObject detail)
    {
        try
        {
            audit.Record(new AuditEvent("person", viewer.Caller.Id, verb, outcome, ViaKind: viewer.App is null ? "api" : "app", ViaId: viewer.App,
                TargetKind: kind, TargetId: name is null ? null : version is { } v ? $"{name}@{v}" : name, Detail: detail.ToJsonString()));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            throw new ReportCatalogException("unavailable", "The audit entry could not be written, so nothing is given.", e);
        }
    }
}
