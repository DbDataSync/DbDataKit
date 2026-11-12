using System.Text.Json.Nodes;
using DbDataKit.Access;

namespace DbDataKit.Semantic;

/// <summary>
/// A catalog as one caller may see it once the column rules of its <c>access</c> section are applied (plan 22a GP13): what the caller asks about, is described and is answered is this catalog, so a hidden column is unknown to every
/// request kind and a masked or nulled one is read through an expression that cannot show the value. <see cref="Catalog"/> is the rewritten catalog (the original when the caller has no treatment).
/// </summary>
public sealed class CatalogColumnView
{
    private CatalogColumnView(Catalog catalog, IReadOnlyDictionary<string, ColumnTreatmentOf> treated, IReadOnlySet<string> hashed, IReadOnlySet<string> hidden)
    {
        Catalog = catalog;
        Treated = treated;
        Hashed = hashed;
        Hidden = hidden;
    }

    /// <summary>What the caller's requests run on.</summary>
    public Catalog Catalog { get; }

    /// <summary>The attributes (<c>dimension.attribute</c>) that are masked or nulled for the caller, with how. The caller sees them described and answered, but cannot filter, sort or join by them.</summary>
    public IReadOnlyDictionary<string, ColumnTreatmentOf> Treated { get; }

    /// <summary>The attributes masked with a keyed hash: the engine groups by the real value, and the answer carries the hash (see <see cref="ColumnMasks.Hash"/>).</summary>
    public IReadOnlySet<string> Hashed { get; }

    /// <summary>The attributes and measures the caller does not see at all, so they exist in no description and no request.</summary>
    public IReadOnlySet<string> Hidden { get; }

    /// <summary>Whether the view differs from the catalog.</summary>
    public bool IsRestricted => Treated.Count > 0 || Hidden.Count > 0;

    /// <summary>The marker of the derived column that carries a masked or nulled column (<c>customer__masked</c>).</summary>
    private const string Suffix = "__access";

    public static CatalogColumnView Unrestricted(Catalog catalog) => new(catalog, new Dictionary<string, ColumnTreatmentOf>(), new HashSet<string>(), new HashSet<string>());

    /// <summary>
    /// Applies the caller's treatments to the catalog. <b>Hide</b>: the attributes bound to the column and the measures over it go, then everything that reads them (a calculation over a hidden measure, a hierarchy or calendar with a
    /// hidden level, a segment or a saved query that names one); the table keeps the column for the joins and the row rules that use it, but nothing reaches it. <b>Null</b> and <b>mask</b> (<c>fixed</c>): a derived column of the table
    /// evaluates to NULL or to the constant in the database itself, written so that it still names the column (<c>COALESCE(NULLIF(c, c), '********')</c>): a constant in a GROUP BY is refused by SQL Server, and a group-by on this
    /// expression gives the one group it should. The attribute is bound to the derived column. <b>Mask</b> (<c>hash</c>): the engine reads the real value and the answer replaces it (<see cref="Hashed"/>); the key never enters a statement.
    /// A column of a source on Analysis Services cannot carry the expression, and a mask of a column declared as something other than text cannot be a text constant: both are hidden instead (default taken).
    /// </summary>
    public static CatalogColumnView For(Catalog catalog, ColumnTreatments treatments)
    {
        if (treatments.IsEmpty) return Unrestricted(catalog);
        var json = (JsonObject)catalog.Source.DeepClone();
        var tables = (json["tables"] as JsonArray)?.OfType<JsonObject>().Where(t => t["name"] is not null).ToDictionary(t => t["name"]!.GetValue<string>(), StringComparer.Ordinal) ?? [];
        var hiddenColumns = new HashSet<(string, string)>();
        var derived = new Dictionary<(string, string), string>();
        var hashedColumns = new HashSet<(string, string)>();
        var treatmentOf = new Dictionary<(string, string), ColumnTreatmentOf>();
        var computed = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (dataset, column, treatment) in treatments.All)
        {
            if (!tables.TryGetValue(dataset, out var table) || (table["columns"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(c => c["name"]?.GetValue<string>() == column) is not { } def) continue;
            var kind = (json["sources"] as JsonObject)?[table["x_source"]?.GetValue<string>() ?? ""]?["kind"]?.GetValue<string>();
            var effective = treatment;
            if (treatment.Treatment != ColumnTreatment.Hide && (kind == "ssas" || (treatment.Treatment == ColumnTreatment.Mask && treatment.Mask == MaskKind.Fixed && def["datatype"]?.GetValue<string>() is { } type && type != "String")))
                effective = new ColumnTreatmentOf(ColumnTreatment.Hide);
            treatmentOf[(dataset, column)] = effective;
            switch (effective.Treatment)
            {
                case ColumnTreatment.Hide: hiddenColumns.Add((dataset, column)); break;
                case ColumnTreatment.Mask when effective.Mask == MaskKind.Hash: hashedColumns.Add((dataset, column)); break;
                default:
                    var name = column + Suffix;
                    var plain = (def["expression"]?["dialects"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(d => d["dialect"]?.GetValue<string>() == "ANSI_SQL")?["expression"]?.GetValue<string>();
                    if (plain is null) { hiddenColumns.Add((dataset, column)); break; }   // no ANSI expression to wrap: hide it
                    ((JsonArray)table["columns"]!).Add(DerivedColumn(def, name, effective));
                    derived[(dataset, column)] = name;
                    if (!computed.TryGetValue(dataset, out var list)) computed[dataset] = list = [];
                    list.Add((effective.Treatment == ColumnTreatment.Null ? $"NULLIF({plain}, {plain})" : $"COALESCE(NULLIF({plain}, {plain}), '{ColumnMasks.FixedValue}')") + " AS " + name);
                    break;
            }
        }

        // the table is read through a derived table that adds the computed columns, so the real column is still there for the joins and the row rules that read it
        foreach (var (dataset, expressions) in computed)
            tables[dataset]["source"] = $"(SELECT *, {string.Join(", ", expressions)} FROM {tables[dataset]["source"]!.GetValue<string>()})";

        var hidden = new HashSet<string>(StringComparer.Ordinal);
        var treated = new Dictionary<string, ColumnTreatmentOf>(StringComparer.Ordinal);
        var hashed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (dimension, dimensionNode) in (json["dimensions"] as JsonObject) ?? [])
            foreach (var (attribute, node) in (dimensionNode?["attributes"] as JsonObject) ?? [])
            {
                var key = dimension + "." + attribute;
                if (node?["bindings"] is not JsonObject bindings) continue;
                foreach (var binding in bindings.Select(b => b.Value).OfType<JsonObject>())
                {
                    if (binding["table"]?.GetValue<string>() is not { } table || binding["column"]?.GetValue<string>() is not { } column) continue;
                    var id = (table, column);
                    if (hiddenColumns.Contains(id)) hidden.Add(key);
                    else if (derived.TryGetValue(id, out var name)) { binding["column"] = name; binding.Remove("native"); treated[key] = treatmentOf[id]; }
                    else if (hashedColumns.Contains(id)) { hashed.Add(key); treated[key] = treatmentOf[id]; }
                }
            }

        // a treated attribute does not list its canonical members (they are the values), and nothing that sorts or filters by a treated one is kept
        var restricted = new HashSet<string>(treated.Keys, StringComparer.Ordinal);
        foreach (var key in restricted) (AttributeNode(json, key))?.Remove("members");

        var hiddenMeasures = new HashSet<string>(StringComparer.Ordinal);
        var measures = (json["measures"] as JsonObject) ?? [];
        bool changed;
        do
        {
            changed = false;
            foreach (var (name, node) in measures.ToList())
            {
                if (node is not JsonObject measure || hiddenMeasures.Contains(name)) continue;
                var drop = false;
                if (measure["of"] is JsonObject of && of["table"]?.GetValue<string>() is { } table && of["column"]?.GetValue<string>() is { } column)
                {
                    if (hiddenColumns.Contains((table, column))) drop = true;
                    else if (derived.TryGetValue((table, column), out var rebound) && measure["agg"]?.GetValue<string>() is "count" or "count_distinct") of["column"] = rebound;
                    else if (derived.ContainsKey((table, column))) drop = true;   // a sum or a minimum of a masked value
                }

                drop |= Mentions(measure["filters"], hidden) || Mentions(measure["filters"], restricted);
                if (measure["semantics"]?["grain"] is JsonArray grain) foreach (var item in grain.ToList()) if (item?.GetValue<string>() is { } named && (hidden.Contains(named) || restricted.Contains(named))) grain.Remove(item);   // a hint about the grain, not a reading
                if (!drop && measure["compute"] is { } compute) drop = Mentions(compute, hiddenMeasures);
                if (drop) { hiddenMeasures.Add(name); changed = true; }
            }
        } while (changed);
        foreach (var name in hiddenMeasures) measures.Remove(name);

        foreach (var key in hidden)
        {
            var dot = key.IndexOf('.', StringComparison.Ordinal);
            if (json["dimensions"]?[key[..dot]]?["attributes"] is JsonObject attributes) attributes.Remove(key[(dot + 1)..]);
        }

        foreach (var (dimension, node) in ((json["dimensions"] as JsonObject) ?? []).ToList())
        {
            if (node?["attributes"] is not JsonObject { Count: > 0 } left) { ((JsonObject)json["dimensions"]!).Remove(dimension); continue; }
            foreach (var attribute in left.Select(a => a.Value).OfType<JsonObject>())
            {
                if (attribute["sort_key"]?.GetValue<string>() is { } sort && (hidden.Contains(dimension + "." + sort) || restricted.Contains(dimension + "." + sort))) attribute.Remove("sort_key");
                if (attribute["date"]?.GetValue<string>() is { } date && (hidden.Contains(date) || restricted.Contains(date))) attribute.Remove("date");
            }
        }

        var gone = new HashSet<string>(hidden.Concat(hiddenMeasures), StringComparer.Ordinal);
        Prune(json["hierarchies"] as JsonObject, gone);
        Prune(json["calendars"] as JsonObject, gone);
        Prune(json["segments"] as JsonObject, new HashSet<string>(gone.Concat(restricted), StringComparer.Ordinal));
        if (json["queries"] is JsonObject queries)
            foreach (var (name, node) in queries.ToList())
                if (MentionsText(node, gone)) queries.Remove(name);

        var view = CatalogLoader.LoadJson(json);
        return new CatalogColumnView(view, treated, hashed, gone);
    }

    private static JsonObject? AttributeNode(JsonObject json, string key)
    {
        var dot = key.IndexOf('.', StringComparison.Ordinal);
        return json["dimensions"]?[key[..dot]]?["attributes"]?[key[(dot + 1)..]] as JsonObject;
    }

    /// <summary>An entry of a section (a hierarchy, a calendar, a segment) goes when it names any of the names anywhere.</summary>
    private static void Prune(JsonObject? section, IReadOnlySet<string> names)
    {
        if (section is null) return;
        foreach (var (name, node) in section.ToList())
            if (Mentions(node, names)) section.Remove(name);
    }

    /// <summary>Whether any string value or property name below the node is one of the names (an attribute or measure is named exactly in a definition).</summary>
    private static bool Mentions(JsonNode? node, IReadOnlySet<string> names)
    {
        if (names.Count == 0) return false;
        switch (node)
        {
            case JsonValue value: return value.TryGetValue<string>(out var text) && names.Contains(text);
            case JsonObject obj: return obj.Any(p => names.Contains(p.Key) || Mentions(p.Value, names));
            case JsonArray array: return array.Any(item => Mentions(item, names));
            default: return false;
        }
    }

    /// <summary>The same for text that holds names inside a statement (a saved query's SQL or its request, which may write an attribute as <c>customers.customer</c> or as the result's <c>customers_customer</c>).</summary>
    private static bool MentionsText(JsonNode? node, IReadOnlySet<string> names)
    {
        if (names.Count == 0 || node is null) return false;
        var text = node.ToJsonString();
        return names.Any(n => text.Contains(n, StringComparison.OrdinalIgnoreCase) || text.Contains(Catalog.ColName(n), StringComparison.OrdinalIgnoreCase));
    }

    private static JsonObject DerivedColumn(JsonObject original, string name, ColumnTreatmentOf treatment)
    {
        var column = new JsonObject
        {
            ["name"] = name,
            ["expression"] = new JsonObject { ["dialects"] = new JsonArray(new JsonObject { ["dialect"] = "ANSI_SQL", ["expression"] = name }) },
        };
        if (original["datatype"] is { } datatype) column["datatype"] = datatype.DeepClone();
        if (treatment.Treatment == ColumnTreatment.Mask) column["datatype"] = "String";
        return column;
    }
}
