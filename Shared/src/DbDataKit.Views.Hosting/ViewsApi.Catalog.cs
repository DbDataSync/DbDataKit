using System.Globalization;
using System.Text.Json.Nodes;
using DbDataKit.State;

namespace DbDataKit.Views.Hosting;

/// <summary>
/// The merged report catalog over REST (plan 32 RC03, section 9e): one browse over our own dashboards and reports and the external report servers the host configured, each item by its key <c>provider:id</c>, as the person asking (<see cref="ViewsOptions.Catalogs"/>).
/// <code>
/// GET  /api/v1/catalog?folder=&amp;search=&amp;types=   {items: [{key, provider, id, path, name, type, description, owner, modified}], unavailable: [{provider, code, message}]}
/// GET  /api/v1/catalog/item?key=                {item, parameters, exports, permissions}
/// POST /api/v1/catalog/create                   {provider, kind, name, folder?} makes an empty item by kind where the provider can (201 {item}; 501 unsupported otherwise); the listing's <c>creatable</c> says which can
/// POST /api/v1/catalog/open                     {key, parameters?: {name: value}, layout?, format?} gives {key, mode: document|frame|link, title, url?, kind?, name?, layouts?, layout?, parameters?}
/// </code>
/// A person is listed only what the catalogs say they may see (an external one by its snapshot and the person's names on the report server); an item that is not listed cannot be asked about or opened (404 not_found, the same as an item that is not there).
/// A provider that is down shows in <c>unavailable</c> and does not hide the others. <c>format</c> asks an external item's export: the answer is a <c>link</c> to the file the report server renders. Every browse and open is audited (<c>view.list</c>, <c>view.open</c> with
/// <c>catalog</c> and the key; a search text and a parameter's value are never recorded).
/// </summary>
public sealed partial class ViewsApi
{
    private ViewsAnswer? NoCatalogs() => options.Catalogs is null
        ? ViewsAnswer.Error(404, "not_found", "No report catalogs are configured on this instance (the `catalogs` setting of the views module).")
        : null;

    private static ReportCatalogViewer CatalogViewer(ViewsPrincipal who) => ViewsCatalog.Viewer(who);

    private static ViewsAnswer CatalogError(ReportCatalogException e) => ViewsAnswer.Error(e.Code switch
    {
        "not_found" => 404,
        "denied" => 403,
        "invalid" => 400,
        "unsupported" => 501,
        _ => 503,
    }, e.Code, e.Message);

    private static JsonObject ItemJson(ReportCatalogItem i) => new()
    {
        ["key"] = i.Key, ["provider"] = i.Provider, ["id"] = i.Id, ["path"] = i.Path, ["name"] = i.Name, ["type"] = i.Type, ["description"] = i.Description, ["owner"] = i.Owner,
        ["modified"] = i.Modified?.ToString("O", CultureInfo.InvariantCulture),
    };

    public async Task<ViewsAnswer> CatalogBrowseAsync(ViewsPrincipal who, string? folder, string? search, string? types, string? traceId = null, CancellationToken cancellationToken = default)
    {
        if (NoCatalogs() is { } none) return none;
        if (Enter() is not { } work) return Draining();
        using (work)
        {
            var query = new ReportCatalogQuery(folder, search, types?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            var listing = await options.Catalogs!.BrowseAsync(CatalogViewer(who), query, cancellationToken).ConfigureAwait(false);
            var items = new JsonArray([.. listing.Items.Select(i => (JsonNode?)ItemJson(i))]);
            var unavailable = new JsonArray([.. listing.Unavailable.Select(p => (JsonNode?)new JsonObject { ["provider"] = p.Provider, ["code"] = p.Code, ["message"] = p.Message })]);
            return Finish(who, ViewsAuditVerbs.List, "catalog", null, null, AuditEvent.Ok, Detail(traceId, ("catalog", true), ("count", items.Count), ("search", !string.IsNullOrWhiteSpace(search))),
                new ViewsAnswer(200, new JsonObject { ["items"] = items, ["unavailable"] = unavailable, ["creatable"] = new JsonObject(options.Catalogs.CreatableKinds.Select(k => new KeyValuePair<string, JsonNode?>(k.Key, new JsonArray([.. k.Value.Select(v => (JsonNode?)JsonValue.Create(v))]))).ToArray()) }));
        }
    }

    public async Task<ViewsAnswer> CatalogItemAsync(ViewsPrincipal who, string? key, string? traceId = null, CancellationToken cancellationToken = default)
    {
        if (NoCatalogs() is { } none) return none;
        if (string.IsNullOrEmpty(key)) return ViewsAnswer.Error(400, "invalid", "`key` is the item's key: the provider's name, a colon and the item's id.");
        if (Enter() is not { } work) return Draining();
        using (work)
        {
            var viewer = CatalogViewer(who);
            try
            {
                if (await options.Catalogs!.FindAsync(viewer, key, cancellationToken).ConfigureAwait(false) is not { } item) return ViewsAnswer.Error(404, "not_found", $"There is no item '{key}'.");
                var folder = item.IsFolder;
                var parameters = folder ? [] : await options.Catalogs.ParametersAsync(viewer, key, cancellationToken).ConfigureAwait(false);
                var exports = folder ? [] : await options.Catalogs.ExportsAsync(viewer, key, cancellationToken).ConfigureAwait(false);
                var permissions = await options.Catalogs.PermissionsAsync(viewer, key, cancellationToken).ConfigureAwait(false);
                var body = new JsonObject
                {
                    ["item"] = ItemJson(item),
                    ["parameters"] = new JsonArray([.. parameters.Select(p => (JsonNode?)ParameterJson(p))]),
                    ["exports"] = new JsonArray([.. exports.Select(e => (JsonNode?)new JsonObject { ["format"] = e.Format, ["available"] = e.Available, ["reason"] = e.Reason })]),
                    ["permissions"] = new JsonObject { ["known"] = permissions.Known, ["principals"] = new JsonArray([.. permissions.Principals.Select(p => (JsonNode?)JsonValue.Create(p))]), ["note"] = permissions.Note },
                };
                return new ViewsAnswer(200, body);
            }
            catch (ReportCatalogException e)
            {
                return CatalogError(e);
            }
        }
    }

    private static JsonObject ParameterJson(ReportCatalogParameter p) => new()
    {
        ["name"] = p.Name, ["type"] = p.Type, ["label"] = p.Label, ["many"] = p.Many, ["required"] = p.Required, ["default"] = p.Default, ["values_from"] = p.ValuesFrom,
        ["valid_values"] = p.ValidValues is null ? null : new JsonArray([.. p.ValidValues.Select(v => (JsonNode?)new JsonObject { ["value"] = v.Value, ["label"] = v.Label })]),
        ["depends_on"] = p.DependsOn is null ? null : new JsonArray([.. p.DependsOn.Select(d => (JsonNode?)JsonValue.Create(d))]),
    };

    public async Task<ViewsAnswer> CatalogOpenAsync(ViewsPrincipal who, JsonObject? body, string? traceId = null, CancellationToken cancellationToken = default)
    {
        if (NoCatalogs() is { } none) return none;
        if (body?["key"]?.GetValue<string>() is not { Length: > 0 } key) return ViewsAnswer.Error(400, "invalid", "The body is `{key, parameters?, layout?, format?}`; `key` is the item's key.");
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (body["parameters"] is JsonObject given)
            foreach (var (name, node) in given)
                values[name] = node switch
                {
                    null => null,
                    JsonArray list => string.Join(",", list.Select(v => v?.ToString())),
                    _ => node.ToString(),
                };
        if (body["format"]?.GetValue<string>() is { Length: > 0 } format) values[ReportCatalogExportFormat.Parameter] = format;
        if (Enter() is not { } work) return Draining();
        using (work)
        {
            try
            {
                var opening = await options.Catalogs!.OpenAsync(CatalogViewer(who), key, values, body["layout"]?.GetValue<string>(), cancellationToken).ConfigureAwait(false);
                var answer = new JsonObject
                {
                    ["key"] = opening.Key, ["mode"] = opening.Mode, ["title"] = opening.Title, ["url"] = opening.Url, ["kind"] = opening.Kind, ["name"] = opening.Name, ["version"] = opening.Version,
                    ["layout"] = opening.Layout, ["layouts"] = opening.Layouts is null ? null : new JsonArray([.. opening.Layouts.Select(l => (JsonNode?)JsonValue.Create(l))]),
                    ["parameters"] = opening.Parameters is null ? null : new JsonObject(opening.Parameters.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, p.Value)).ToArray()),
                };
                // an external item is recorded here (ours records its own open); the key and the names of the parameters, never a value
                if (opening.Mode != ReportOpenModes.Document)
                    Record(who, ViewsAuditVerbs.Open, "catalog", key, null, AuditEvent.Ok, Detail(traceId, ("catalog", true), ("mode", opening.Mode), ("parameters", string.Join(",", values.Keys.Order(StringComparer.Ordinal)))));
                return new ViewsAnswer(200, answer);
            }
            catch (ReportCatalogException e)
            {
                if (e.Code is "not_found" or "denied") Record(who, ViewsAuditVerbs.Open, "catalog", key, null, AuditEvent.Refused, Detail(traceId, ("catalog", true), ("code", e.Code)));
                return CatalogError(e);
            }
        }
    }

    public async Task<ViewsAnswer> CatalogCreateAsync(ViewsPrincipal who, JsonObject? body, string? traceId = null, CancellationToken cancellationToken = default)
    {
        if (NoCatalogs() is { } none) return none;
        if (body?["provider"]?.GetValue<string>() is not { Length: > 0 } provider || body["kind"]?.GetValue<string>() is not { Length: > 0 } kind || body["name"]?.GetValue<string>() is not { Length: > 0 } name)
            return ViewsAnswer.Error(400, "invalid", "The body is `{provider, kind, name, folder?}`.");
        if (Enter() is not { } work) return Draining();
        using (work)
        {
            try
            {
                var item = await options.Catalogs!.CreateAsync(CatalogViewer(who), provider, kind, name, body["folder"]?.GetValue<string>(), cancellationToken).ConfigureAwait(false);
                return Finish(who, ViewsAuditVerbs.Open, "catalog", item.Key, null, AuditEvent.Ok, Detail(traceId, ("catalog", true), ("created", kind)), new ViewsAnswer(201, new JsonObject { ["item"] = ItemJson(item) }));
            }
            catch (ReportCatalogException e)
            {
                return CatalogError(e);
            }
        }
    }
}
