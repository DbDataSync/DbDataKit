using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using DbDataKit.Views;

namespace DbDataKit.ReportCatalogs;

/// <summary>Which report server a provider reads: SQL Server Reporting Services, or Power BI Report Server (which also holds Power BI reports).</summary>
public enum ReportServerKind
{
    Ssrs,
    Pbirs,
}

/// <summary>
/// Where a report server is. <see cref="PortalAddress"/> is the web portal (<c>https://reports.example.com/Reports</c>), whose <c>api/v2.0</c> is the REST API; <see cref="ReportServerAddress"/> is the report server's URL access
/// (<c>https://reports.example.com/ReportServer</c>), by default the portal's address with its last segment <c>Reports</c> replaced by <c>ReportServer</c>. <see cref="Provider"/> is the name that prefixes the keys of the items
/// (default <c>ssrs</c> or <c>pbirs</c>).
/// </summary>
public sealed record ReportServerOptions(Uri PortalAddress, ReportServerKind Kind = ReportServerKind.Ssrs, Uri? ReportServerAddress = null, string? Provider = null)
{
    public string ProviderName => Provider ?? (Kind == ReportServerKind.Pbirs ? "pbirs" : "ssrs");

    internal Uri Api => new(PortalAddress.AbsoluteUri.TrimEnd('/') + "/api/v2.0/");

    internal Uri UrlAccess
    {
        get
        {
            if (ReportServerAddress is { } given) return given;
            var portal = PortalAddress.AbsoluteUri.TrimEnd('/');
            var slash = portal.LastIndexOf('/');
            var last = portal[(slash + 1)..];
            return new Uri(last.Equals("Reports", StringComparison.OrdinalIgnoreCase) ? portal[..(slash + 1)] + "ReportServer" : portal + "/ReportServer");
        }
    }
}

/// <summary>
/// The report catalog interface over the REST API of SQL Server Reporting Services and Power BI Report Server (plan 32 RC01). The client is the caller's, signed in as the catalog's service account; every request goes to the portal's
/// <c>api/v2.0</c> and nothing else. The provider lists what the service account sees: the per-person filter is RC03's.
/// </summary>
public sealed class ReportServerCatalogProvider(HttpClient http, ReportServerOptions options) : IReportCatalogProvider
{
    private const int MaxPages = 200;

    private static readonly string[] SsrsFormats = ["PDF", "EXCELOPENXML", "WORDOPENXML", "PPTX", "CSV", "XML", "IMAGE", "MHTML", "ATOM"];

    public string Provider => options.ProviderName;

    // ---- browse ----

    public async Task<IReadOnlyList<ReportCatalogItem>> BrowseAsync(ReportCatalogViewer viewer, ReportCatalogQuery query, CancellationToken cancellationToken = default)
    {
        var all = await ReadItemsAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(query.Search))
            return [.. all.Where(i => Holds(i.Name, query.Search) || Holds(i.Path, query.Search) || (i.Description is not null && Holds(i.Description, query.Search)))];
        var folder = NormalizeFolder(query.Folder);
        if (folder != "/" && !all.Any(i => i.IsFolder && string.Equals(i.Path, folder, StringComparison.OrdinalIgnoreCase)))
            throw new ReportCatalogException("not_found", $"There is no folder '{folder}' on the report server at {options.PortalAddress}.");
        return [.. all.Where(i => string.Equals(ParentOf(i.Path), folder, StringComparison.OrdinalIgnoreCase))];
    }

    private static bool Holds(string text, string search) => text.Contains(search, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeFolder(string? folder) => string.IsNullOrEmpty(folder) || folder == "/" ? "/" : "/" + folder.Trim('/');

    private static string ParentOf(string path) => path.LastIndexOf('/') is var slash and > 0 ? path[..slash] : "/";

    private async Task<List<ReportCatalogItem>> ReadItemsAsync(CancellationToken cancellationToken)
    {
        var items = new List<ReportCatalogItem>();
        foreach (var row in await GetAllAsync("CatalogItems", cancellationToken).ConfigureAwait(false))
            if (ItemOf(row) is { } item) items.Add(item);
        return items;
    }

    private ReportCatalogItem? ItemOf(JsonElement row)
    {
        var kind = Text(row, "Type");
        var type = kind switch
        {
            "Folder" => ReportItemTypes.Folder,
            "Report" or "LinkedReport" or "PowerBIReport" or "Kpi" or "MobileReport" or "ExcelWorkbook" => ReportItemTypes.External,
            _ => null,
        };
        if (type is null || Text(row, "Id") is not { Length: > 0 } id || Text(row, "Path") is not { Length: > 0 } path) return null;
        var modified = Text(row, "ModifiedDate") is { } m && DateTimeOffset.TryParse(m, CultureInfo.InvariantCulture, DateTimeStyles.None, out var when) ? when : (DateTimeOffset?)null;
        return new ReportCatalogItem(id, path, Text(row, "Name") ?? path[(path.LastIndexOf('/') + 1)..], type, Provider, Text(row, "Description") is { Length: > 0 } d ? d : null, Text(row, "ModifiedBy"), modified);
    }

    public async Task<ReportCatalogItem?> FindAsync(ReportCatalogViewer viewer, string id, CancellationToken cancellationToken = default) => (await FetchAsync(id, cancellationToken).ConfigureAwait(false))?.Item;

    /// <summary>An item with the type the server gave it (<c>Report</c>, <c>PowerBIReport</c>, ...), which decides where its parameters are read and how it is opened.</summary>
    private async Task<(ReportCatalogItem Item, string ServerType)?> FetchAsync(string id, CancellationToken cancellationToken)
    {
        var row = await GetAsync($"CatalogItems({Guid(id)})", cancellationToken, missingIsNull: true).ConfigureAwait(false);
        return row is { } r && ItemOf(r) is { } item ? (item, Text(r, "Type") ?? "") : null;
    }

    private async Task<(ReportCatalogItem Item, string ServerType)> RequireAsync(string id, CancellationToken cancellationToken) =>
        await FetchAsync(id, cancellationToken).ConfigureAwait(false) ?? throw new ReportCatalogException("not_found", $"There is no item '{id}' on the report server at {options.PortalAddress}.");

    // ---- parameters ----

    public async Task<IReadOnlyList<ReportCatalogParameter>> ParametersAsync(ReportCatalogViewer viewer, string id, CancellationToken cancellationToken = default)
    {
        var (_, serverType) = await RequireAsync(id, cancellationToken).ConfigureAwait(false);
        // only a paginated report (and a report linked to one) has parameter definitions; a folder, a Power BI report, a KPI or a workbook has none to ask
        if (serverType is not ("Report" or "LinkedReport")) return [];
        var rows = await GetAllAsync($"Reports({Guid(id)})/ParameterDefinitions", cancellationToken).ConfigureAwait(false);
        var list = new List<ReportCatalogParameter>();
        foreach (var p in rows)
        {
            if (Text(p, "Name") is not { Length: > 0 } name) continue;
            var defaults = StringList(p, "DefaultValues");
            var valid = p.TryGetProperty("ValidValues", out var v) && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray().Select(o => new ReportCatalogOption(Text(o, "Value") ?? "", Text(o, "Label"))).ToList()
                : null;
            var nullable = p.TryGetProperty("Nullable", out var n) && n.ValueKind == JsonValueKind.True;
            list.Add(new ReportCatalogParameter(name, Text(p, "ParameterType") ?? "String", Text(p, "Prompt"), p.TryGetProperty("MultiValue", out var many) && many.ValueKind == JsonValueKind.True,
                !nullable && defaults.Count == 0, defaults.Count > 0 ? string.Join(",", defaults) : null, valid is { Count: > 0 } ? valid : null, null, StringList(p, "Dependencies") is { Count: > 0 } dep ? dep : null));
        }
        return list;
    }

    // ---- open ----

    public async Task<ReportCatalogOpening> OpenAsync(ReportCatalogViewer viewer, string id, IReadOnlyDictionary<string, string?>? parameters = null, string? layout = null, CancellationToken cancellationToken = default)
    {
        var (item, serverType) = await RequireAsync(id, cancellationToken).ConfigureAwait(false);
        if (item.IsFolder) throw new ReportCatalogException("invalid", $"'{item.Path}' is a folder: browse it.");
        var known = (await ParametersAsync(viewer, id, cancellationToken).ConfigureAwait(false)).Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var filled = new Dictionary<string, string?>(StringComparer.Ordinal);
        string? format = null;
        foreach (var (name, value) in parameters ?? new Dictionary<string, string?>())
        {
            if (name == ReportCatalogExportFormat.Parameter)
            {
                format = SsrsFormats.FirstOrDefault(f => string.Equals(f, value, StringComparison.OrdinalIgnoreCase));
                if (format is null || serverType is not ("Report" or "LinkedReport"))
                    throw new ReportCatalogException("invalid", $"The item '{item.Path}' is not offered in the format '{value}'.");
                continue;
            }
            if (!known.Contains(name)) throw new ReportCatalogException("invalid", $"The report '{item.Path}' has no parameter '{name}'.");
            filled[name] = value;
        }
        // an export is the file the server renders, which a person follows (a frame would show a download as nothing)
        return new ReportCatalogOpening($"{Provider}:{item.Id}", format is null ? ReportOpenModes.Frame : ReportOpenModes.Link, item.Name, Url: UrlFor(item, serverType, filled, format), Parameters: filled);
    }

    /// <summary>The report server's URL-access address of an item: the path, the render command, the toolbar hidden (the one parameters pane of the suite takes its place) and each parameter by its name (a value that is null is sent as the server's null).</summary>
    internal string UrlFor(ReportCatalogItem item, string serverType, IReadOnlyDictionary<string, string?> parameters, string? format = null)
    {
        var sb = new StringBuilder();
        var server = options.UrlAccess.AbsoluteUri.TrimEnd('/');
        if (serverType == "PowerBIReport")
            return options.PortalAddress.AbsoluteUri.TrimEnd('/') + "/powerbi" + EscapePath(item.Path);
        sb.Append(server).Append('?').Append(EscapePath(item.Path)).Append("&rs:Command=Render").Append(format is null ? "&rc:Toolbar=false" : "&rs:Format=" + Uri.EscapeDataString(format));
        foreach (var (name, value) in parameters)
            sb.Append('&').Append(Uri.EscapeDataString(name)).Append('=').Append(value is null ? "" : Uri.EscapeDataString(value)).Append(value is null ? $"&{Uri.EscapeDataString(name)}:isnull=true" : "");
        return sb.ToString();
    }

    private static string EscapePath(string path) => Uri.EscapeDataString(path).Replace("%2F", "/", StringComparison.Ordinal);

    // ---- exports and permissions ----

    public async Task<IReadOnlyList<ReportCatalogExport>> ExportsAsync(ReportCatalogViewer viewer, string id, CancellationToken cancellationToken = default)
    {
        var (item, serverType) = await RequireAsync(id, cancellationToken).ConfigureAwait(false);
        // a paginated report renders in every format of the server; a Power BI report, a KPI and a workbook are taken as they are, in the portal
        return item.IsFolder || serverType is not ("Report" or "LinkedReport") ? [] : [.. SsrsFormats.Select(f => new ReportCatalogExport(f))];
    }

    public async Task<ReportCatalogPermissions> PermissionsAsync(ReportCatalogViewer viewer, string id, CancellationToken cancellationToken = default)
    {
        await RequireAsync(id, cancellationToken).ConfigureAwait(false);
        JsonElement? row;
        try
        {
            row = await GetAsync($"CatalogItems({Guid(id)})/Policies", cancellationToken, missingIsNull: true).ConfigureAwait(false);
        }
        catch (ReportCatalogException e) when (e.Code == "unavailable" && e.Message.Contains("(HTTP 403)", StringComparison.Ordinal))
        {
            return new ReportCatalogPermissions(false, [], "The service account may not read this item's policies.");
        }
        if (row is not { } r) return new ReportCatalogPermissions(false, [], "The report server does not state this item's policies.");
        var rows = r.TryGetProperty("Policies", out var p) && p.ValueKind == JsonValueKind.Array ? p : r.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array ? v : default;
        if (rows.ValueKind != JsonValueKind.Array) return new ReportCatalogPermissions(false, [], "The report server does not state this item's policies.");
        var principals = rows.EnumerateArray().Select(o => Text(o, "GroupUserName")).Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new ReportCatalogPermissions(true, principals);
    }

    // ---- the wire ----

    private static string Guid(string id) => System.Guid.TryParse(id, out var g) ? g.ToString("D") : throw new ReportCatalogException("invalid", $"'{id}' is not an item id of a report server (a GUID).");

    private async Task<List<JsonElement>> GetAllAsync(string relative, CancellationToken cancellationToken)
    {
        var rows = new List<JsonElement>();
        Uri? next = new(options.Api, relative);
        for (var page = 0; next is not null; page++)
        {
            if (page >= MaxPages) throw new ReportCatalogException("unavailable", $"The report server at {options.PortalAddress} answered more than {MaxPages} pages of '{relative}'.");
            var doc = await SendAsync(next, cancellationToken).ConfigureAwait(false) ?? throw new ReportCatalogException("not_found", $"The report server at {options.PortalAddress} has no '{relative}'.");
            if (doc.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array) rows.AddRange(value.EnumerateArray().Select(e => e.Clone()));
            next = doc.RootElement.TryGetProperty("@odata.nextLink", out var link) && link.ValueKind == JsonValueKind.String && Uri.TryCreate(options.Api, link.GetString(), out var u) && SameOrigin(u) ? u : null;
        }
        return rows;
    }

    private bool SameOrigin(Uri uri) => uri.Scheme == options.PortalAddress.Scheme && uri.Authority == options.PortalAddress.Authority;

    private async Task<JsonElement?> GetAsync(string relative, CancellationToken cancellationToken, bool missingIsNull)
    {
        var doc = await SendAsync(new Uri(options.Api, relative), cancellationToken, missingIsNull).ConfigureAwait(false);
        return doc?.RootElement.Clone();
    }

    private async Task<JsonDocument?> SendAsync(Uri uri, CancellationToken cancellationToken, bool missingIsNull = false)
    {
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd("application/json");
            response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            throw new ReportCatalogException("unavailable", $"The report server at {options.PortalAddress} could not be reached ({e.HttpRequestError}).", e);
        }
        catch (TaskCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ReportCatalogException("unavailable", $"The report server at {options.PortalAddress} did not answer in time.", e);
        }
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound && missingIsNull) return null;
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new ReportCatalogException("unavailable", $"The service account cannot read the report catalog at {options.PortalAddress} (HTTP {(int)response.StatusCode}): check its sign-in and its role on the report server.");
            if (!response.IsSuccessStatusCode)
                throw new ReportCatalogException("unavailable", $"The report server at {options.PortalAddress} answered HTTP {(int)response.StatusCode}.");
            try
            {
                return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException e)
            {
                throw new ReportCatalogException("unavailable", $"The report server at {options.PortalAddress} answered something that is not JSON (is the address the web portal's?).", e);
            }
        }
    }

    private static string? Text(JsonElement row, string name) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out var v) ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.ToString(), _ => null } : null;

    private static List<string> StringList(JsonElement row, string name) =>
        row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? [.. v.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.ValueKind == JsonValueKind.Null ? null : e.ToString()).Where(s => s is not null).Select(s => s!)] : [];
}
