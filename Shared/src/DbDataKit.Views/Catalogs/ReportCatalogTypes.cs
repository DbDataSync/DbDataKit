using DbDataKit.Serving;

namespace DbDataKit.Views;

/// <summary>What an item of a report catalog is: a folder, one of our own documents, or a report of an external report server.</summary>
public static class ReportItemTypes
{
    public const string Folder = "folder";
    public const string Dashboard = "dashboard";
    public const string Report = "report";

    /// <summary>A report of an external report server (SSRS, Power BI Report Server): opened by address, in a frame or as a link.</summary>
    public const string External = "external-report";
}

/// <summary>How an opened item is shown: <c>document</c> (ours: the shared visuals render it as the viewer), <c>frame</c> (an external report in a frame, only from an approved origin) or <c>link</c> (an address to follow).</summary>
public static class ReportOpenModes
{
    public const string Document = "document";
    public const string Frame = "frame";
    public const string Link = "link";
}

/// <summary>
/// Who is asking a catalog (plan 32 section 9e): the serving core's caller (identity, roles, attributes) and, for an app's token, the app instance and the semantic models its manifest names, which narrow what the app may list and open
/// whatever the person may do. A provider answers for this person and no one else.
/// </summary>
public sealed record ReportCatalogViewer(ServingCaller Caller, string? App = null, IReadOnlyCollection<string>? AppModels = null)
{
    /// <summary>Whether an app's narrowing allows a model; true when the viewer is no app.</summary>
    public bool AllowsModel(string model) => App is null || (AppModels ?? []).Contains(model, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// One entry of a catalog. <see cref="Id"/> is the provider's own and stable (ours: <c>dashboard/sales</c>); <see cref="Path"/> is where it sits (<c>/Dashboards/sales</c>); <see cref="Key"/> is the id across providers (<c>views:dashboard/sales</c>), which the
/// merged catalog fills in. A folder has <see cref="ReportItemTypes.Folder"/> for its type and the path its children are browsed under.
/// </summary>
public sealed record ReportCatalogItem(string Id, string Path, string Name, string Type, string Provider, string? Description = null, string? Owner = null, DateTimeOffset? Modified = null, string? Version = null)
{
    public bool IsFolder => Type == ReportItemTypes.Folder;

    public string Key => $"{Provider}:{Id}";
}

/// <summary>What a person asks to see: the items directly under <see cref="Folder"/> (the root when null), or, with <see cref="Search"/>, every item whose name, path or description holds the text (case ignored), whatever its folder; <see cref="Types"/> narrows by item type.</summary>
public sealed record ReportCatalogQuery(string? Folder = null, string? Search = null, IReadOnlyCollection<string>? Types = null)
{
    public bool Wants(string type) => Types is null || Types.Count == 0 || Types.Contains(type, StringComparer.Ordinal);
}

/// <summary>A provider that could not answer: its name and why in words (the others' items are still given).</summary>
public sealed record ReportCatalogProblem(string Provider, string Code, string Message);

/// <summary>The answer to a browse: the items in provider order and then path order, and the providers that did not answer.</summary>
public sealed record ReportCatalogListing(IReadOnlyList<ReportCatalogItem> Items, IReadOnlyList<ReportCatalogProblem> Unavailable);

/// <summary>A value a parameter may take, as the catalog lists it: the value sent and the text shown.</summary>
public sealed record ReportCatalogOption(string Value, string? Label = null);

/// <summary>
/// A parameter of an item: its <see cref="Type"/> (the provider's words: ours are the document's parameter types), whether it takes several values, whether it needs one, its default as text, the values it may take when the catalog lists them
/// (<see cref="ValidValues"/>), the attribute the values come from when they are the data's (<see cref="ValuesFrom"/>, ours), and the parameters whose values change this one's (<see cref="DependsOn"/>).
/// </summary>
public sealed record ReportCatalogParameter(string Name, string Type, string? Label = null, bool Many = false, bool Required = false, string? Default = null,
    IReadOnlyList<ReportCatalogOption>? ValidValues = null, string? ValuesFrom = null, IReadOnlyList<string>? DependsOn = null);

/// <summary>A format an item can be taken in (<c>pdf</c>, <c>xlsx</c>, <c>csv</c>, <c>svg</c> for ours; an external server's own), and whether it can be taken now (a host without a browser cannot make a PDF: <see cref="Reason"/> says why).</summary>
public sealed record ReportCatalogExport(string Format, bool Available = true, string? Reason = null);

/// <summary>
/// Who may see an item, as far as the catalog says (section 9e: a catalog that exposes no policies says so with <see cref="Known"/> false, and is shown to the groups an administrator names). Ours is decided by what the viewer may read when it is rendered:
/// <see cref="Known"/> is false and <see cref="Note"/> says that. <see cref="Principals"/> are the users and groups an external catalog names, as it writes them.
/// </summary>
public sealed record ReportCatalogPermissions(bool Known, IReadOnlyList<string> Principals, string? Note = null);

/// <summary>
/// An item opened for a viewer. Ours (<see cref="ReportOpenModes.Document"/>): <see cref="Kind"/> and <see cref="Name"/> say which published document to render, <see cref="Layouts"/> the layouts it has and <see cref="Layout"/> the one asked for
/// (null: the viewer's own choice). An external one: <see cref="Url"/> with the parameters filled in, shown as <see cref="Mode"/> (<c>frame</c> only when the origin is approved, else <c>link</c>).
/// </summary>
public sealed record ReportCatalogOpening(string Key, string Mode, string Title, string? Kind = null, string? Name = null, string? Version = null, IReadOnlyList<string>? Layouts = null, string? Layout = null,
    string? Url = null, IReadOnlyDictionary<string, string?>? Parameters = null);

/// <summary>Why a catalog could not do what was asked: <c>not_found</c>, <c>denied</c> (the viewer may not), <c>invalid</c> (the request, in words), <c>unavailable</c> (the provider cannot be reached or is draining).</summary>
public sealed class ReportCatalogException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}

/// <summary>
/// The catalog interface (plan 32 section 9e): what a source of reports answers, the same for our own dashboards and reports (<c>DbDataKit.Views.Hosting.ViewsCatalog</c>) and for an external report server (the later provider of RC01): <b>browse</b> (folders and items), the
/// <b>parameters</b> of an item, <b>open</b> (ours: the document to render; external: the address to frame or follow, with the parameters filled in), the <b>export</b> formats offered, and the <b>permissions</b> the catalog states.
/// Every call is for one <see cref="ReportCatalogViewer"/>: a provider never lists or opens what that person may not. A provider throws <see cref="ReportCatalogException"/> for a refusal in words.
/// </summary>
public interface IReportCatalogProvider
{
    /// <summary>The provider's name, the prefix of every key it gives (<c>views</c> for ours).</summary>
    string Provider { get; }

    /// <summary>The items under a folder or matching a search; a folder that is not there is a <c>not_found</c>.</summary>
    Task<IReadOnlyList<ReportCatalogItem>> BrowseAsync(ReportCatalogViewer viewer, ReportCatalogQuery query, CancellationToken cancellationToken = default);

    /// <summary>The item with the provider's id, or null when there is none the viewer may see (a refusal and a missing item look the same to the viewer).</summary>
    Task<ReportCatalogItem?> FindAsync(ReportCatalogViewer viewer, string id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ReportCatalogParameter>> ParametersAsync(ReportCatalogViewer viewer, string id, CancellationToken cancellationToken = default);

    /// <summary>Opens an item. <paramref name="parameters"/> are values by parameter name (text; several values are a list); <paramref name="layout"/> names one of our layouts and means nothing to an external item.</summary>
    Task<ReportCatalogOpening> OpenAsync(ReportCatalogViewer viewer, string id, IReadOnlyDictionary<string, string?>? parameters = null, string? layout = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ReportCatalogExport>> ExportsAsync(ReportCatalogViewer viewer, string id, CancellationToken cancellationToken = default);

    Task<ReportCatalogPermissions> PermissionsAsync(ReportCatalogViewer viewer, string id, CancellationToken cancellationToken = default);
}
