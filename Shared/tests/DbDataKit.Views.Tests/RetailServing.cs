using System.Text.Json.Nodes;
using Apache.Arrow;
using Apache.Arrow.Types;
using DbDataKit.Serving;

namespace DbDataKit.Views.Tests;

/// <summary>
/// A fake serving core over a small, fixed **retail sample**: the names of the retail template's semantic model (<c>retail_sales</c>: <c>sales.net_amount</c>, <c>sales.units</c>, <c>sales.margin_rate</c>, <c>customers.region</c>,
/// <c>products.category</c>, <c>products.product</c>, <c>date.year_month</c>) over 960 generated sales lines (24 products, 4 regions, 10 months), answered as the semantic module answers: Arrow, with the engine's descriptor in the schema's metadata (<c>qis.descriptor</c>:
/// kinds, bindings, additivity, the rows that are totals). It evaluates the part of a semantic request the widgets make (equals and the comparison filters, grouping, order, limit, totals), resolves <c>{"param": name}</c> from the
/// request's <c>parameters</c> as the engine does (and refuses an unbound one with the engine's code), and **applies an access rule of its own**: a caller whose claims say <c>region: East</c> sees only the East's lines.
/// It records every request with its context, and a hook lets a test delay, deny or fail one.
/// </summary>
internal sealed class RetailServing : IServingQueries
{
    public sealed record Line(string Region, string Category, string Product, long YearMonth, long Units, double Net, double Margin);

    private static readonly string[] Regions = ["West", "Central", "East", "South"];
    // 24 products in four categories, listed from the best seller down (units fall with the position, so a table sorted by units reads in this order)
    private static readonly (string Product, string Category, double Price, double Rate)[] Products =
    [
        ("Trail Jacket", "Apparel", 120, 0.42), ("Rain Shell", "Apparel", 95, 0.38), ("Fleece Pullover", "Apparel", 70, 0.45), ("Hiking Boot", "Footwear", 140, 0.36),
        ("Trail Runner", "Footwear", 110, 0.40), ("Camp Sandal", "Footwear", 55, 0.48), ("Daypack 22L", "Outdoor", 85, 0.44), ("Duffel 60L", "Outdoor", 110, 0.41),
        ("Water Bottle", "Accessories", 20, 0.60), ("Wool Sock 3-pack", "Accessories", 28, 0.52), ("Beanie", "Accessories", 25, 0.55), ("Gloves", "Accessories", 45, 0.50),
        ("Base Layer Top", "Apparel", 60, 0.47), ("Base Layer Bottom", "Apparel", 60, 0.47), ("Tent 2P", "Outdoor", 260, 0.33), ("Sleeping Bag", "Outdoor", 180, 0.35),
        ("Headlamp", "Accessories", 40, 0.53), ("Trekking Poles", "Outdoor", 75, 0.43), ("Sun Hat", "Accessories", 30, 0.54), ("Rain Pants", "Apparel", 80, 0.39),
        ("Windbreaker", "Apparel", 90, 0.40), ("Camp Stove", "Outdoor", 65, 0.37), ("Insulated Jacket", "Apparel", 200, 0.38), ("Trail Shorts", "Apparel", 50, 0.46),
    ];

    public static IReadOnlyList<Line> Lines { get; } = Generate();

    private static List<Line> Generate()
    {
        var lines = new List<Line>();
        for (var r = 0; r < Regions.Length; r++)
            for (var p = 0; p < Products.Length; p++)
                for (var m = 0; m < 10; m++)
                {
                    var units = 120 - 5 * p + (r * 7 + m * 5 + p) % 5;
                    var net = Math.Round(units * Products[p].Price * (1 - 0.02 * ((r + m) % 3)), 2);
                    lines.Add(new Line(Regions[r], Products[p].Category, Products[p].Product, 202601 + m, units, net, Math.Round(net * Products[p].Rate, 2)));
                }
        return lines;
    }

    public List<(ServingContext Context, ServingRequest Request)> Received { get; } = [];

    /// <summary>Called first with each request: a test delays, denies or fails it here.</summary>
    public Func<ServingContext, ServingRequest, Task>? Before { get; set; }

    private sealed record Attribute(string Name, Func<Line, object> Of, string Type);

    private static readonly Dictionary<string, Attribute> Attributes = new[]
    {
        new Attribute("customers.region", l => l.Region, "string"), new Attribute("products.category", l => l.Category, "string"),
        new Attribute("products.product", l => l.Product, "string"), new Attribute("date.year_month", l => l.YearMonth, "integer"),
    }.ToDictionary(a => a.Name);

    private sealed record Measure(string Name, string Type, string Additivity, Func<IEnumerable<Line>, double> Of);

    private static readonly Dictionary<string, Measure> Measures = new[]
    {
        new Measure("sales.net_amount", "number", "additive", ls => ls.Sum(l => l.Net)),
        new Measure("sales.net_amount_prev", "number", "additive", ls => Math.Round(ls.Sum(l => l.Net) * 0.9, 2)),
        new Measure("sales.units", "integer", "additive", ls => ls.Sum(l => l.Units)),
        new Measure("sales.margin_amount", "number", "additive", ls => ls.Sum(l => l.Margin)),
        new Measure("sales.margin_rate", "number", "non_additive", ls => { var n = ls.Sum(l => l.Net); return n == 0 ? 0 : Math.Round(ls.Sum(l => l.Margin) / n, 4); }),
    }.ToDictionary(m => m.Name);

    public async Task<IServingAnswer> RunAsync(ServingContext context, ServingRequest request, CancellationToken cancellationToken = default)
    {
        Received.Add((context, request));
        if (Before is not null) await Before(context, request).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (request is not ServingRequest.Semantic semantic) throw new ServingInvalidException($"The retail sample runs semantic requests, not {request.GetType().Name}.");
        var document = JsonNode.Parse(semantic.Json) as JsonObject ?? throw new ServingInvalidException("schema_invalid: a request is a JSON object.");
        // the access rule: what this caller's claims reach
        var visible = Lines.Where(l => !context.Caller.Claims.TryGetValue("region", out var region) || l.Region == region).ToList();
        return document["kind"]?.GetValue<string>() switch
        {
            "members" => Members(document, visible, semantic.Source),
            "query" => Query(document, visible, semantic.Source),
            var other => throw new ServingInvalidException($"schema_invalid: the retail sample does not run '{other}'."),
        };
    }

    public Task<ServingStatementInfo> PrepareAsync(ServingContext context, ServingRequest.Sql statement, CancellationToken cancellationToken = default) => Task.FromResult(new ServingStatementInfo(0));

    // ---- members ----

    private static IServingAnswer Members(JsonObject request, List<Line> visible, string source)
    {
        var name = request["attribute"]!.GetValue<string>();
        if (!Attributes.TryGetValue(name, out var attribute)) throw new ServingInvalidException($"unknown_attribute: {name} is not an attribute of the model.");
        var members = visible.Select(l => attribute.Of(l)).Distinct().ToList();
        if (request["limit"] is JsonValue lv && lv.TryGetValue<long>(out var limit)) members = members.Take((int)limit).ToList();
        var rows = members.Select(m => new JsonObject { ["member"] = m is long l ? JsonValue.Create(l) : JsonValue.Create((string)m) }).ToList();
        var fields = new JsonArray(new JsonObject { ["name"] = "member", ["type"] = attribute.Type, ["x_kind"] = "column" });
        return Answer(fields, rows, null, source, "members");
    }

    // ---- query ----

    private static IServingAnswer Query(JsonObject document, List<Line> visible, string source)
    {
        var query = document["query"] as JsonObject ?? throw new ServingInvalidException("schema_invalid: a query has a `query`.");
        var parameters = document["parameters"] as JsonObject;
        var attributes = (query["attributes"] as JsonArray ?? []).Select(a => a!.GetValue<string>()).ToList();
        var measures = (query["measures"] as JsonArray ?? []).Select(m => m!.GetValue<string>()).ToList();
        foreach (var a in attributes) if (!Attributes.ContainsKey(a)) throw new ServingInvalidException($"unknown_member: {a} is not a catalog member.");
        foreach (var m in measures) if (!Measures.ContainsKey(m)) throw new ServingInvalidException($"unknown_member: {m} is not a catalog member.");

        // filters
        var lines = visible;
        foreach (var filter in (query["filters"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var name = filter["attribute"]!.GetValue<string>();
            var attribute = Attributes.GetValueOrDefault(name) ?? throw new ServingInvalidException($"unknown_member: {name} is not a catalog member.");
            var op = filter["operator"]!.GetValue<string>();
            var values = Values(filter["values"], parameters);
            lines = lines.Where(l => Matches(attribute.Of(l), op, values)).ToList();
        }

        var limit = Limit(query["limit"], parameters);
        var totals = query["totals"] as JsonObject;

        // the levels: 0 is the detail; the others keep a subset of subtotal_by
        var levels = new List<(int Level, HashSet<string> Kept, HashSet<string> Rolled)> { (0, [.. attributes], []) };
        if (totals is not null)
        {
            var by = (totals["subtotal_by"] as JsonArray ?? []).Select(a => a!.GetValue<string>()).ToList();
            var kept = new List<List<string>>();
            if (totals["cube"]?.GetValue<bool>() == true)
                for (var mask = (1 << by.Count) - 2; mask >= 0; mask--) kept.Add(by.Where((_, i) => (mask & (1 << i)) != 0).ToList());
            else
                for (var k = by.Count - 1; k >= 0; k--) kept.Add(by.Take(k).ToList());
            var grand = totals["grand_total"]?.GetValue<bool>() == true;
            foreach (var k in kept.Where(k => k.Count > 0 || grand))
                levels.Add((levels.Count, [.. attributes.Where(a => !by.Contains(a) || k.Contains(a))], [.. by.Where(a => !k.Contains(a))]));
        }

        var rows = new List<JsonObject>();
        foreach (var (level, kept, rolled) in levels)
        {
            // a level that keeps no attribute is one group of every line (a metric, a grand total)
            List<IEnumerable<Line>> groups = kept.Count == 0 ? [lines] : [.. lines.GroupBy(l => string.Join('\u001f', attributes.Where(kept.Contains).Select(a => Attributes[a].Of(l))))];
            var levelRows = new List<(JsonObject Row, List<object> Key)>();
            foreach (var g in groups)
            {
                var row = new JsonObject();
                var key = new List<object>();
                foreach (var a in attributes)
                {
                    var keep = kept.Contains(a);
                    var v = keep ? Attributes[a].Of(g.First()) : null;
                    row[ColName(a)] = v is long l ? JsonValue.Create(l) : v is string s ? JsonValue.Create(s) : null;
                    key.Add(v ?? "");
                }
                if (totals is not null) row["_rollup_level"] = (long)level;
                foreach (var m in measures) row[ColName(m)] = Measures[m].Type == "integer" ? JsonValue.Create((long)Measures[m].Of(g)) : JsonValue.Create(Math.Round(Measures[m].Of(g), 4));
                levelRows.Add((row, key));
            }
            rows.AddRange(Order(levelRows, query["order"] as JsonArray, parameters));
        }
        if (limit is { } n) rows = [.. Take(rows, n, totals is not null)];

        var fields = new JsonArray();
        foreach (var a in attributes) fields.Add(new JsonObject { ["name"] = ColName(a), ["type"] = Attributes[a].Type, ["x_kind"] = "attribute", ["x_binding"] = a });
        foreach (var m in measures)
            fields.Add(new JsonObject
            {
                ["name"] = ColName(m), ["type"] = Measures[m].Type, ["x_kind"] = "measure", ["x_binding"] = m, ["x_additivity"] = Measures[m].Additivity,
                ["x_semantics"] = new JsonObject { ["rollup"] = new JsonObject { ["default"] = Measures[m].Additivity == "additive" ? "sum" : "none" } },
            });
        JsonObject? x_totals = null;
        if (totals is not null)
        {
            fields.Add(new JsonObject { ["name"] = "_rollup_level", ["type"] = "integer", ["x_kind"] = "column" });
            x_totals = new JsonObject
            {
                ["level_field"] = "_rollup_level",
                ["subtotal_by"] = totals["subtotal_by"]?.DeepClone(),
                ["levels"] = new JsonArray(levels.Select(l => (JsonNode)new JsonObject
                {
                    ["level"] = l.Level, ["kind"] = l.Level == 0 ? "detail" : l.Kept.Count == 0 ? "grand_total" : "subtotal",
                    ["kept"] = new JsonArray(l.Kept.Select(k => (JsonNode)k).ToArray()), ["rolled_up"] = new JsonArray(l.Rolled.Select(k => (JsonNode)k).ToArray()),
                }).ToArray()),
            };
        }
        return Answer(fields, rows, x_totals, source, "combined");
    }

    /// <summary>The limit applies to the detail rows; the total rows a request asked for come with them.</summary>
    private static IEnumerable<JsonObject> Take(List<JsonObject> rows, int limit, bool totals) =>
        totals ? rows.Where(r => r["_rollup_level"]!.GetValue<long>() == 0).Take(limit).Concat(rows.Where(r => r["_rollup_level"]!.GetValue<long>() != 0)) : rows.Take(limit);

    private static IEnumerable<JsonObject> Order(List<(JsonObject Row, List<object> Key)> rows, JsonArray? order, JsonObject? parameters)
    {
        if (order is not { Count: > 0 }) return rows.Select(r => r.Row);
        IOrderedEnumerable<(JsonObject Row, List<object> Key)>? sorted = null;
        foreach (var entry in order.OfType<JsonArray>())
        {
            var field = ColName(entry[0]!.GetValue<string>());
            var desc = entry.Count > 1 && entry[1]!.GetValue<string>() == "desc";
            Func<(JsonObject Row, List<object> Key), IComparable?> key = r => r.Row[field] is JsonValue v ? (v.TryGetValue<double>(out var d) ? d : v.TryGetValue<long>(out var l) ? l : v.GetValue<string>()) : null;
            sorted = sorted is null ? (desc ? rows.OrderByDescending(key) : rows.OrderBy(key)) : (desc ? sorted.ThenByDescending(key) : sorted.ThenBy(key));
        }
        return sorted!.Select(r => r.Row);
    }

    private static string ColName(string member) => member.Replace('.', '_');

    // ---- parameters and filters, as the engine resolves them ----

    private static JsonNode? Bound(JsonNode? marker, JsonObject? parameters)
    {
        if (marker is not JsonObject { Count: 1 } o || o["param"] is not JsonValue v) return marker;
        var name = v.GetValue<string>();
        if (parameters is null || !parameters.TryGetPropertyValue(name, out var value)) throw new ServingInvalidException($"param_missing: {name} has no value and no default.");
        if (value is JsonArray { Count: 0 }) throw new ServingInvalidException($"param_empty_list: {name} is an empty list.");
        return value;
    }

    private static List<object> Values(JsonNode? node, JsonObject? parameters)
    {
        var result = new List<object>();
        void Add(JsonNode? n)
        {
            if (n is JsonArray a) { foreach (var item in a) Add(Bound(item, parameters)); return; }
            if (n is JsonValue v) result.Add(v.TryGetValue<long>(out var l) ? l : v.TryGetValue<double>(out var d) ? d : (object)v.GetValue<string>());
        }
        Add(Bound(node, parameters));
        return result;
    }

    private static int? Limit(JsonNode? node, JsonObject? parameters) => Bound(node, parameters) is JsonValue v && v.TryGetValue<long>(out var l) ? (int)l : null;

    private static bool Matches(object value, string op, List<object> values)
    {
        int Compare(object x) => value is long a && x is long b ? a.CompareTo(b) : string.CompareOrdinal(value.ToString(), x.ToString());
        return op switch
        {
            "equals" => values.Any(x => Compare(x) == 0),
            "not_equals" => values.All(x => Compare(x) != 0),
            "contains" => values.Any(x => value.ToString()!.Contains(x.ToString()!, StringComparison.OrdinalIgnoreCase)),
            "gt" => Compare(values[0]) > 0, "gte" => Compare(values[0]) >= 0, "lt" => Compare(values[0]) < 0, "lte" => Compare(values[0]) <= 0,
            _ => throw new ServingInvalidException($"invalid_operator: {op} is not an operator of the model."),
        };
    }

    // ---- the answer, as the semantic module gives it ----

    private static IServingAnswer Answer(JsonArray fields, List<JsonObject> rows, JsonObject? totals, string source, string name)
    {
        var descriptor = new JsonObject { ["descriptor_version"] = "1.0", ["name"] = name, ["schema"] = new JsonObject { ["fields"] = fields } };
        if (totals is not null) descriptor["x_totals"] = totals;
        var arrowFields = new List<Field>();
        var arrays = new List<IArrowArray>();
        foreach (var field in fields)
        {
            var column = field!["name"]!.GetValue<string>();
            var values = rows.Select(r => r[column]).ToList();
            switch (field["type"]!.GetValue<string>())
            {
                case "integer":
                    var longs = new Int64Array.Builder();
                    foreach (var v in values) if (v is null) longs.AppendNull(); else longs.Append(v.GetValue<long>());
                    arrays.Add(longs.Build()); arrowFields.Add(new Field(column, Int64Type.Default, true)); break;
                case "number":
                    var doubles = new DoubleArray.Builder();
                    foreach (var v in values) if (v is null) doubles.AppendNull(); else doubles.Append(v.GetValue<double>());
                    arrays.Add(doubles.Build()); arrowFields.Add(new Field(column, DoubleType.Default, true)); break;
                default:
                    var text = new StringArray.Builder();
                    foreach (var v in values) if (v is null) text.AppendNull(); else text.Append(v.GetValue<string>());
                    arrays.Add(text.Build()); arrowFields.Add(new Field(column, StringType.Default, true)); break;
            }
        }
        var schema = new Schema(arrowFields, new Dictionary<string, string> { ["qis.status"] = "complete", ["qis.descriptor"] = descriptor.ToJsonString() });
        return new TableAnswer(new RecordBatch(schema, arrays, rows.Count), new ServingProvenance(name, null, "prod", source));
    }

    private sealed class TableAnswer(RecordBatch batch, ServingProvenance provenance) : IServingAnswer
    {
        public Schema Schema => batch.Schema;
        public ServingProvenance Provenance { get; } = provenance;

        public async IAsyncEnumerable<RecordBatch> ReadBatchesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return batch;
            await Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
