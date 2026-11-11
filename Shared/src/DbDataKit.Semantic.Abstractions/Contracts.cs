using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace DbDataKit.Semantic.Abstractions;

/// <summary>Serializer settings shared by every contract: snake_case names, nulls omitted.</summary>
public static class QisJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <summary>
    /// Stable hash of a request: keys sorted, compact output, first 16 hex chars of SHA-256.
    /// Hashes are stable within one implementation. Number formatting can differ between serializers, so they are not portable.
    /// </summary>
    public static string RequestHash(JsonNode doc)
    {
        var canonical = Canonicalize(doc)?.ToJsonString() ?? "null";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(bytes)[..16];
    }

    private static JsonNode? Canonicalize(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.OrderBy(p => p.Key, StringComparer.Ordinal)
                                        .Select(p => KeyValuePair.Create(p.Key, Canonicalize(p.Value)))),
        JsonArray a => new JsonArray(a.Select(Canonicalize).ToArray()),
        null => null,
        _ => JsonNode.Parse(node.ToJsonString()),
    };
}

// ---------- Errors (core error format) ----------

public sealed record QisError(string Spec, string Code, string Path, string Message, List<string>? Suggestions = null);

// ---------- Filters (core filter grammar) ----------

/// <summary>A filter is a leaf ({attribute or measure, operator, values}) or an and/or of filters.</summary>
public abstract record Filter
{
    public static Filter Parse(JsonNode node)
    {
        var o = node.AsObject();
        if (o["and"] is JsonArray and) return new AndFilter(and.Select(n => Parse(n!)).ToList());
        if (o["or"] is JsonArray or) return new OrFilter(or.Select(n => Parse(n!)).ToList());
        var values = o["values"] is JsonArray v ? v.Select(x => x!.DeepClone()).ToList() : new List<JsonNode>();
        var onMeasure = o["measure"] is not null;
        return new LeafFilter((o["attribute"] ?? o["measure"])!.GetValue<string>(), o["operator"]!.GetValue<string>(), values,
            onMeasure, o["count"]?.GetValue<int>());
    }

    public abstract IEnumerable<string> Names();

    /// <summary>Display text used in covers.filters_applied, e.g. "date.fiscal_quarter equals FY27-Q1".</summary>
    public abstract string Text();
}

/// <summary>
/// A condition on one attribute, or (<c>OnMeasure</c>) on the output of one measure. <c>Count</c> is the number of whole periods
/// of <c>last_complete</c>.
/// </summary>
public sealed record LeafFilter(string Name, string Operator, List<JsonNode> Values, bool OnMeasure = false, int? Count = null) : Filter
{
    public override IEnumerable<string> Names() => [Name];
    public override string Text() =>
        $"{Name} {Operator} {(Count is { } c ? c + " " : "")}{string.Join(", ", Values.Select(v => v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : v.ToJsonString()))}".TrimEnd();
}

public sealed record AndFilter(List<Filter> Items) : Filter
{
    public override IEnumerable<string> Names() => Items.SelectMany(i => i.Names()).Distinct();
    public override string Text() => "(" + string.Join(" and ", Items.Select(i => i.Text())) + ")";
}

public sealed record OrFilter(List<Filter> Items) : Filter
{
    public override IEnumerable<string> Names() => Items.SelectMany(i => i.Names()).Distinct();
    public override string Text() => "(" + string.Join(" or ", Items.Select(i => i.Text())) + ")";
}

/// <summary>
/// A catalog measure in a request: just the name, or an object with an alias, filters or overrides.
/// <c>Name</c> is the output name (the alias, else the measure); the column is its logical name.
/// </summary>
public sealed record MeasureRef(
    string Name, string Measure, bool IsOverride, List<Filter> Filters, string? Spread,
    bool HasAllocate, bool HasRollup, bool HasEmpty, bool Acknowledge, JsonNode Raw)
{
    /// <summary>The allocate object of a spread override: how to split this measure onto finer rows.</summary>
    public JsonObject? Allocate => (Raw["spread"] as JsonObject)?["allocate"] as JsonObject;

    /// <summary>A rollup override: the attribute and the rule to combine the measure across it.</summary>
    public IReadOnlyList<(string Attribute, string Rule)> Rollup =>
        (Raw["rollup"] as JsonObject)?.Select(kv => (kv.Key, kv.Value!.GetValue<string>())).ToList() ?? [];

    /// <summary>skip (default) or zero: whether members with no value count as zero in an average.</summary>
    public string? Empty => Raw["empty"]?.GetValue<string>();

    public static bool IsRef(JsonNode n) => n.GetValueKind() == JsonValueKind.String || (n["measure"] is not null && n["transform"] is null);

    public static MeasureRef Parse(JsonNode n)
    {
        if (n.GetValueKind() == JsonValueKind.String)
        {
            var name = n.GetValue<string>();
            return new MeasureRef(name, name, false, [], null, false, false, false, false, n);
        }
        var measure = n["measure"]!.GetValue<string>();
        var spread = n["spread"];
        return new MeasureRef(
            n["as"]?.GetValue<string>() ?? measure, measure, true,
            n["filters"]?.AsArray().Select(f => Filter.Parse(f!)).ToList() ?? [],
            spread is JsonValue ? spread.GetValue<string>() : null,
            spread is JsonObject, n["rollup"] is not null, n["empty"] is not null,
            n["acknowledge"]?.GetValue<bool>() ?? false, n);
    }
}

/// <summary>A transform entry (<c>{"as", "transform": kind, "measure", ...}</c>): a measure computed at another level of detail or relative to other rows.</summary>
public sealed record TransformRef(string Alias, string Type, string Measure, JsonObject Raw)
{
    public static bool IsRef(JsonNode n) => n.GetValueKind() == JsonValueKind.Object && n["transform"] is JsonValue;

    public static TransformRef Parse(JsonNode n)
    {
        var t = n.AsObject();
        return new TransformRef(t["as"]!.GetValue<string>(), t["transform"]!.GetValue<string>(), t["measure"]!.GetValue<string>(), t);
    }

    public string? Str(string key) => Raw[key]?.GetValue<string>();
    public List<string> List(string key) => Raw[key]?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? [];
}

/// <summary>
/// Subtotal and total rows. <c>Rollup</c> lists attributes to roll up, outermost first, like SQL ROLLUP;
/// the other query attributes always partition. A chain keeps a prefix of the list in each set; a cube keeps every subset; <c>GrandTotal</c> adds the set that keeps no attribute at all.
/// </summary>
public sealed record TotalsSpec(List<string> Rollup, bool GrandTotal, string? Compute = null, bool Cube = false);

// ---------- Semantic request (typed view over the JSON document) ----------

/// <summary>
/// The engine keeps the original JSON (for hashing and replay) and reads it through this view.
/// Measure entries that aren't plain names stay as JSON, since level 1 returns them as remaining steps.
/// </summary>
public sealed class SemanticRequest
{
    public JsonObject Document { get; }
    public List<JsonNode> MeasureEntries { get; }
    public List<string> Attributes { get; }
    public List<Filter> Filters { get; }
    public List<(string Member, string Direction)> Order { get; }
    public int Limit { get; }
    public TotalsSpec? Totals { get; }
    public List<Filter> PostFilters { get; }
    /// <summary>
    /// Filters on measures (HAVING): the engine moves them out of <c>filters</c> into <c>having</c> before it reads the
    /// request, because they apply to the answer, not to the rows that make it up. Their members are output names.
    /// </summary>
    public List<Filter> Having { get; }

    public SemanticRequest(JsonObject document)
    {
        Document = document;
        var q = document["query"]!.AsObject();
        MeasureEntries = q["measures"]!.AsArray().Select(n => n!).ToList();
        Attributes = q["attributes"]?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? [];
        Filters = q["filters"]?.AsArray().Select(n => Filter.Parse(n!)).ToList() ?? [];
        Order = q["order"]?.AsArray()
                    .Select(n => (n![0]!.GetValue<string>(), n[1]!.GetValue<string>())).ToList() ?? [];
        Limit = q["limit"]!.GetValue<int>();
        PostFilters = q["post_filters"]?.AsArray().Select(n => Filter.Parse(n!)).ToList() ?? [];
        Having = q["having"]?.AsArray().Select(n => Filter.Parse(n!)).ToList() ?? [];
        Totals = q["totals"] is JsonObject t
            ? new TotalsSpec(t["subtotal_by"]!.AsArray().Select(n => n!.GetValue<string>()).ToList(),
                             t["grand_total"]?.GetValue<bool>() ?? false, t["compute"]?.GetValue<string>(), t["cube"]?.GetValue<bool>() ?? false)
            : null;
    }

    /// <summary>Catalog measures, plain or with overrides, in request order.</summary>
    public IEnumerable<MeasureRef> Measures => MeasureEntries.Where(MeasureRef.IsRef).Select(MeasureRef.Parse);

    /// <summary>Transforms, in request order.</summary>
    public IEnumerable<TransformRef> Transforms => MeasureEntries.Where(TransformRef.IsRef).Select(TransformRef.Parse);

    /// <summary>Ad hoc aggregates and transforms: forms the engine returns as remaining steps when it can't run them.</summary>
    public IEnumerable<JsonNode> OtherMeasureEntries => MeasureEntries.Where(n => !MeasureRef.IsRef(n));

    /// <summary>The output column names of measures and transforms, in the order the request listed them.</summary>
    public IEnumerable<string> OutputOrder => MeasureEntries
        .Where(n => MeasureRef.IsRef(n) || TransformRef.IsRef(n))
        .Select(n => MeasureRef.IsRef(n) ? MeasureRef.Parse(n).Name : TransformRef.Parse(n).Alias);
}

// ---------- Result contract ----------

public sealed record NativeQuery(string Dialect, string Text);

public sealed record FilterNotApplied(string Filter, string Reason);

public sealed record Covers(
    List<string> Measures,
    List<string> FiltersApplied,
    List<FilterNotApplied> FiltersNotApplied);

public sealed record ResultSet(
    string Id,
    string? Source,
    List<string> Grain,
    Covers? Covers,
    JsonObject Descriptor,
    NativeQuery? NativeQuery,
    List<Dictionary<string, object?>> Rows)
{
    private List<Dictionary<string, object?>>? _rows = Rows;

    /// <summary>A result set whose rows arrived as Arrow columns (plan 19 J24): the dictionaries are built when something asks for <see cref="Rows"/>, and the sandbox loads from the columns without them.</summary>
    public ResultSet(string id, string? source, List<string> grain, Covers? covers, JsonObject descriptor, NativeQuery? nativeQuery, ColumnarRows columnar)
        : this(id, source, grain, covers, descriptor, nativeQuery, (List<Dictionary<string, object?>>)null!) => Columnar = columnar;

    [System.Text.Json.Serialization.JsonIgnore]
    public ColumnarRows? Columnar { get; init; }

    /// <summary>The rows; for a columnar result set, built once from the columns on first use.</summary>
    public List<Dictionary<string, object?>> Rows
    {
        get => _rows ??= Columnar!.ToRows().Rows;
        init => _rows = value;
    }

    /// <summary>The number of rows, without building them for a columnar result set.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int RowCount => _rows?.Count ?? Columnar!.Count;
}

public sealed record Warning(string Code, string Message);

/// <summary>A relative range as resolved: where it was in the request, the clock date, and the dates it became (both ends inclusive). A range of whole periods also names the level attribute and the members it filters on.</summary>
public sealed record ResolvedRange(string Path, string Today, string Start, string End, string? Attribute = null, List<string>? Members = null, string? Operator = null);

public sealed record Result
{
    public string Kind { get; init; } = "result";
    public required string Status { get; init; }            // complete | partial | failed
    public string? RequestHash { get; init; }
    public required int EngineLevel { get; init; }
    public List<ResultSet>? ResultSets { get; init; }
    public ResultSet? Combined { get; init; }
    /// <summary>Remaining logical plan steps, kept as JSON so new step types don't need contract changes.</summary>
    public List<JsonObject>? Remaining { get; init; }
    public string? ReasonCode { get; init; }
    public List<Warning>? Warnings { get; init; }
    public List<QisError>? Errors { get; init; }
    /// <summary>The dates each relative range resolved to in this run, with the clock date used.</summary>
    public List<ResolvedRange>? Resolved { get; init; }

    public static Result Failed(int level, string? hash, List<QisError> errors) =>
        new() { Status = "failed", EngineLevel = level, RequestHash = hash, Errors = errors };
}

// ---------- Completion ----------

public sealed record Completion(string Kind, string RequestHash, List<int> Completes, string Sql, List<string>? Maps = null);
