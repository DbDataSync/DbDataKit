using System.Globalization;
using System.Text.RegularExpressions;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Apps;

public static partial class AppManifestLoader
{
    /// <summary>A custom view that is not well formed (an unknown key, a bad name or kind, a query that is not a form of the vocabulary).</summary>
    public const string BadCustomView = "DBD-APP-019";
    /// <summary>A custom widget that declares no query: the host has nothing to run for it, and the code may not fetch data of its own on the paths that print or email.</summary>
    public const string CustomViewNoQuery = "DBD-APP-020";
    /// <summary>A declared query that names a model the manifest does not list (a semantic model under <c>semantic</c>, a Store model and entity under <c>models</c> with <c>read</c>), or a member the model does not have.</summary>
    public const string CustomViewQueryModel = "DBD-APP-021";
    /// <summary>An <c>export</c> that no <c>defineWidget</c> or <c>definePage</c> in the app's source registers.</summary>
    public const string CustomViewNotRegistered = "DBD-APP-022";

    /// <summary>Warning: the app lists a dashboard or a report and this tool has no loader for them, so the file was not checked.</summary>
    public const string ViewDocumentNotChecked = "DBD-APP-023";
    /// <summary>A dashboard or report that queries a semantic model the manifest does not list under <c>semantic</c>, or a Store entity it does not list with <c>read</c>.</summary>
    public const string ViewDocumentModel = "DBD-APP-024";
    /// <summary>A custom widget whose component is not a widget of this app, or whose parameters are not the ones the manifest declares.</summary>
    public const string ViewDocumentComponent = "DBD-APP-025";

    public static readonly string[] CustomViewKeys = ["name", "kind", "export", "title", "params", "queries", "formats", "page"];
    public static readonly string[] CustomViewFormats = ["pdf", "png"];
    public static readonly string[] CustomViewPageSizes = ["A3", "A4", "A5", "Letter", "Legal"];
    private static readonly Regex ExportRule = new("^[A-Za-z][A-Za-z0-9_]{0,63}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private sealed partial class Reader
    {
        private readonly List<CustomView> customViews = [];

        private void ReadCustomView(YamlMapping map)
        {
            CheckKeys(map, CustomViewKeys, "a custom view");
            var name = map.Get("name") is YamlScalar n && NameRule.IsMatch(n.Value) ? n.Value : null;
            if (name is null) Add(BadCustomView, (YamlNode?)map.Get("name") ?? map, "A custom view has a `name`: lower-case letters, digits, '-' and '_', starting with a letter or digit (at most 64); it addresses the view.");
            var kind = map.Get("kind") switch { YamlScalar { Value: "widget" } => (CustomViewKind?)CustomViewKind.Widget, YamlScalar { Value: "page" } => CustomViewKind.Page, _ => null };
            if (kind is null) Add(BadCustomView, (YamlNode?)map.Get("kind") ?? map, "A custom view says its `kind`: `widget` (placed in a dashboard or report) or `page` (a whole dashboard or report as code).");
            var export = map.Get("export") as YamlScalar;
            if (export is null || !ExportRule.IsMatch(export.Value)) { Add(BadCustomView, (YamlNode?)map.Get("export") ?? map, "A custom view names its `export`: the name its code registers with `defineWidget` or `definePage` (letters, digits and '_', starting with a letter)."); export = null; }
            var title = map.Get("title") is YamlScalar t ? t.Value : null;

            var parameters = new List<CustomViewParam>();
            if (map.Get("params") is { } pn)
            {
                if (pn is not YamlSequence list) Add(BadCustomView, pn, "`params` is a list of `{name, type, required, many, default}`.");
                else foreach (var item in list.Items) if (ReadParam(item) is { } p) { if (parameters.Any(x => x.Name == p.Name)) Add(BadCustomView, item, $"The parameter `{p.Name}` is declared twice."); else parameters.Add(p); }
            }

            var queries = new List<CustomViewQuery>();
            if (map.Get("queries") is { } qn)
            {
                if (qn is not YamlMapping qm) Add(BadCustomView, qn, "`queries` is a mapping of a name to a query: `orders: {semantic: {model: sales, request: {...}}}`.");
                else foreach (var e in qm.Entries) if (ReadQuery(e) is { } q) queries.Add(q);
            }

            var formats = new List<string>();
            if (map.Get("formats") is { } fn)
            {
                if (kind == CustomViewKind.Widget) Add(BadCustomView, fn, "`formats` belong to a page: a widget is exported with the dashboard or report that places it.");
                else if (fn is not YamlSequence fl) Add(BadCustomView, fn, "`formats` is a list: pdf, png.");
                else foreach (var item in fl.Items) if (item is YamlScalar s && CustomViewFormats.Contains(s.Value)) { if (!formats.Contains(s.Value)) formats.Add(s.Value); } else Add(BadCustomView, item, $"A format is one of: {string.Join(", ", CustomViewFormats)}.");
            }
            string? size = null, orientation = null;
            if (map.Get("page") is { } pg)
            {
                if (kind == CustomViewKind.Widget) Add(BadCustomView, pg, "`page` belongs to a page view: a widget has the box its layout gives it.");
                else if (pg is not YamlMapping pm) Add(BadCustomView, pg, "`page` is `{size, orientation}`.");
                else
                {
                    CheckKeys(pm, ["size", "orientation"], "`page`");
                    if (pm.Get("size") is { } sz) { if (sz is YamlScalar s && CustomViewPageSizes.Contains(s.Value)) size = s.Value; else Add(BadCustomView, sz, $"A page size is one of: {string.Join(", ", CustomViewPageSizes)}."); }
                    if (pm.Get("orientation") is { } o) { if (o is YamlScalar s && s.Value is "portrait" or "landscape") orientation = s.Value; else Add(BadCustomView, o, "An orientation is portrait or landscape."); }
                }
            }
            if (name is null || kind is null || export is null) return;
            if (customViews.Any(v => v.Name == name)) { Add(BadCustomView, map.Get("name")!, $"The custom view `{name}` is declared twice."); return; }
            if (customViews.Any(v => v.Export == export.Value)) { Add(BadCustomView, export, $"The export `{export.Value}` is registered by two custom views."); return; }
            customViews.Add(new CustomView(name, kind.Value, export.Value, title, parameters, queries, formats, size, orientation, new SpecPos(map.Line, map.Column), new SpecPos(export.Line, export.Column)));
        }

        private CustomViewParam? ReadParam(YamlNode node)
        {
            if (node is not YamlMapping m) { Add(BadCustomView, node, "A parameter is a mapping: `{name: region, type: string}`."); return null; }
            CheckKeys(m, ["name", "type", "many", "required", "default"], "a custom view's parameter");
            var name = m.Get("name") is YamlScalar n && Regex.IsMatch(n.Value, "^[A-Za-z][A-Za-z0-9_]{0,63}$") ? n.Value : null;
            if (name is null) { Add(BadCustomView, (YamlNode?)m.Get("name") ?? m, "A parameter has a `name`: letters, digits and '_', starting with a letter."); return null; }
            var type = m.Get("type") is YamlScalar { Value: var v } && v is "string" or "integer" or "number" or "boolean" ? v : null;
            if (type is null) { Add(BadCustomView, (YamlNode?)m.Get("type") ?? m, $"The parameter `{name}` has a `type`: string, integer, number or boolean."); return null; }
            bool Flag(string key) => m.Get(key) is YamlScalar { Value: "true" };
            return new CustomViewParam(name, type, Flag("many"), Flag("required"), m.Get("default") is not null, new SpecPos(m.Line, m.Column));
        }

        private CustomViewQuery? ReadQuery(YamlEntry entry)
        {
            var name = entry.Key.Value;
            if (!NameRule.IsMatch(name)) { Add(BadCustomView, entry.Key, $"`{name}` is not a query name: lower-case letters, digits, '-' and '_'."); return null; }
            if (entry.Value is not YamlMapping q) { Add(BadCustomView, entry.Value, $"The query `{name}` is a mapping with one form: `semantic` or `store`."); return null; }
            var forms = q.Entries.Select(e => e.Key.Value).ToList();
            if (forms.Count != 1 || forms[0] is not ("semantic" or "store")) { Add(BadCustomView, q, $"The query `{name}` has exactly one form, `semantic` or `store`."); return null; }
            var form = forms[0];
            if (q.Get(form) is not YamlMapping body) { Add(BadCustomView, q.Get(form)!, $"`{form}` of the query `{name}` is a mapping."); return null; }
            CheckKeys(body, form == "semantic" ? ["model", "request"] : ["model", "entity", "columns", "limit"], $"the `{form}` of the query `{name}`");
            var model = body.Get("model") as YamlScalar;
            if (model is null || model.Value.Length == 0) { Add(BadCustomView, (YamlNode?)body.Get("model") ?? body, $"The query `{name}` names its `model`."); return null; }
            string? entity = null;
            var members = new List<string>();
            if (form == "store")
            {
                entity = (body.Get("entity") as YamlScalar)?.Value;
                if (entity is null || !Regex.IsMatch(entity, "^[A-Za-z][A-Za-z0-9_]*$")) { Add(BadCustomView, (YamlNode?)body.Get("entity") ?? body, $"The query `{name}` names the `entity` it reads."); return null; }
            }
            else if (body.Get("request") is YamlMapping request)
            {
                foreach (var key in new[] { "measures", "attributes" })
                    if (request.Get(key) is YamlSequence list) foreach (var item in list.Items) if (item is YamlScalar s) members.Add(s.Value);
            }
            else { Add(BadCustomView, (YamlNode?)body.Get("request") ?? body, $"The semantic query `{name}` has a `request`: `{{measures: [...], attributes: [...], limit: 100}}`."); return null; }
            return new CustomViewQuery(name, form, model.Value, entity, members, new SpecPos(entry.Key.Line, entry.Key.Column), new SpecPos(model.Line, model.Column));
        }
    }
}
