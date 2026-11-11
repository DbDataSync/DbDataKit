using System.Text.Json.Nodes;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Views;

/// <summary>What the catalog says a name is: a measure, an attribute (a dimension's attribute, a calendar level, a datetime bucket), or nothing it knows.</summary>
public enum CatalogRole { Unknown, Measure, Attribute }

/// <summary>
/// The roles of a semantic model's members (plan 32 section 2, DBR03): the only thing <see cref="DeriveRequest"/> needs from the catalog. It is built from a loaded catalog (the host, the console's
/// copy of the Gateway's <c>GET /api/v1/semantic-models/{name}</c>, Build's <c>project compile</c>) or from two lists, so the function stays pure and the editor's TypeScript twin can use the same data.
/// </summary>
public sealed class CatalogRoles
{
    private readonly Func<string, CatalogRole> of;

    public CatalogRoles(string model, Func<string, CatalogRole> of) { Model = model; this.of = of; }

    public CatalogRoles(string model, IEnumerable<string> measures, IEnumerable<string> attributes)
    {
        Model = model;
        var m = measures.ToHashSet(StringComparer.Ordinal);
        var a = attributes.ToHashSet(StringComparer.Ordinal);
        of = name => m.Contains(name) ? CatalogRole.Measure : a.Contains(name) ? CatalogRole.Attribute : CatalogRole.Unknown;
    }

    /// <summary>The model's name (the <c>model</c> of a widget's query).</summary>
    public string Model { get; }

    public CatalogRole Of(string name) => of(name);

    /// <summary>Reads <c>{"model": "...", "measures": [...], "attributes": [...]}</c>, the form of the shared goldens.</summary>
    public static CatalogRoles FromJson(JsonNode node) =>
        new(node["model"]?.GetValue<string>() ?? "", Names(node["measures"]), Names(node["attributes"]));

    private static IEnumerable<string> Names(JsonNode? list) => list is JsonArray a ? a.Select(n => n!.GetValue<string>()) : [];
}

/// <summary>A document parameter that supplies a request's <c>{"param": name}</c> marker: the request's name, and the document's (they differ when the query's <c>params</c> maps one to the other).</summary>
public sealed record ParameterBinding(string RequestParameter, string DocumentParameter);

/// <summary>
/// The result of <see cref="DeriveRequest.Derive"/>: the semantic request (the envelope of <c>docs/semantic/query.md</c>) or null with the diagnostics, the parameters it leaves to be bound, and for each
/// entry of the request's <c>measures</c> and <c>attributes</c> the path in the document of the encoded field that asked for it (so a refusal of the engine lands on the field, <see cref="DocumentPath"/>).
/// </summary>
public sealed class DerivedRequest(JsonObject? request, IReadOnlyList<ParameterBinding> bindings, IReadOnlyDictionary<string, string> origins, string widgetPath, IReadOnlyList<ConfigDiagnostic> diagnostics)
{
    public JsonObject? Request { get; } = request;
    public IReadOnlyList<ParameterBinding> Bindings { get; } = bindings;
    public IReadOnlyList<ConfigDiagnostic> Diagnostics { get; } = diagnostics;
    public bool Ok => Request is not null && !Diagnostics.HasErrors();

    /// <summary>
    /// Where in the document a place of the request comes from: <c>$.query.measures[1]</c> is the encoded field that asked for it (<c>widgets.by_region.encodings.y</c>), <c>$.query.filters[0]</c> is
    /// <c>widgets.by_region.query.filters[0]</c>, and so on; a place nothing in the document wrote (the default limit) is the widget's query.
    /// </summary>
    public string DocumentPath(string requestPath)
    {
        var key = RequestPath.Normalise(requestPath)[2..];
        // the longest place the document wrote: `query.filters[0].operator` is a place inside the filter at `query.filters[0]`
        for (var k = key; k.Length > 0;)
        {
            if (origins.TryGetValue(k, out var found)) return found + key[k.Length..];
            var cut = Math.Max(k.LastIndexOf('.'), k.LastIndexOf('['));
            k = cut < 0 ? "" : k[..cut];
        }
        return key.StartsWith("query", StringComparison.Ordinal) ? widgetPath + "." + key : $"{widgetPath}.query";
    }
}

/// <summary>The engine names a place of a request two ways: <c>$.query.filters[0].attribute</c> (its own checks) and <c>$/query/filters/0</c> (the JSON Schema's); both become the first.</summary>
internal static class RequestPath
{
    public static string Normalise(string path)
    {
        if (!path.StartsWith("$/", StringComparison.Ordinal)) return path.StartsWith("$.", StringComparison.Ordinal) || path == "$" ? path : "$." + path;
        var result = "$";
        foreach (var segment in path[2..].Split('/', StringSplitOptions.RemoveEmptyEntries))
            result += int.TryParse(segment, out var index) ? $"[{index}]" : "." + segment;
        return result;
    }
}

/// <summary>
/// <c>DeriveRequest</c> (plan 32 section 2.3, DBR03): a widget's encodings, with the catalog's roles, become the semantic request they need. Pure: nothing is read or run, so what is drawn and what is
/// asked cannot drift, and the TypeScript twin in <c>@dbdatakit/visuals</c> (the editor's field wells) passes the same goldens (<c>conformance/views/derive</c>).
/// <para>
/// The rules: a measure well (a metric's <c>value</c> and <c>compare</c>, a matrix's <c>values</c>) asks for measures; a matrix's <c>rows</c> and <c>columns</c> for attributes; every other encoded field
/// (a chart's channels, a table's columns) goes to <c>measures</c> when the catalog says it is a measure and to <c>attributes</c> otherwise. Fields are taken in a fixed order (chart: x, y, color, column, row,
/// tooltip; matrix: rows, columns, values) and each once, whatever order the file wrote them in. <c>filters</c>, <c>segments</c> and <c>timezone</c> are the query's, as written; <c>order</c> is the query's, or
/// a table's own <c>sort</c> (a sort by a field the table does not show adds it to the request); <c>limit</c> is the query's or the widget type's default (metric 1, chart 500, table 100, matrix 10,000),
/// never above 10,000; a matrix's <c>totals</c> become the request's <c>totals</c> (and so does a sort by a measure, which needs the axis total); a table's or chart's <c>totals: {grand_total: true}</c> without
/// <c>subtotal_by</c> rolls up every attribute shown. A chart's channel <c>sort</c>, a matrix's <c>sort</c> and <c>column_limit</c> are the view's and add nothing.
/// </para>
/// </summary>
public static class DeriveRequest
{
    public const string SpecVersion = "0.3";
    public const int MaximumLimit = 10_000;

    private static readonly string[] ChartChannels = ["x", "y", "color", "column", "row", "tooltip"];

    /// <summary>The widget type's default <c>limit</c> when its query has none.</summary>
    public static int DefaultLimit(string widgetType) => widgetType switch { "metric" => 1, "chart" => 500, "table" => 100, "matrix" => 10_000, _ => 100 };

    private enum Well { Either, Measure, Attribute }

    private sealed record Slot(string Field, string Path, Well Well);

    public static DerivedRequest Derive(ViewWidget widget, CatalogRoles roles, string file = "")
    {
        var wp = $"widgets.{widget.Id}";
        var diagnostics = new List<ConfigDiagnostic>();
        ConfigDiagnostic Diag(string code, string path, string message, string? fix = null)
        {
            var at = widget.Fields.FirstOrDefault(f => f.Path == path);
            return DefaultDiagnosticCatalog.Instance.Create(code, file, at?.Line ?? widget.Line, at?.Column ?? widget.Column, message, fix, path);
        }
        DerivedRequest Refused() => new(null, [], new Dictionary<string, string>(), wp, diagnostics);

        if (widget.Type is not ("metric" or "chart" or "matrix" or "table"))
        { diagnostics.Add(Diag(ViewsCodes.NothingToDerive, wp, $"A {widget.Type} widget asks for nothing: it has no query.")); return Refused(); }
        if (widget.Query is null || widget.Query.Explicit)
        { diagnostics.Add(Diag(ViewsCodes.NothingToDerive, $"{wp}.query", "This query is written out (`mode: explicit`), so the encodings do not drive it and there is nothing to derive.", "Use the query's own request, or drop `mode: explicit` and the written query.")); return Refused(); }

        var encodings = widget.Raw["encodings"] as JsonObject ?? [];
        var slots = Slots(widget.Type, encodings, $"{wp}.encodings");
        var query = widget.Query.Raw;

        var measures = new List<string>();
        var attributes = new List<string>();
        var origins = new Dictionary<string, string>();

        void Place(string field, string path, Well well)
        {
            var role = roles.Of(field);
            // the words are the engine's own (Validator.CheckMember), so a field well and the engine refuse in one voice
            if (role == CatalogRole.Unknown)
                diagnostics.Add(Diag("unknown_member", path, $"{field} is not a catalog member."));
            else if (well == Well.Measure && role == CatalogRole.Attribute)
                diagnostics.Add(Diag("wrong_member_kind", path, $"{field} is a attribute, not a measure."));
            else if (well == Well.Attribute && role == CatalogRole.Measure)
                diagnostics.Add(Diag("wrong_member_kind", path, $"{field} is a measure, not a attribute."));
            var toMeasures = well == Well.Measure || (well == Well.Either && role == CatalogRole.Measure);
            var list = toMeasures ? measures : attributes;
            if (list.Contains(field)) return;
            list.Add(field);
            origins[$"query.{(toMeasures ? "measures" : "attributes")}[{list.Count - 1}]"] = path;
        }

        foreach (var s in slots) Place(s.Field, s.Path, s.Well);

        // ---- order: the query's own, else a table's sort ----
        JsonArray? order = null;
        if (query["order"] is JsonArray written && written.Count > 0)
        {
            order = (JsonArray)written.DeepClone();
            for (var i = 0; i < order.Count; i++) origins[$"query.order[{i}]"] = $"{wp}.query.order[{i}]";
        }
        else if (widget.Type == "table" && encodings["sort"] is JsonObject { } sort && sort["by"] is JsonValue by && by.TryGetValue<string>(out var sortField))
        {
            var direction = sort["dir"] is JsonValue d && d.TryGetValue<string>(out var dir) ? dir : "asc";
            var sortPath = $"{wp}.encodings.sort.by";
            if (!measures.Contains(sortField) && !attributes.Contains(sortField)) Place(sortField, sortPath, Well.Either);
            order = [new JsonArray(sortField, direction)];
            origins["query.order[0]"] = sortPath;
        }

        // ---- totals ----
        JsonObject? totals = null;
        if (widget.Type == "matrix") totals = MatrixTotals(encodings, roles, wp, query, diagnostics, Diag);
        else if (query["totals"] is JsonObject qt)
        {
            totals = (JsonObject)qt.DeepClone();
            if (totals["subtotal_by"] is null && attributes.Count > 0)
            {
                // the engine's schema wants the list; a grand total alone is the roll-up of everything the widget groups by
                var withList = new JsonObject { ["subtotal_by"] = new JsonArray(attributes.Select(a => (JsonNode)a).ToArray()) };
                foreach (var (key, value) in totals) withList[key] = value?.DeepClone();
                totals = withList;
            }
        }
        if (measures.Count == 0 && diagnostics.Count == 0)
            diagnostics.Add(Diag(ViewsCodes.NoMeasure, $"{wp}.encodings", "The encodings name no measure, and a request needs at least one.", widget.Type == "table" ? "Add a measure column, or write the query out with `mode: explicit` and a `store` read for rows as they are." : "Encode a measure."));
        if (diagnostics.Count > 0) return Refused();

        // ---- the request ----
        var limit = query["limit"] is JsonValue lv && Whole(lv) is { } written2 ? (int)Math.Min(written2, MaximumLimit) : DefaultLimit(widget.Type);
        var q = new JsonObject { ["measures"] = new JsonArray(measures.Select(m => (JsonNode)m).ToArray()) };
        if (attributes.Count > 0) q["attributes"] = new JsonArray(attributes.Select(a => (JsonNode)a).ToArray());
        if (query["filters"] is JsonArray { Count: > 0 } filters)
        {
            q["filters"] = filters.DeepClone();
            for (var i = 0; i < filters.Count; i++) origins[$"query.filters[{i}]"] = $"{wp}.query.filters[{i}]";
        }
        if (query["segments"] is JsonArray { Count: > 0 } segments)
        {
            q["segments"] = segments.DeepClone();
            for (var i = 0; i < segments.Count; i++) origins[$"query.segments[{i}]"] = $"{wp}.query.segments[{i}]";
        }
        if (order is not null) q["order"] = order;
        q["limit"] = limit;
        if (totals is not null) { q["totals"] = totals; origins["query.totals"] = widget.Type == "matrix" ? $"{wp}.encodings.totals" : $"{wp}.query.totals"; }
        if (query["timezone"] is JsonValue { } tz && tz.TryGetValue<string>(out var zone)) q["timezone"] = zone;

        // a number read from YAML is a long; the request is JSON as it travels, so the engine reads it as it reads any request
        var request = (JsonObject)JsonNode.Parse(new JsonObject { ["spec_version"] = SpecVersion, ["kind"] = "query", ["query"] = q }.ToJsonString())!;
        return new DerivedRequest(request, Bindings(q, query["params"] as JsonObject), origins, wp, []);
    }

    // ---- the fields of each widget type, in the fixed order ----

    private static List<Slot> Slots(string type, JsonObject encodings, string path)
    {
        var slots = new List<Slot>();
        void One(string channel, Well well)
        {
            if (encodings[channel] is JsonValue v && v.TryGetValue<string>(out var f) && f.Length > 0) slots.Add(new(f, $"{path}.{channel}", well));
        }
        void Channel(JsonNode? node, string p)
        {
            if (node is JsonValue v && v.TryGetValue<string>(out var f) && f.Length > 0) slots.Add(new(f, p, Well.Either));
            else if (node is JsonObject o && o["field"] is JsonValue fv && fv.TryGetValue<string>(out var name) && name.Length > 0) slots.Add(new(name, $"{p}.field", Well.Either));
        }
        switch (type)
        {
            case "metric": One("value", Well.Measure); One("compare", Well.Measure); break;
            case "chart":
                foreach (var c in ChartChannels)
                {
                    if (c == "tooltip" && encodings[c] is JsonArray list) { for (var i = 0; i < list.Count; i++) Channel(list[i], $"{path}.tooltip[{i}]"); }
                    else Channel(encodings[c], $"{path}.{c}");
                }
                break;
            case "matrix":
                foreach (var axis in new[] { "rows", "columns" })
                    if (encodings[axis] is JsonArray fields)
                        for (var i = 0; i < fields.Count; i++)
                        {
                            if (fields[i] is JsonValue v && v.TryGetValue<string>(out var f)) slots.Add(new(f, $"{path}.{axis}[{i}]", Well.Attribute));
                            else if (fields[i] is JsonObject entry && entry["field"] is JsonValue ev && ev.TryGetValue<string>(out var ef)) slots.Add(new(ef, $"{path}.{axis}[{i}].field", Well.Attribute));
                        }
                if (encodings["values"] is JsonArray values)
                    for (var i = 0; i < values.Count; i++)
                        if (values[i] is JsonObject o && o["field"] is JsonValue fv && fv.TryGetValue<string>(out var name)) slots.Add(new(name, $"{path}.values[{i}].field", Well.Measure));
                break;
            case "table":
                if (encodings["columns"] is JsonArray columns)
                    for (var i = 0; i < columns.Count; i++)
                        if (columns[i] is JsonObject o && o["field"] is JsonValue fv && fv.TryGetValue<string>(out var name)) slots.Add(new(name, $"{path}.columns[{i}].field", Well.Either));
                break;
        }
        return slots;
    }

    // ---- a matrix's totals ----

    private static JsonObject? MatrixTotals(JsonObject encodings, CatalogRoles roles, string wp, JsonObject query, List<ConfigDiagnostic> diagnostics, Func<string, string, string, string?, ConfigDiagnostic> diag)
    {
        // an axis entry is a field name or `{field, format, sort}` (DBR41)
        static string EntryField(JsonNode? n) => n is JsonObject o ? o["field"]?.GetValue<string>() ?? "" : n?.GetValue<string>() ?? "";
        List<string> Axis(string name) => encodings[name] is JsonArray a ? a.Select(EntryField).Distinct().ToList() : [];
        JsonNode? AxisSort(string axis) => encodings["sort"]?[axis] ?? (encodings[axis] is JsonArray a ? a.OfType<JsonObject>().Select(o => o["sort"]).FirstOrDefault(x => x is not null) : null);
        var rows = Axis("rows");
        var columns = Axis("columns");
        var wantRow = encodings["totals"]?["rows"]?.GetValue<bool>() ?? false;      // a total row: the row fields rolled up, the column fields kept
        var wantColumn = encodings["totals"]?["columns"]?.GetValue<bool>() ?? false;
        // sorting an axis by a measure reads each member's value in the axis total (the view's `sort_needs_totals`), so the request asks for that total too
        bool ByMeasure(string axis) => AxisSort(axis)?["by"] is JsonValue v && v.TryGetValue<string>(out var by) && roles.Of(by) == CatalogRole.Measure;
        var needRow = wantRow || (ByMeasure("columns") && rows.Count > 0);
        var needColumn = wantColumn || (ByMeasure("rows") && columns.Count > 0);
        if (!needRow && !needColumn) return null;

        if (query["totals"] is not null)
        { diagnostics.Add(diag(ViewsCodes.TotalsConflict, $"{wp}.query.totals", "A matrix's totals are made from its encodings (`totals`, and a sort by a measure); the query asks for them too.", "Remove `totals` from the query.")); return null; }
        if (wantRow && rows.Count == 0)
        { diagnostics.Add(diag(ViewsCodes.TotalsConflict, $"{wp}.encodings.totals.rows", "A total row needs at least one row field to roll up.", null)); return null; }
        if (wantColumn && columns.Count == 0)
        { diagnostics.Add(diag(ViewsCodes.TotalsConflict, $"{wp}.encodings.totals.columns", "A total column needs at least one column field to roll up.", null)); return null; }

        string[] all = [.. rows, .. columns];
        if (needRow && needColumn)
            return new JsonObject { ["subtotal_by"] = Array(all), ["grand_total"] = true, ["cube"] = true };
        // a roll-up takes the innermost attribute first: the axis to roll up goes last
        return needRow
            ? new JsonObject { ["subtotal_by"] = Array([.. columns, .. rows]) }
            : new JsonObject { ["subtotal_by"] = Array(all) };
    }

    private static long? Whole(JsonValue v) => v.TryGetValue<long>(out var l) ? l : v.TryGetValue<int>(out var i) ? i : v.TryGetValue<double>(out var d) && d == Math.Floor(d) ? (long)d : null;

    private static JsonArray Array(IEnumerable<string> names) => new(names.Select(n => (JsonNode)n).ToArray());

    // ---- parameter markers ----

    private static List<ParameterBinding> Bindings(JsonObject request, JsonObject? map)
    {
        var found = new List<ParameterBinding>();
        void Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject { Count: 1 } o when o["param"] is JsonValue v && v.TryGetValue<string>(out var name):
                    var document = map?[name] is JsonValue m && m.TryGetValue<string>(out var mapped) ? mapped : name;
                    if (!found.Any(b => b.RequestParameter == name)) found.Add(new ParameterBinding(name, document));
                    break;
                case JsonObject o: foreach (var (_, child) in o) Walk(child); break;
                case JsonArray a: foreach (var child in a) Walk(child); break;
            }
        }
        Walk(request);
        return found;
    }
}
