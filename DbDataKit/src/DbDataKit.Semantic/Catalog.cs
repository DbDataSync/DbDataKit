using DbDataKit.Semantic.Abstractions;
using System.Text.Json.Nodes;

namespace DbDataKit.Semantic;

public sealed class JoinDef
{
    public string Table { get; set; } = "";
    public string Alias { get; set; } = "";
    public string Condition { get; set; } = "";

    /// <summary>For a join that hangs off another dimension (a snowflake), the key of that join; null when it joins to the fact table.</summary>
    public string? Parent { get; set; }

    /// <summary>
    /// For the join a datetime dimension makes to a date table: the datetime column (as written in a query, <c>f.ordered_at</c>) whose local date is matched
    /// to <see cref="DateColumn"/>. The condition depends on the request's zone, so the emitter writes it; <see cref="Condition"/> is empty.
    /// The join is a left join, so a day the date table lacks leaves its attributes blank instead of dropping the rows.
    /// </summary>
    public string? LocalDateOf { get; set; }
    public string? DateColumn { get; set; }
}

public sealed class SourceDef
{
    public string Kind { get; set; } = "";                 // sqlserver | ssas
    public JoinDef Fact { get; set; } = new();
    /// <summary>How this source computes subtotals (native, engine or auto) unless a request says otherwise; null leaves it to the engine.</summary>
    public string? Totals { get; set; }
    public Dictionary<string, JoinDef> Joins { get; set; } = new();
    /// <summary>
    /// How this source stores datetime values: <c>UTC</c> (the default), or the IANA zone whose wall-clock time its datetime columns hold.
    /// </summary>
    public string DatetimeZone { get; set; } = "UTC";
}

public sealed class BindingDef
{
    public string? Join { get; set; }
    public string Column { get; set; } = "";
    public string? Dax { get; set; }
}

/// <summary>How an ad hoc dimension relates to a catalog dimension: a registered table's group column, mapped to a key.</summary>
public sealed class AdHocRelation
{
    public string TableName { get; set; } = "";
    public int TableVersion { get; set; }
    public string GroupField { get; set; } = "";
    public string KeyField { get; set; } = "";
    public string KeyAttribute { get; set; } = "";
    public string? MapId { get; set; }
    public bool Confirmed { get; set; }

    /// <summary>
    /// The table column holding each row's share of its key. When set, the relation may be many-to-many: a key's value is
    /// split across its groups by these weights, which must sum to 1 per key.
    /// </summary>
    public string? WeightField { get; set; }
}

/// <summary>
/// A dimension: the table that describes the context of fact rows (date, customers). What a query groups and filters by are its attributes
/// (<see cref="AttributeDef"/>); the dimension holds what is true of all of them, such as the role its own name reads.
/// </summary>
public sealed class DimensionDef
{
    public string? Label { get; set; }

    /// <summary>For a dimension a fact table reaches in several roles: the one its own name reads unless a measure says otherwise.</summary>
    public string? DefaultRole { get; set; }
}

public sealed class AttributeDef
{
    /// <summary>Set for dimensions registered from ad hoc tables; null for governed catalog dimensions.</summary>
    public AdHocRelation? Relation { get; set; }

    public string Type { get; set; } = "string";
    public string? SortKey { get; set; }
    /// <summary>The canonical members when the catalog lists them.</summary>
    public List<string>? MemberList { get; set; }
    /// <summary>The source whose dimension table holds the canonical members.</summary>
    public string? MembersFrom { get; set; }
    public List<string> Synonyms { get; set; } = new();
    public Dictionary<string, BindingDef> Bindings { get; set; } = new();

    /// <summary>For a bound role-playing dimension: the role its bare name means unless a measure says otherwise (the <c>role</c> of its binding).</summary>
    public string? DefaultRole { get; set; }

    /// <summary>For an instance derived from a base dimension and a role (<c>ship_date.fiscal_month</c>): the base's name and the role. Null otherwise.</summary>
    public (string Base, string Role)? Instance { get; set; }

    /// <summary>For a bucket of a datetime dimension (<c>orders.ordered_at_month</c>): the datetime dimension and the granularity. Null otherwise.</summary>
    public (string Base, string Granularity)? Bucket { get; set; }

    /// <summary>For a datetime dimension tied to a date dimension: that date dimension (<c>date.calendar_date</c>).</summary>
    public string? DateKey { get; set; }

    /// <summary>True when the dimension's labels are periods that sort in time order (its bound field is a time field), so a string dimension can be compared with gt, gte, lt and lte.</summary>
    public bool IsTime { get; set; }

    /// <summary>For an attribute of that date dimension read through a datetime (<c>orders.ordered_at_fiscal_month</c>): the datetime and the date dimension's own attribute.</summary>
    public (string Datetime, string Attribute)? Linked { get; set; }
}

/// <summary>An operand of a derived measure: a measure, a number, or another operation.</summary>
public sealed record ComputeArg(string? Measure, decimal? Number, ComputeSpec? Nested);

/// <summary>A closed operation (divide, subtract, add, multiply, coalesce) over measures, evaluated after rollup.</summary>
public sealed record ComputeSpec(string Op, List<ComputeArg> Args)
{
    /// <summary>Every catalog measure this operation reads, including inside nested operations.</summary>
    public IEnumerable<string> Measures() => Args.SelectMany(a => a.Measure is not null ? [a.Measure] : a.Nested?.Measures() ?? []);
}

/// <summary>A measure computed per entity first, then rolled up over that entity by the aggregate (avg, min or max).</summary>
/// <summary>One step of a rollup override: combine the measure across a dimension by a rule.</summary>
public sealed record RollupStep(string Entity, string Agg);

/// <summary>
/// A measure scanned finer, by these dimensions, and combined across them one step at a time, in order: with
/// {warehouse: sum, month: avg} the warehouses are added within each month, then the months averaged.
/// </summary>
public sealed record EntityRollup(IReadOnlyList<RollupStep> Steps, bool ZeroEmpty = false)
{
    public EntityRollup(string entity, string agg, bool zeroEmpty = false) : this([new RollupStep(entity, agg)], zeroEmpty) { }

    /// <summary>The first dimension to roll up, and its rule.</summary>
    public string Entity => Steps[0].Entity;
    public string Agg => Steps[0].Agg;
    public IEnumerable<string> Entities => Steps.Select(s => s.Entity);

    public bool Equals(EntityRollup? other) => other is not null && ZeroEmpty == other.ZeroEmpty && Steps.SequenceEqual(other.Steps);
    public override int GetHashCode() => HashCode.Combine(ZeroEmpty, string.Join("|", Steps.Select(s => s.Entity + ":" + s.Agg)));
}

/// <summary>How a measure is allocated onto finer rows: <c>By</c> is measure (weights from another measure) or equal.</summary>
public sealed record AllocateSpec(string By, string? WeightMeasure, string? Members, string Fallback, string? OffsetUnit = null, int Offset = -1);

/// <summary>A measure evaluated one calendar unit earlier and relabeled to the current members (the prior value of prior_period).</summary>
public sealed record PeriodShift(string Unit, int Offset = -1);

/// <summary>
/// A measure aggregated over a window of calendar members around each member of <c>Along</c>: from <c>StartPeriod</c>'s first member that holds it, or
/// <c>StartOffset</c> members back, through <c>EndOffset</c> members from it (year to date at each month, or the trailing twelve months).
/// </summary>
public sealed record WindowSpec(string Along, string? StartPeriod, int StartOffset, int EndOffset);

/// <summary>A measure read at one fixed member (<c>Base</c>) of the period attribute <c>Along</c>, for every other combination of the query's attributes: the base of an index. <c>Path</c> is where the transform sits in the request.</summary>
public sealed record IndexSpec(string Along, string Base, string Path);

public sealed class MeasureDef
{
    public string Type { get; set; } = "number";
    public string Source { get; set; } = "";
    public string Agg { get; set; } = "sum";
    public string Column { get; set; } = "";
    public string? Dax { get; set; }

    /// <summary>The DAX name of the model table (or table expression) tested with NOT ISEMPTY to keep a group that has fact rows: <c>nonempty.DAX</c> when the catalog gives it, otherwise the table in the DAX expression of the field the measure aggregates.</summary>
    public string? DaxFact { get; set; }
    public List<string> Synonyms { get; set; } = new();

    /// <summary>How the measure's numbers are shown (style, currency, decimals); null leaves it to the view.</summary>
    public System.Text.Json.Nodes.JsonObject? Format { get; set; }
    /// <summary>The role this measure reads for each role-playing dimension, when it isn't the dimension's default (<c>{date: ship_date}</c>).</summary>
    public Dictionary<string, string> Roles { get; set; } = new();
    public Dictionary<string, object> Semantics { get; set; } = new();

    /// <summary>
    /// For a derived measure (a ratio, a difference): the operation over other measures. It has no source of its own and
    /// is evaluated after rollup at every grain, so a ratio is never summed and a total is a ratio of totals.
    /// </summary>
    public ComputeSpec? Derived { get; set; }

    /// <summary>The filters applied to this measure only, on top of the query's. Same grammar as query filters.</summary>
    public List<Filter> Filters { get; set; } = new();

    /// <summary>On a request-scoped measure (an alias or override): the catalog measure it derives from.</summary>
    public string? Base { get; set; }

    /// <summary>On the hidden prior-period copy of a measure: scan the source for the period one unit earlier.</summary>
    public PeriodShift? Shift { get; set; }

    /// <summary>On the hidden copy of a measure for a <c>window</c> transform: scan the window around each member.</summary>
    public WindowSpec? Window { get; set; }

    /// <summary>On the hidden copy of a measure for an <c>index</c> transform: scan the base member, for each combination of the other attributes.</summary>
    public IndexSpec? Index { get; set; }

    /// <summary>On a per_entity measure: aggregate per entity at the source, then roll up over the entity here.</summary>
    public EntityRollup? Via { get; set; }

    /// <summary>On a request-scoped measure: split onto finer rows by weights, instead of repeating or leaving blank.</summary>
    public AllocateSpec? Allocate { get; set; }

    /// <summary>How many of <see cref="Filters"/>, counting from the end, the request added on top of the catalog's own.</summary>
    public int RequestFilters { get; set; }

    public MeasureDef Copy()
    {
        var copy = (MeasureDef)MemberwiseClone();
        copy.Semantics = new Dictionary<string, object>(Semantics);
        copy.Roles = new Dictionary<string, string>(Roles);
        copy.Filters = Filters.ToList();
        return copy;
    }
}

/// <summary>Levels of a hierarchy, coarse to fine. Each level is many-to-one onto the one above.</summary>
public sealed class HierarchyDef
{
    public List<string> Levels { get; set; } = new();
}

/// <summary>A calendar's date attribute and its levels (its periods), each the attribute holding that level's member for every day.</summary>
public sealed class CalendarDef
{
    public string Date { get; set; } = "";
    public Dictionary<string, string> Levels { get; set; } = new();

    /// <summary>True for the calendar a datetime dimension has by itself: hour to year in the request's zone, with no date table behind it.</summary>
    public bool Implicit { get; set; }

    /// <summary>For a datetime tied to a date dimension: the date dimension's own calendar, whose levels (fiscal months and the like) the datetime reads through its date.</summary>
    public CalendarDef? Base { get; set; }
}

public sealed class CatalogDoc
{
    /// <summary>The business time zone (IANA): the zone in which a day, a month or a quarter of a datetime is read, unless a request names another.</summary>
    public string Timezone { get; set; } = "UTC";
    /// <summary>Calendars that carry levels, by name.</summary>
    public Dictionary<string, CalendarDef> Calendars { get; set; } = new();
    /// <summary>Named filters a query can name; their filters are added to the query's own (AND).</summary>
    public Dictionary<string, List<Filter>> Segments { get; set; } = new();
    /// <summary>Calendar shifts: for a unit (fiscal_year), each time dimension and the dimension holding its member one unit earlier.</summary>
    public Dictionary<string, Dictionary<string, string>> Shifts { get; set; } = new();
    public Dictionary<string, HierarchyDef> Hierarchies { get; set; } = new();
    public Dictionary<string, SourceDef> Sources { get; set; } = new();
    public Dictionary<string, AttributeDef> Attributes { get; set; } = new();
    /// <summary>The dimensions, by name; each has attributes, which are keyed dimension.attribute in <see cref="Attributes"/>.</summary>
    public Dictionary<string, DimensionDef> Dimensions { get; set; } = new();
    public Dictionary<string, MeasureDef> Measures { get; set; } = new();
}

/// <summary>Catalog of sources, dimensions and measures.</summary>
public sealed class Catalog
{
    public CatalogDoc Doc { get; }

    /// <summary>The catalog document as validated, kept for export.</summary>
    public JsonObject Source { get; }

    internal Catalog(CatalogDoc doc, JsonObject source)
    {
        Doc = doc;
        Source = source;
    }

    public string Name => Source["name"]!.GetValue<string>();

    /// <summary>Loads and validates a catalog; throws <see cref="CatalogException"/> with the validation codes if it is invalid.</summary>
    public static Catalog Load(string path) => CatalogLoader.Load(path);

    /// <summary>
    /// A copy whose dimension list can grow without touching the original. Ad hoc dimensions belong to one
    /// session, so each session registers them on its own copy.
    /// </summary>
    public Catalog Clone() => new(new CatalogDoc
    {
        Sources = Doc.Sources,
        Measures = new Dictionary<string, MeasureDef>(Doc.Measures),
        Attributes = new Dictionary<string, AttributeDef>(Doc.Attributes),
        Dimensions = Doc.Dimensions,
        Hierarchies = Doc.Hierarchies,
        Shifts = Doc.Shifts,
        Calendars = Doc.Calendars,
        Segments = Doc.Segments,
        Timezone = Doc.Timezone,
    }, Source);

    private string? _zone;

    /// <summary>The zone the request in hand is read in: its own <c>timezone</c>, else the catalog's.</summary>
    public string Zone => _zone ?? Doc.Timezone;

    public IDisposable WithZone(string? zone)
    {
        var previous = _zone;
        _zone = zone;
        return new Restore(() => _zone = previous);
    }

    /// <summary>
    /// The period an attribute is: a level of a calendar (<c>date.fiscal_month</c>, its role instance <c>ship_date.fiscal_month</c>, or the same level read through
    /// a datetime tied to the date dimension), the calendar's date attribute (a day), or a bucket of a datetime (<c>orders.ordered_at_month</c>). The unit is the
    /// level's name in that calendar. Null for an attribute that isn't a period, which a relative operator can't be used on.
    /// </summary>
    public (CalendarDef Calendar, string Unit)? PeriodOf(string attribute)
    {
        var calendars = Doc.Calendars.Values.SelectMany(c => new[] { c }
            .Concat(Doc.Attributes.Where(kv => kv.Value.Instance is { } i && i.Base == c.Date).Select(kv => CalendarFor(kv.Key)))).Where(c => c is not null).Cast<CalendarDef>()
            .Concat(Doc.Attributes.Where(kv => kv.Value is { Type: "datetime", Instance: null, Bucket: null }).Select(kv => CalendarFor(kv.Key))!);
        foreach (var calendar in calendars)
            foreach (var (unit, level) in calendar!.Levels)
                if (level == attribute) return (calendar, unit);
        return null;
    }

    /// <summary>Every attribute that is a period: what a relative operator may be used on.</summary>
    public List<string> PeriodAttributes() =>
        Doc.Attributes.Keys.Where(a => PeriodOf(a) is { } p && !(p.Calendar.Implicit && p.Unit == "hour")).ToList();

    /// <summary>The calendar whose date dimension this is, if any.</summary>
    public CalendarDef? CalendarFor(string dateAttribute)
    {
        if (Doc.Attributes.TryGetValue(dateAttribute, out var datetime) && datetime is { Type: "datetime", Instance: null, Bucket: null })
        {
            var levels = CatalogLoader.DatetimeGranularities.ToDictionary(g => g, g => $"{dateAttribute}_{g}");
            // A datetime tied to a date dimension also reads that dimension's calendar: its fiscal levels, through the datetime's own date.
            var linked = datetime.DateKey is { } key ? CalendarFor(key) : null;
            if (linked is not null)
                foreach (var (level, attr) in linked.Levels)
                    if (!levels.ContainsKey(level) && Doc.Attributes.ContainsKey($"{dateAttribute}_{attr[(attr.IndexOf('.') + 1)..]}"))
                        levels[level] = $"{dateAttribute}_{attr[(attr.IndexOf('.') + 1)..]}";
            return new CalendarDef { Date = dateAttribute, Implicit = true, Levels = levels, Base = linked };
        }
        if (Doc.Calendars.Values.FirstOrDefault(c => c.Date == dateAttribute) is { } own) return own;
        // A role of the calendar's date dimension (ship_date.calendar_date) has the same calendar read in that role: each level is the level's own role instance.
        if (!Doc.Attributes.TryGetValue(dateAttribute, out var d) || d.Instance is not { } inst) return null;
        var baseCalendar = Doc.Calendars.Values.FirstOrDefault(c => c.Date == inst.Base);
        if (baseCalendar is null) return null;
        string InRole(string attr) => Doc.Attributes.TryGetValue(attr, out var x) && x.DefaultRole is not null && Doc.Attributes.ContainsKey($"{inst.Role}.{attr[(attr.IndexOf('.') + 1)..]}")
            && attr.Split('.')[0] == inst.Base.Split('.')[0] ? $"{inst.Role}.{attr[(attr.IndexOf('.') + 1)..]}" : attr;
        return new CalendarDef { Date = dateAttribute, Levels = baseCalendar.Levels.ToDictionary(kv => kv.Key, kv => InRole(kv.Value)) };
    }

    /// <summary>True if <paramref name="parent"/> is the level directly above <paramref name="child"/> in a declared hierarchy.</summary>
    public bool IsParentLevel(string child, string parent) =>
        Doc.Hierarchies.Values.Any(h =>
        {
            var i = h.Levels.IndexOf(child);
            return i > 0 && h.Levels[i - 1] == parent;
        });

    public static string ColName(string member) => member.Replace('.', '_');

    /// <summary>YamlDotNet yields Dictionary&lt;object, object&gt; for nested maps; convert them to JSON.</summary>
    public static System.Text.Json.Nodes.JsonNode? ToJson(object? value) => value switch
    {
        null => null,
        System.Collections.IDictionary d => new System.Text.Json.Nodes.JsonObject(
            d.Keys.Cast<object>().Select(k => KeyValuePair.Create(k.ToString()!, ToJson(d[k])))),
        string s => System.Text.Json.Nodes.JsonValue.Create(s),
        System.Collections.IEnumerable e => new System.Text.Json.Nodes.JsonArray(e.Cast<object?>().Select(ToJson).ToArray()),
        _ => System.Text.Json.Nodes.JsonValue.Create(value.ToString()),
    };

    /// <summary>
    /// How a measure combines across a removed dimension: the rule named for that dimension, else the
    /// <c>default</c> rule, else <c>sum</c>. One of sum, avg, min, max, first, last, none.
    /// </summary>
    public string RollupRule(string measure, string dimension)
    {
        var sem = Doc.Measures[measure].Semantics;
        if (sem.TryGetValue("rollup", out var r) && r is System.Collections.IDictionary map)
        {
            if (map.Contains(dimension)) return map[dimension]!.ToString()!;
            if (map.Contains("default")) return map["default"]!.ToString()!;
        }
        return "sum";
    }

    /// <summary>
    /// Whether a measure can be added up, read from its rollup rules rather than kept as a second setting: additive if every rule is
    /// sum, non_additive if the default is none (a distinct count, a ratio), semi_additive otherwise (it adds across some dimensions, and takes
    /// the last, an average or a minimum across others).
    /// </summary>
    public string Additivity(string measure)
    {
        var sem = Doc.Measures[measure].Semantics;
        if (!sem.TryGetValue("rollup", out var r) || r is not System.Collections.IDictionary map) return "additive";
        var rules = map.Values.Cast<object>().Select(v => v.ToString()).ToList();
        var fallback = map.Contains("default") ? map["default"]!.ToString() : "sum";
        if (fallback == "none") return "non_additive";
        return rules.All(x => x == "sum") && fallback == "sum" ? "additive" : "semi_additive";
    }

    /// <summary>The measure's spread rule: repeat, blank, or allocate (an object, reported as "allocate").</summary>
    public string SpreadRule(string measure) =>
        Doc.Measures[measure].Allocate is not null ? "allocate"
        : Doc.Measures[measure].Semantics.TryGetValue("spread", out var s)
            ? (s is System.Collections.IDictionary ? "allocate" : s.ToString()!)
            : "blank";

    /// <summary>
    /// Registers request-scoped measures (aliases and overrides) under their output names until disposed. A name
    /// that already exists is restored afterwards, so an override never changes the catalog.
    /// </summary>
    public IDisposable ScopedMeasures(Dictionary<string, MeasureDef> defs)
    {
        var previous = defs.Keys.ToDictionary(k => k, k => Doc.Measures.GetValueOrDefault(k));
        foreach (var (name, def) in defs) Doc.Measures[name] = def;
        return new Restore(() =>
        {
            foreach (var (name, old) in previous)
                if (old is null) Doc.Measures.Remove(name); else Doc.Measures[name] = old;
        });
    }

    private sealed class Restore(Action undo) : IDisposable { public void Dispose() => undo(); }

    public bool IsAttribute(string name) => Doc.Attributes.ContainsKey(name);
    public bool IsMeasure(string name) => Doc.Measures.ContainsKey(name);
    public bool Exists(string name) => IsAttribute(name) || IsMeasure(name);

    public string TypeOf(string name) =>
        Doc.Attributes.TryGetValue(name, out var d) ? d.Type : Doc.Measures[name].Type;

    public BindingDef? Binding(string dimension, string source) =>
        Doc.Attributes.TryGetValue(_roleMap is not null && _roleMap.TryGetValue(dimension, out var instance) ? instance : dimension, out var d)
        && d.Bindings.TryGetValue(source, out var b) ? b : null;

    private Dictionary<string, string>? _roleMap;

    /// <summary>
    /// For the measures that read a role-playing dimension in a role other than its default: each bare dimension of the request that this changes,
    /// mapped to the instance (<c>date.fiscal_month</c> to <c>ship_date.fiscal_month</c>). A role named in the request (an instance name) is untouched,
    /// so it overrides both defaults. Empty when the measure has nothing to say.
    /// </summary>
    public Dictionary<string, string> RoleMap(string measure, IEnumerable<string> requestMembers)
    {
        var map = new Dictionary<string, string>();
        if (!Doc.Measures.TryGetValue(measure, out var def) || def.Roles.Count == 0) return map;
        foreach (var attr in requestMembers.Distinct())
        {
            if (!Doc.Attributes.TryGetValue(attr, out var d) || d.Instance is not null || d.DefaultRole is null) continue;
            var owner = attr.Split('.')[0];
            if (def.Roles.TryGetValue(owner, out var role) && role != d.DefaultRole)
            {
                var instance = $"{role}.{attr[(owner.Length + 1)..]}";
                if (Doc.Attributes.TryGetValue(instance, out var i) && i.Instance is { } inst && inst.Base == attr) map[attr] = instance;
            }
        }
        return map;
    }

    /// <summary>Reads the bare dimensions in <paramref name="map"/> in the roles it names until disposed (see <see cref="RoleMap"/>).</summary>
    public IDisposable WithRoles(Dictionary<string, string> map)
    {
        var previous = _roleMap;
        _roleMap = map.Count == 0 ? null : map;
        return new Restore(() => _roleMap = previous);
    }

    /// <summary>Close catalog names, matching on member names and synonyms.</summary>
    public List<string> Suggestions(string text, string? kind = null, int n = 3)
    {
        var candidates = new List<(string Key, string Name)>();
        void Add(string name, IEnumerable<string> synonyms)
        {
            candidates.Add((name, name));
            candidates.Add((name.Split('.')[1].Replace('_', ' '), name));
            candidates.AddRange(synonyms.Select(s => (s, name)));
        }
        if (kind is null or "attribute")
            foreach (var (name, d) in Doc.Attributes) Add(name, d.Synonyms);
        if (kind is null or "measure")
            foreach (var (name, m) in Doc.Measures) Add(name, m.Synonyms);

        var key = text.Split('.')[^1].Replace('_', ' ');
        return candidates
            .Select(c => (c.Name, Score: Similarity(key, c.Key)))
            .Where(c => c.Score >= 0.5)
            .OrderByDescending(c => c.Score)
            .Select(c => c.Name).Distinct().Take(n).ToList();
    }

    /// <summary>Ratcliff/Obershelp-style ratio.</summary>
    private static double Similarity(string a, string b)
    {
        static int Matches(string a, string b)
        {
            if (a.Length == 0 || b.Length == 0) return 0;
            int bestI = 0, bestJ = 0, bestLen = 0;
            for (int i = 0; i < a.Length; i++)
                for (int j = 0; j < b.Length; j++)
                {
                    int k = 0;
                    while (i + k < a.Length && j + k < b.Length && a[i + k] == b[j + k]) k++;
                    if (k > bestLen) (bestI, bestJ, bestLen) = (i, j, k);
                }
            if (bestLen == 0) return 0;
            return bestLen + Matches(a[..bestI], b[..bestJ]) + Matches(a[(bestI + bestLen)..], b[(bestJ + bestLen)..]);
        }
        return a.Length + b.Length == 0 ? 1 : 2.0 * Matches(a, b) / (a.Length + b.Length);
    }
}
