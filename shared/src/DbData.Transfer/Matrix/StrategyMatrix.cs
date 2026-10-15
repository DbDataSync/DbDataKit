using System.Reflection;
using System.Text.Json;
using DbData.Dialect;

namespace DbData.Transfer;

public enum MatrixStatus { Reference, ProductVerified, Unverified, Unsupported }

public sealed record MatrixEntry(MatrixStatus Status, EngineVersion? MinVersion, IReadOnlyList<string> Requires, string? Note);

/// <summary>The outcome of asking whether a strategy may run on an engine. <see cref="Allowed"/> false carries the reason; allowed but unverified carries a warning.</summary>
public sealed record MatrixDecision(bool Allowed, MatrixStatus Status, string? Reason, IReadOnlyList<string> Requires);

/// <summary>
/// The strategy by engine capability table, loaded from data (<c>strategy-matrix.json</c>), never from code. Invalid combinations are
/// rejected at validation time, before any statement is built.
/// </summary>
public sealed class StrategyMatrix
{
    private readonly Dictionary<string, Dictionary<string, MatrixEntry>> _table;
    private readonly Dictionary<string, string?> _aliases;

    private StrategyMatrix(Dictionary<string, Dictionary<string, MatrixEntry>> table, Dictionary<string, string?> aliases)
    {
        _table = table; _aliases = aliases;
    }

    private static readonly Lazy<StrategyMatrix> BuiltinLazy = new(() =>
    {
        using var s = typeof(StrategyMatrix).Assembly.GetManifestResourceStream("DbData.Transfer.strategy-matrix.json")
            ?? throw new InvalidOperationException("strategy-matrix.json is not embedded");
        using var r = new StreamReader(s);
        return Parse(r.ReadToEnd());
    });

    public static StrategyMatrix Builtin => BuiltinLazy.Value;

    public IReadOnlyCollection<string> Strategies => _table.Keys;

    public static StrategyMatrix Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var table = new Dictionary<string, Dictionary<string, MatrixEntry>>(StringComparer.Ordinal);
        foreach (var strat in doc.RootElement.GetProperty("strategies").EnumerateObject())
        {
            var engines = new Dictionary<string, MatrixEntry>(StringComparer.Ordinal);
            foreach (var e in strat.Value.GetProperty("engines").EnumerateObject())
            {
                var status = e.Value.GetProperty("status").GetString() switch
                {
                    "reference" => MatrixStatus.Reference,
                    "product-verified" => MatrixStatus.ProductVerified,
                    "unverified" => MatrixStatus.Unverified,
                    "unsupported" => MatrixStatus.Unsupported,
                    var other => throw new FormatException($"unknown status '{other}' for {strat.Name}/{e.Name}"),
                };
                EngineVersion? min = null;
                if (e.Value.TryGetProperty("min_version", out var mv) && !EngineVersion.TryParse(mv.GetString(), out min))
                    throw new FormatException($"bad min_version for {strat.Name}/{e.Name}");
                var requires = e.Value.TryGetProperty("requires", out var rq) ? rq.EnumerateArray().Select(x => x.GetString()!).ToList() : [];
                var note = e.Value.TryGetProperty("note", out var n) ? n.GetString() : null;
                engines[e.Name] = new MatrixEntry(status, min, requires, note);
            }
            table[strat.Name] = engines;
        }
        var aliases = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (doc.RootElement.TryGetProperty("aliases", out var al))
            foreach (var a in al.EnumerateObject()) aliases[a.Name] = a.Value.ValueKind == JsonValueKind.Null ? null : a.Value.GetString();
        return new StrategyMatrix(table, aliases);
    }

    /// <summary>Maps a product's kind name (Sync <c>MsSqlMerge</c>) to the shared name. Null when the kind has no shared strategy (Sync <c>Snapshot</c>, <c>Scd2</c>).</summary>
    public string? Resolve(string name)
    {
        if (_table.ContainsKey(name)) return name;
        return _aliases.TryGetValue(name, out var target) ? target : null;
    }

    public MatrixEntry? Entry(string strategy, EngineId engine) =>
        _table.TryGetValue(strategy, out var engines) && engines.TryGetValue(engine.Value, out var e) ? e : null;

    /// <summary>Checks one combination. Engines not in the table (generic ODBC) are unsupported until someone adds a row.</summary>
    public MatrixDecision Check(string strategy, EngineId engine, EngineVersion? version = null, bool requireVerified = false)
    {
        var resolved = Resolve(strategy);
        if (resolved is null) return new(false, MatrixStatus.Unsupported, $"`{strategy}` is not a shared strategy", []);
        var e = Entry(resolved, engine);
        if (e is null) return new(false, MatrixStatus.Unsupported, $"no row for engine `{engine}`", []);
        if (e.Status == MatrixStatus.Unsupported) return new(false, e.Status, e.Note ?? "unsupported", e.Requires);
        if (e.MinVersion is { } min && version is not null && version.CompareTo(min) < 0)
            return new(false, e.Status, $"needs {engine} {min} or later (found {version})", e.Requires);
        if (requireVerified && e.Status == MatrixStatus.Unverified)
            return new(false, e.Status, $"`{resolved}` on `{engine}` is not verified by any conformance run", e.Requires);
        return new(true, e.Status, e.Status == MatrixStatus.Unverified ? "unverified: no conformance run has exercised this combination" : null, e.Requires);
    }

    public void Require(string strategy, EngineId engine, EngineVersion? version = null, bool requireVerified = false)
    {
        var d = Check(strategy, engine, version, requireVerified);
        if (!d.Allowed) throw new StrategyNotSupportedException(Resolve(strategy) ?? strategy, engine.Value, d.Reason ?? "not allowed");
    }
}
