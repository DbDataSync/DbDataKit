using System.Globalization;
using System.Text.RegularExpressions;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Apps;

public sealed class ViewSpecLoadResult(ViewSpec? spec, IReadOnlyList<ConfigDiagnostic> diagnostics)
{
    public ViewSpec? Spec { get; } = spec;
    public IReadOnlyList<ConfigDiagnostic> Diagnostics { get; } = diagnostics;
    public bool Ok => Spec is not null && !Diagnostics.HasErrors();
}

/// <summary>
/// Strict reader of a declarative view file, <c>view-spec/1</c> (plan 22a GP06): a grid, a form, a detail page or a dashboard over the entities of a Store model the app uses. Every problem is a diagnostic
/// with the file, line and column and nothing throws on a damaged file. Codes, in the series of the manifest's own: <c>DBD-APP-014</c> the shape or a value, <c>-015</c> the file's format number,
/// <c>-016</c> a model or entity the manifest does not list (<see cref="AppChecks.ViewSpecs"/>), <c>-017</c> an attribute the model does not have, <c>-018</c> a link that does not lead to a view of the app.
/// This reads one file; what it names is checked against the manifest and the models by <see cref="AppChecks.ViewSpecs"/>.
/// </summary>
public static class ViewSpecLoader
{
    public const string BadViewSpec = "DBD-APP-014";
    public const string ViewSpecFormat = "DBD-APP-015";
    public const string UnknownViewModel = "DBD-APP-016";
    public const string UnknownViewAttribute = "DBD-APP-017";
    public const string BadViewLink = "DBD-APP-018";

    /// <summary>The format this program reads. Formats are numbered from 1; a newer one is refused naming the file and both numbers (plan 24a, the rule of formats).</summary>
    public const int Version = 1;
    public const string Kind = "view-spec";

    public const int MaximumColumns = 50;
    public const int MaximumFields = 100;
    public const int MaximumSections = 20;
    public const int MaximumAttributes = 100;
    public const int MaximumWidgets = 24;
    public const int MaximumLinks = 20;
    public const int MaximumParams = 20;
    public const int DefaultPageSize = 50;
    public const int MaximumPageSize = 500;
    public const int DefaultWidgetLimit = 10;
    public const int MaximumWidgetLimit = 100;

    private static readonly string[] Common = ["schemaVersion", "kind", "title", "model", "params", "links"];

    /// <summary>The keys each part of a spec accepts, by part name (<c>grid</c>, <c>form</c>, <c>detail</c> and <c>dashboard</c> for the file itself). The JSON Schema file has the same keys; a test keeps them equal.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Keys = new Dictionary<string, string[]>
    {
        ["grid"] = [.. Common, "entity", "columns", "filters", "sort", "pageSize"],
        ["form"] = [.. Common, "entity", "mode", "key", "fields", "submit"],
        ["detail"] = [.. Common, "entity", "key", "sections"],
        ["dashboard"] = [.. Common, "widgets"],
        ["param"] = ["name", "type", "required", "default"],
        ["link"] = ["label", "to", "row", "with"],
        ["column"] = ["attribute", "label", "sortable"],
        ["field"] = ["attribute", "label", "readonly", "required", "help"],
        ["sort"] = ["attribute", "direction"],
        ["section"] = ["title", "attributes"],
        ["attribute"] = ["attribute", "label"],
        ["widget"] = ["kind", "title", "entity", "measure", "by", "columns", "limit", "url", "link_text"],
    };

    /// <summary>The values of each choice, by name.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Choices = new Dictionary<string, string[]>
    {
        ["kind"] = ["grid", "form", "detail", "dashboard"],
        ["paramType"] = ["string", "int", "bool", "date"],
        ["direction"] = ["asc", "desc"],
        ["mode"] = ["create", "edit"],
        ["widgetKind"] = ["metric", "bar", "line", "table", "embed"],
        ["aggregate"] = ["sum", "avg", "min", "max"],
    };

    /// <summary>The JSON Schema of <c>view-spec/1</c> (an embedded resource), for editors and hosts. The loader is the authority; a test keeps the two in agreement.</summary>
    public static string Schema()
    {
        using var stream = typeof(ViewSpecLoader).Assembly.GetManifestResourceStream("DbDataKit.Apps.Manifest.schemas.view-spec.schema.json")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Whether a file is a <c>dashboard/1</c> or <c>report/1</c> document and not a view spec (plan 32 DBR13): its <c>kind</c> is <c>report</c> (a view spec has no such kind), or <c>dashboard</c> with the
    /// <c>layouts</c> a dashboard document has and the mini dashboard of a view spec has not. The checker of <see cref="IViewDocumentChecker"/> reads it.
    /// </summary>
    public static bool IsDocument(string yaml)
    {
        var scratch = new List<ConfigDiagnostic>();
        if (StrictYamlReader.Read(yaml, "x", scratch) is not YamlMapping map) return false;
        var kind = (map.Get("kind") as YamlScalar)?.Value;
        return kind == "report" || (kind == "dashboard" && map.Get("layouts") is not null);
    }

    private static readonly Regex Identifier = new("^[A-Za-z][A-Za-z0-9_]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Whether a text is a value of a parameter type: a whole number, <c>true</c> or <c>false</c>, a date as yyyy-MM-dd.</summary>
    internal static bool Fits(string value, ViewParamType type) => type switch
    {
        ViewParamType.Int => long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _),
        ViewParamType.Bool => value is "true" or "false",
        ViewParamType.Date => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
        _ => true,
    };

    /// <summary>The words of a refusal for a file in a format this program does not read, naming the kind, the file and both numbers; with <paramref name="app"/>, the app the file is part of.</summary>
    public static string Refusal(string file, int format, string? app = null)
    {
        var which = app is null ? $"the {Kind} '{file}'" : $"the app '{app}' holds the {Kind} '{file}', which";
        return format > Version
            ? $"{which} is in format {format}, and this program reads format {Version} of {Kind}. Upgrade this program; the file was written by a newer version."
            : $"{which} is in format {format}, which this program does not read (it reads format {Version} of {Kind}).";
    }

    /// <param name="yaml">The file's text.</param>
    /// <param name="file">The file's path in the app, which diagnostics name.</param>
    /// <param name="app">The app the file belongs to, named in a refusal for a newer format; null for a file read alone.</param>
    public static ViewSpecLoadResult Load(string yaml, string file, string? app = null)
    {
        var diags = new List<ConfigDiagnostic>();
        var root = StrictYamlReader.Read(yaml, file, diags);
        if (root is null && !diags.HasErrors()) diags.Add(DefaultDiagnosticCatalog.Instance.Create(BadViewSpec, file, 1, 1, "The view file is empty.", "Write `schemaVersion: 1`, a `kind` and the rest of the view."));
        if (root is null || diags.HasErrors()) return new(null, diags);
        var spec = new Reader(file, app, diags).Read(root);
        return new(diags.HasErrors() ? null : spec, diags);
    }

    private sealed class Reader(string fileName, string? app, List<ConfigDiagnostic> diags) : YamlFieldReader(fileName, diags)
    {
        private List<ViewParam> declared = [];

        private static SpecPos P(YamlNode n) => new(n.Line, n.Column);

        private void Bad(YamlNode at, string message, string? fix = null) => Add(BadViewSpec, at, message, fix);

        public ViewSpec? Read(YamlNode root)
        {
            if (root is not YamlMapping map) { Bad(root, "A view is a mapping with `schemaVersion: 1`, `kind`, `title` and `model`."); return null; }
            if (!CheckFormat(map)) return null;
            if (map.Get("kind") is not YamlScalar kindNode) { Add(SharedCodes.MissingKey, map, "Required key `kind` is missing.", $"Add `kind:` with one of: {string.Join(", ", Choices["kind"])}."); return null; }
            if (!Choices["kind"].Contains(kindNode.Value)) { Bad(kindNode, $"`{kindNode.Value}` is not a kind of view.", $"Use one of: {string.Join(", ", Choices["kind"])}."); return null; }
            var kind = kindNode.Value;
            CheckKeys(map, Keys[kind], $"a {kind} view");

            var title = Line(map, "title", 200, required: true);
            var modelNode = Scalar(map, "model", required: true, map);
            if (modelNode is not null && !AppManifestLoader.NameRule.IsMatch(modelNode.Value)) { Bad(modelNode, $"`{modelNode.Value}` is not a model name: lower-case letters, digits, '-' and '_'."); modelNode = null; }
            declared = Params(map);
            var links = Links(map, kind);
            ViewSpec? spec = kind switch
            {
                "grid" => Grid(map, title, modelNode, links),
                "form" => Form(map, title, modelNode, links),
                "detail" => Detail(map, title, modelNode, links),
                _ => Dashboard(map, title, modelNode, links),
            };
            return spec;
        }

        /// <summary><c>schemaVersion</c>: present, a whole number, and a format this program reads. A newer one is refused naming the kind, the file (and the app) and both numbers.</summary>
        private bool CheckFormat(YamlMapping map)
        {
            var node = map.Get("schemaVersion");
            if (node is null) { Add(ViewSpecFormat, map, $"`schemaVersion` is missing: a view file says which format of {Kind} it is in.", "Add `schemaVersion: 1`."); return false; }
            if (node is not YamlScalar { Value: var text } || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var format) || format < 1)
            { Add(ViewSpecFormat, node, "`schemaVersion` must be a positive whole number."); return false; }
            if (format == Version) return true;
            Add(ViewSpecFormat, node, Refusal(File, format, app), format > Version ? "Upgrade the tool, or write the file in format " + Version + "." : null);
            return false;
        }

        private string? Line(YamlMapping map, string key, int max, bool required)
        {
            var s = Scalar(map, key, required, map);
            if (s is null) return null;
            if (s.Value.Length > max || s.Value.Any(char.IsControl)) { Bad(s, $"`{key}` is one line of at most {max} characters."); return null; }
            return s.Value;
        }

        private bool Flag(YamlMapping map, string key, bool fallback)
        {
            if (map.Get(key) is not { } n) return fallback;
            if (n is YamlScalar { Value: "true" or "false" } s) return s.Value == "true";
            Bad(n, $"`{key}` is `true` or `false`.");
            return fallback;
        }

        private int Number(YamlMapping map, string key, int min, int max, int fallback)
        {
            if (map.Get(key) is not { } n) return fallback;
            if (n is YamlScalar s && int.TryParse(s.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v >= min && v <= max) return v;
            Bad(n, $"`{key}` is a whole number from {min} to {max}.");
            return fallback;
        }

        private T? Pick<T>(YamlMapping map, string key, string choice, T[] values, T? fallback) where T : struct
        {
            if (map.Get(key) is not { } n) return fallback;
            if (n is YamlScalar s && Choices[choice].ToList().IndexOf(s.Value) is var i and >= 0) return values[i];
            Bad(n, $"`{(n as YamlScalar)?.Value}` is not a value of `{key}`.", $"Use one of: {string.Join(", ", Choices[choice])}.");
            return fallback;
        }

        /// <summary>An attribute name: a scalar that is an identifier.</summary>
        private (string Name, SpecPos At)? AttributeName(YamlNode? node, string what)
        {
            if (node is YamlScalar s && Identifier.IsMatch(s.Value)) return (s.Value, P(s));
            if (node is not null) Bad(node, $"{what} is the name of an attribute of the entity: letters, digits and '_', starting with a letter.");
            return null;
        }

        private List<ViewParam> Params(YamlMapping map)
        {
            var found = new List<ViewParam>();
            if (map.Get("params") is not { } node) return found;
            if (node is not YamlSequence list) { Bad(node, "`params` is a list: `- name: id` with an optional `type`, `required` and `default`."); return found; }
            if (list.Items.Count > MaximumParams) Bad(list, $"A view has at most {MaximumParams} parameters.");
            foreach (var item in list.Items.Take(MaximumParams))
            {
                if (item is not YamlMapping p) { Bad(item, "A parameter is a mapping with a `name`."); continue; }
                CheckKeys(p, Keys["param"], "a parameter");
                var name = Scalar(p, "name", required: true, p);
                var type = Pick(p, "type", "paramType", [ViewParamType.String, ViewParamType.Int, ViewParamType.Bool, ViewParamType.Date], ViewParamType.String);
                var required = Flag(p, "required", false);
                string? fallback = null;
                if (p.Get("default") is { } d)
                {
                    if (d is not YamlScalar ds) Bad(d, "`default` is one value.");
                    else if (!ViewSpecLoader.Fits(ds.Value, type!.Value)) Bad(ds, $"`{ds.Value}` is not a {Choices["paramType"][(int)type.Value]}.");
                    else if (required) Bad(ds, "A parameter that is `required` has no `default`: one or the other.");
                    else fallback = ds.Value;
                }
                if (name is null) continue;
                if (!Identifier.IsMatch(name.Value)) { Bad(name, $"`{name.Value}` is not a parameter name: letters, digits and '_', starting with a letter."); continue; }
                if (found.Any(f => f.Name == name.Value)) { Bad(name, $"The parameter `{name.Value}` is declared twice."); continue; }
                found.Add(new ViewParam(name.Value, type!.Value, required, fallback, P(name)));
            }
            return found;
        }

        private List<ViewLink> Links(YamlMapping map, string kind)
        {
            var found = new List<ViewLink>();
            if (map.Get("links") is not { } node) return found;
            if (node is not YamlSequence list) { Bad(node, "`links` is a list: `- label: Open` with `to` (a view file of the app) and `with`."); return found; }
            if (list.Items.Count > MaximumLinks) Bad(list, $"A view has at most {MaximumLinks} links.");
            foreach (var item in list.Items.Take(MaximumLinks))
            {
                if (item is not YamlMapping l) { Bad(item, "A link is a mapping with `label` and `to`."); continue; }
                CheckKeys(l, Keys["link"], "a link");
                var label = Line(l, "label", 200, required: true);
                var to = Scalar(l, "to", required: true, l);
                var row = Flag(l, "row", false);
                if (row && kind != "grid") Bad((YamlNode?)l.Get("row") ?? l, "`row: true` puts a link on every row of a grid; only a grid has rows.");
                var with = new Dictionary<string, ViewValue>(StringComparer.Ordinal);
                if (l.Get("with") is { } w)
                {
                    if (w is not YamlMapping wm) Bad(w, "`with` maps the parameters of the target view to values: `id: $id`.");
                    else foreach (var e in wm.Entries)
                    {
                        if (!Identifier.IsMatch(e.Key.Value)) { Bad(e.Key, $"`{e.Key.Value}` is not a parameter name."); continue; }
                        if (e.Value is not YamlScalar v) { Bad(e.Value, $"The value of `{e.Key.Value}` is one value: text, `$attribute` or `$params.name`."); continue; }
                        var value = Value(v, kind, row);
                        if (value is not null && !with.TryAdd(e.Key.Value, value)) Bad(e.Key, $"`{e.Key.Value}` is given twice.");
                    }
                }
                if (to is null) continue;
                if (!SafeTarget(to.Value)) { Bad(to, $"`{to.Value}` is not a view file inside the app: use a relative path with forward slashes, ending in .yaml or .yml, and no `..`."); continue; }
                if (label is not null) found.Add(new ViewLink(label, to.Value, row, with, P(l), P(to)));
            }
            return found;
        }

        private static bool SafeTarget(string path) =>
            path.Length is > 0 and <= 300 && !path.StartsWith('/') && !path.Contains('\\') && !path.Contains(':') && path.Split('/').All(part => part.Length > 0 && part is not ("." or "..")) &&
            (path.EndsWith(".yaml", StringComparison.Ordinal) || path.EndsWith(".yml", StringComparison.Ordinal));

        /// <summary><c>$name</c> is an attribute of the row, <c>$params.name</c> a parameter of this view, anything else is text. A row's attribute needs a row: a link on a grid row, or a link of a detail page or a form.</summary>
        private ViewValue? Value(YamlScalar v, string kind, bool row)
        {
            var text = v.Value;
            if (!text.StartsWith('$')) return new ViewValue(ViewValueKind.Literal, text, P(v));
            if (text.StartsWith("$params.", StringComparison.Ordinal))
            {
                var name = text["$params.".Length..];
                if (declared.All(d => d.Name != name)) { Bad(v, $"`{text}` is not a parameter of this view.", declared.Count == 0 ? "Declare it under `params`." : $"The view's parameters: {string.Join(", ", declared.Select(d => d.Name))}."); return null; }
                return new ViewValue(ViewValueKind.Param, name, P(v));
            }
            var attribute = text[1..];
            if (!Identifier.IsMatch(attribute)) { Bad(v, $"`{text}` is not `$attribute` or `$params.name`."); return null; }
            if (kind is "dashboard" || (kind == "grid" && !row)) { Bad(v, $"`{text}` needs a row, and this link has none.", kind == "grid" ? "Add `row: true` to put the link on each row." : "A dashboard has no row; use `$params.name` or text."); return null; }
            return new ViewValue(ViewValueKind.Attribute, attribute, P(v));
        }

        private string? Entity(YamlMapping map, out SpecPos at)
        {
            at = P(map);
            var s = Scalar(map, "entity", required: true, map);
            if (s is null) return null;
            at = P(s);
            if (!Identifier.IsMatch(s.Value)) { Bad(s, $"`{s.Value}` is not an entity name."); return null; }
            return s.Value;
        }

        /// <summary>A list of attributes, each a name or a mapping with <c>attribute</c> and <c>label</c>.</summary>
        private List<ViewAttribute> Attributes(YamlMapping map, string key, bool required, int max)
        {
            var found = new List<ViewAttribute>();
            if (map.Get(key) is not { } node) { if (required) Add(SharedCodes.MissingKey, map, $"Required key `{key}` is missing.", $"Add `{key}:` with the attributes to show."); return found; }
            if (node is not YamlSequence list || (required && list.Items.Count == 0)) { Bad(node, $"`{key}` is a list of attributes of the entity, each a name or `{{attribute: name, label: Text}}`."); return found; }
            if (list.Items.Count > max) Bad(list, $"`{key}` has at most {max} entries.");
            foreach (var item in list.Items.Take(max))
            {
                if (item is YamlMapping m)
                {
                    CheckKeys(m, Keys["attribute"], $"an entry of `{key}`");
                    var a = AttributeName(m.Get("attribute"), "`attribute`");
                    if (a is null && m.Get("attribute") is null) Add(SharedCodes.MissingKey, m, "Required key `attribute` is missing.", "Add `attribute:`.");
                    var label = m.Get("label") is null ? null : Line(m, "label", 200, false);
                    if (a is { } v) Append(found, key, new ViewAttribute(v.Name, label, v.At));
                }
                else if (AttributeName(item, $"An entry of `{key}`") is { } named) Append(found, key, new ViewAttribute(named.Name, null, named.At));
            }
            return found;
        }

        private void Append(List<ViewAttribute> into, string key, ViewAttribute attribute)
        {
            if (into.Any(a => a.Name == attribute.Name)) Diagnostics.Add(DefaultDiagnosticCatalog.Instance.Create(BadViewSpec, File, attribute.At.Line, attribute.At.Column, $"`{attribute.Name}` is listed twice in `{key}`."));
            else into.Add(attribute);
        }

        private ViewSpec? Grid(YamlMapping map, string? title, YamlScalar? model, List<ViewLink> links)
        {
            var entity = Entity(map, out var entityAt);
            var columns = new List<ViewColumn>();
            if (map.Get("columns") is not YamlSequence { Items.Count: > 0 } cols) Bad((YamlNode?)map.Get("columns") ?? map, "`columns` is a list of the attributes to show: `[name, {attribute: total, label: Total, sortable: true}]`.");
            else
            {
                if (cols.Items.Count > MaximumColumns) Bad(cols, $"A grid has at most {MaximumColumns} columns.");
                foreach (var item in cols.Items.Take(MaximumColumns))
                {
                    (string Name, SpecPos At)? name; string? label = null; var sortable = true;
                    if (item is YamlMapping m)
                    {
                        CheckKeys(m, Keys["column"], "a column");
                        name = AttributeName(m.Get("attribute"), "`attribute`");
                        if (name is null && m.Get("attribute") is null) Add(SharedCodes.MissingKey, m, "Required key `attribute` is missing.", "Add `attribute:`.");
                        if (m.Get("label") is not null) label = Line(m, "label", 200, false);
                        sortable = Flag(m, "sortable", true);
                    }
                    else name = AttributeName(item, "A column");
                    if (name is not { } n) continue;
                    if (columns.Any(c => c.Attribute == n.Name)) { Diagnostics.Add(DefaultDiagnosticCatalog.Instance.Create(BadViewSpec, File, n.At.Line, n.At.Column, $"`{n.Name}` is a column twice.")); continue; }
                    columns.Add(new ViewColumn(n.Name, label, sortable, n.At));
                }
            }
            var filters = Attributes(map, "filters", false, MaximumAttributes);
            var sort = new List<ViewSort>();
            if (map.Get("sort") is { } sortNode)
            {
                if (sortNode is not YamlSequence sortList) Bad(sortNode, "`sort` is a list: `[name, {attribute: total, direction: desc}]`.");
                else foreach (var item in sortList.Items)
                {
                    (string Name, SpecPos At)? name; var direction = ViewSortDirection.Asc;
                    if (item is YamlMapping m)
                    {
                        CheckKeys(m, Keys["sort"], "a sort entry");
                        name = AttributeName(m.Get("attribute"), "`attribute`");
                        if (name is null && m.Get("attribute") is null) Add(SharedCodes.MissingKey, m, "Required key `attribute` is missing.", "Add `attribute:`.");
                        direction = Pick(m, "direction", "direction", [ViewSortDirection.Asc, ViewSortDirection.Desc], ViewSortDirection.Asc)!.Value;
                    }
                    else name = AttributeName(item, "A sort entry");
                    if (name is { } n) sort.Add(new ViewSort(n.Name, direction, n.At));
                }
            }
            var size = Number(map, "pageSize", 1, MaximumPageSize, DefaultPageSize);
            if (title is null || model is null || entity is null) return null;
            return new GridView(File, title, model.Value, entity, columns, filters, sort, size, declared, links, P(map), P(model), entityAt);
        }

        private ViewSpec? Form(YamlMapping map, string? title, YamlScalar? model, List<ViewLink> links)
        {
            var entity = Entity(map, out var entityAt);
            var mode = Pick(map, "mode", "mode", [ViewFormMode.Create, ViewFormMode.Edit], ViewFormMode.Create)!.Value;
            var key = KeyParam(map, mode == ViewFormMode.Edit, "An edit form names the parameter that carries the row's key: `key: id`.", "A form that creates a row has no `key`: only an edit form has one.");
            var submit = map.Get("submit") is null ? null : Line(map, "submit", 80, false);
            var fields = new List<ViewField>();
            if (map.Get("fields") is not YamlSequence { Items.Count: > 0 } list) Bad((YamlNode?)map.Get("fields") ?? map, "`fields` is a list of the attributes to edit: `[name, {attribute: region, required: true}]`.");
            else
            {
                if (list.Items.Count > MaximumFields) Bad(list, $"A form has at most {MaximumFields} fields.");
                foreach (var item in list.Items.Take(MaximumFields))
                {
                    (string Name, SpecPos At)? name; string? label = null, help = null; bool readOnly = false, required = false;
                    if (item is YamlMapping m)
                    {
                        CheckKeys(m, Keys["field"], "a field");
                        name = AttributeName(m.Get("attribute"), "`attribute`");
                        if (name is null && m.Get("attribute") is null) Add(SharedCodes.MissingKey, m, "Required key `attribute` is missing.", "Add `attribute:`.");
                        if (m.Get("label") is not null) label = Line(m, "label", 200, false);
                        if (m.Get("help") is not null) help = Line(m, "help", 500, false);
                        readOnly = Flag(m, "readonly", false);
                        required = Flag(m, "required", false);
                    }
                    else name = AttributeName(item, "A field");
                    if (name is not { } n) continue;
                    if (fields.Any(f => f.Attribute == n.Name)) { Diagnostics.Add(DefaultDiagnosticCatalog.Instance.Create(BadViewSpec, File, n.At.Line, n.At.Column, $"`{n.Name}` is a field twice.")); continue; }
                    fields.Add(new ViewField(n.Name, label, readOnly, required, help, n.At));
                }
            }
            if (title is null || model is null || entity is null) return null;
            return new FormView(File, title, model.Value, entity, mode, key, submit, fields, declared, links, P(map), P(model), entityAt);
        }

        /// <summary>The <c>key</c> of a detail page or an edit form: the name of a declared, required parameter.</summary>
        private string? KeyParam(YamlMapping map, bool needed, string missing, string notAllowed)
        {
            var node = map.Get("key");
            if (node is null) { if (needed) Add(SharedCodes.MissingKey, map, missing, "Add `key:` with the name of a parameter."); return null; }
            if (!needed) { Bad(node, notAllowed); return null; }
            if (node is not YamlScalar s || !Identifier.IsMatch(s.Value)) { Bad(node, "`key` is the name of a parameter of this view."); return null; }
            var p = declared.FirstOrDefault(d => d.Name == s.Value);
            if (p is null) { Bad(s, $"`key: {s.Value}` is not a parameter of this view.", "Declare it under `params` with `required: true`."); return null; }
            if (!p.Required) { Bad(s, $"The key parameter `{s.Value}` must be `required: true`: a row cannot be found without it."); return null; }
            return s.Value;
        }

        private ViewSpec? Detail(YamlMapping map, string? title, YamlScalar? model, List<ViewLink> links)
        {
            var entity = Entity(map, out var entityAt);
            var key = KeyParam(map, true, "A detail page names the parameter that carries the row's key: `key: id`.", "");
            var sections = new List<ViewSection>();
            if (map.Get("sections") is not YamlSequence { Items.Count: > 0 } list) Bad((YamlNode?)map.Get("sections") ?? map, "`sections` is a list of `- title: Name` with the `attributes` to show.");
            else
            {
                if (list.Items.Count > MaximumSections) Bad(list, $"A detail page has at most {MaximumSections} sections.");
                foreach (var item in list.Items.Take(MaximumSections))
                {
                    if (item is not YamlMapping m) { Bad(item, "A section is a mapping with a `title` and `attributes`."); continue; }
                    CheckKeys(m, Keys["section"], "a section");
                    var sectionTitle = Line(m, "title", 200, required: true);
                    var attributes = Attributes(m, "attributes", true, MaximumAttributes);
                    if (sectionTitle is not null) sections.Add(new ViewSection(sectionTitle, attributes, P(m)));
                }
            }
            if (title is null || model is null || entity is null || key is null) return null;
            return new DetailView(File, title, model.Value, entity, key, sections, declared, links, P(map), P(model), entityAt);
        }

        private ViewSpec? Dashboard(YamlMapping map, string? title, YamlScalar? model, List<ViewLink> links)
        {
            var widgets = new List<ViewWidget>();
            if (map.Get("widgets") is not YamlSequence { Items.Count: > 0 } list) Bad((YamlNode?)map.Get("widgets") ?? map, "`widgets` is a list of figures: `- {kind: metric, title: Orders, entity: order, measure: count}`.");
            else
            {
                if (list.Items.Count > MaximumWidgets) Bad(list, $"A dashboard has at most {MaximumWidgets} widgets.");
                foreach (var item in list.Items.Take(MaximumWidgets))
                    if (Widget(item) is { } widget) widgets.Add(widget);
            }
            if (title is null || model is null) return null;
            return new DashboardView(File, title, model.Value, widgets, declared, links, P(map), P(model));
        }

        /// <summary>An <c>embed</c> widget (plan 26 AP17): a report of an external report server, framed when the host approves its origin and a link otherwise. It has no entity, no data and none of the figure's keys.</summary>
        private ViewWidget? Embed(YamlMapping m, string? title)
        {
            var ok = title is not null;
            foreach (var key in new[] { "entity", "measure", "by", "columns", "limit" })
                if (m.Get(key) is { } stray) { Bad(stray, $"`{key}` is for a figure; an `embed` widget has a `url` and a `link_text`."); ok = false; }
            var url = Scalar(m, "url", required: true, m);
            var at = url is null ? P(m) : P(url);
            if (url is not null && !SafeAddress(url.Value)) { Bad(url, "`url` is an absolute https address with no credentials, such as `https://reports.contoso.com/Reports/report/Sales`."); url = null; }
            var text = m.Get("link_text") is null ? null : Line(m, "link_text", 200, false);
            return ok && url is not null ? new ViewWidget(ViewWidgetKind.Embed, title!, "", ViewAggregate.Count, null, null, [], DefaultWidgetLimit, P(m), P(m), url.Value, text, at) : null;
        }

        private static bool SafeAddress(string value) =>
            value.Length <= 2000 && !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
            && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 && uri.Host.Length > 0;

        private ViewWidget? Widget(YamlNode item)
        {
            if (item is not YamlMapping m) { Bad(item, "A widget is a mapping with `kind`, `title` and `entity`."); return null; }
            CheckKeys(m, Keys["widget"], "a widget");
            var kind = Pick<ViewWidgetKind>(m, "kind", "widgetKind", [ViewWidgetKind.Metric, ViewWidgetKind.Bar, ViewWidgetKind.Line, ViewWidgetKind.Table, ViewWidgetKind.Embed], null);
            if (m.Get("kind") is null) Add(SharedCodes.MissingKey, m, "Required key `kind` is missing.", $"Add `kind:` with one of: {string.Join(", ", Choices["widgetKind"])}.");
            var title = Line(m, "title", 200, required: true);
            if (kind == ViewWidgetKind.Embed) return Embed(m, title);
            var entity = Entity(m, out var entityAt);
            var limit = Number(m, "limit", 1, MaximumWidgetLimit, DefaultWidgetLimit);
            var aggregate = ViewAggregate.Count;
            ViewAttribute? measure = null, by = null;
            var columns = new List<ViewAttribute>();
            var ok = kind is not null && title is not null && entity is not null;

            if ((m.Get("url") ?? m.Get("link_text")) is { } stray) { Bad(stray, "`url` and `link_text` are for an `embed` widget."); ok = false; }
            if (kind is ViewWidgetKind.Metric or ViewWidgetKind.Bar or ViewWidgetKind.Line)
            {
                if (m.Get("measure") is not { } mn) { Add(SharedCodes.MissingKey, m, "Required key `measure` is missing.", "Add `measure: count`, or `measure: {sum: attribute}`."); ok = false; }
                else if (mn is YamlScalar { Value: "count" }) aggregate = ViewAggregate.Count;
                else if (mn is YamlMapping { Entries.Count: 1 } mm && Choices["aggregate"].Contains(mm.Entries[0].Key.Value))
                {
                    aggregate = Enum.Parse<ViewAggregate>(mm.Entries[0].Key.Value, ignoreCase: true);
                    if (AttributeName(mm.Entries[0].Value, $"`{mm.Entries[0].Key.Value}`") is { } a) measure = new ViewAttribute(a.Name, null, a.At); else ok = false;
                }
                else { Bad(mn, "`measure` is `count`, or one aggregate of an attribute: `{sum: total}`.", $"Aggregates: count, {string.Join(", ", Choices["aggregate"])}."); ok = false; }
                if (kind != ViewWidgetKind.Metric)
                {
                    if (m.Get("by") is null) { Add(SharedCodes.MissingKey, m, $"Required key `by` is missing: a {kind.ToString()!.ToLowerInvariant()} groups the measure by an attribute.", "Add `by:` with an attribute."); ok = false; }
                    else if (AttributeName(m.Get("by"), "`by`") is { } b) by = new ViewAttribute(b.Name, null, b.At); else ok = false;
                }
                else if (m.Get("by") is { } extra) { Bad(extra, "A metric is one figure; `by` is for a bar or a line."); ok = false; }
                if (m.Get("columns") is { } c1) { Bad(c1, "`columns` is for a table."); ok = false; }
            }
            else if (kind == ViewWidgetKind.Table)
            {
                columns = Attributes(m, "columns", true, MaximumColumns);
                if (columns.Count == 0) ok = false;
                if (m.Get("measure") is { } m1) { Bad(m1, "`measure` is for a metric, a bar or a line."); ok = false; }
                if (m.Get("by") is { } b1) { Bad(b1, "`by` is for a bar or a line."); ok = false; }
            }
            return ok ? new ViewWidget(kind!.Value, title!, entity!, aggregate, measure, by, columns, limit, P(m), entityAt) : null;
        }
    }
}
