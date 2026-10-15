using System.Reflection;

namespace DbData.Dialect;

/// <summary>
/// A set of dialect definitions (embedded built-ins plus project-level additions) that resolves <c>extends</c> chains
/// into <see cref="ISqlDialect"/> instances. Immutable; <see cref="With"/> returns a new catalog.
/// </summary>
public sealed class DialectCatalog
{
    private static readonly Dictionary<string, Func<ResolvedDialect, DataDrivenDialect>> Factories = new()
    {
        ["sqlserver"] = s => new SqlServerDialect(s),
        ["fabric"] = s => new FabricDialect(s),
        ["mysql"] = s => new MySqlDialect(s),
        ["oracle"] = s => new OracleDialect(s),
    };

    private static readonly Lazy<DialectCatalog> BuiltinLazy = new(LoadBuiltin);
    private readonly Dictionary<string, DialectDefinition> _defs;
    private readonly Dictionary<string, DataDrivenDialect> _resolved = new();
    private readonly object _gate = new();

    private DialectCatalog(Dictionary<string, DialectDefinition> defs) => _defs = defs;

    /// <summary>ansi, sqlserver, postgres, mysql, oracle, duckdb, fabric.</summary>
    public static DialectCatalog Builtin => BuiltinLazy.Value;

    public IReadOnlyCollection<string> Ids => _defs.Keys.Order().ToList();

    /// <summary>A new catalog with one more (or replacing) dialect parsed from dialect.yaml text.</summary>
    public DialectCatalog With(string yaml, string sourceName = "dialect.yaml")
    {
        var def = DialectYamlLoader.Load(yaml, sourceName);
        var copy = new Dictionary<string, DialectDefinition>(_defs) { [def.Id] = def };
        var catalog = new DialectCatalog(copy);
        catalog.Get(def.Id); // fail fast: resolve now so a bad extends chain is reported at load
        return catalog;
    }

    public ISqlDialect Get(EngineId id) => Resolve(id.Value, []);
    public ISqlDialect Get(string id) => Get(new EngineId(id));

    public bool TryGet(EngineId id, out ISqlDialect? dialect)
    {
        dialect = null;
        if (!_defs.ContainsKey(id.Value)) return false;
        dialect = Get(id);
        return true;
    }

    /// <summary>Builds a dialect for a definition that is not registered in this catalog (used by the generic ODBC dialect).</summary>
    internal ISqlDialect Build(DialectDefinition def)
    {
        var copy = new Dictionary<string, DialectDefinition>(_defs) { [def.Id] = def };
        return new DialectCatalog(copy).Get(def.Id);
    }

    private DataDrivenDialect Resolve(string id, List<string> chain)
    {
        lock (_gate)
        {
            if (_resolved.TryGetValue(id, out var cached)) return cached;
            if (!_defs.TryGetValue(id, out var def)) throw new DialectException($"unknown dialect '{id}'" + (chain.Count > 0 ? $" (extended by '{chain[^1]}')" : ""));
            if (chain.Contains(id)) throw new DialectException($"dialect inheritance cycle: {string.Join(" -> ", chain)} -> {id}", def.Source);
            chain.Add(id);
            var (resolved, factory) = ResolveSpec(def, chain);
            var dialect = (factory ?? (s => new DataDrivenDialect(s)))(resolved);
            chain.RemoveAt(chain.Count - 1);
            _resolved[id] = dialect;
            return dialect;
        }
    }

    private (ResolvedDialect, Func<ResolvedDialect, DataDrivenDialect>?) ResolveSpec(DialectDefinition def, List<string> chain)
    {
        ResolvedDialect? parent = null;
        Func<ResolvedDialect, DataDrivenDialect>? factory = null;
        if (def.Extends is { } ext)
        {
            if (!_defs.TryGetValue(ext, out var pdef)) throw new DialectException($"dialect '{def.Id}' extends unknown dialect '{ext}'", def.Source);
            if (chain.Contains(ext)) throw new DialectException($"dialect inheritance cycle: {string.Join(" -> ", chain)} -> {ext}", def.Source);
            chain.Add(ext);
            (parent, factory) = ResolveSpec(pdef, chain);
            chain.RemoveAt(chain.Count - 1);
        }
        var r = def.Resolve(parent);
        if (Factories.TryGetValue(def.Id, out var own)) factory = own;
        return (r, factory);
    }

    private static DialectCatalog LoadBuiltin()
    {
        var asm = typeof(DialectCatalog).Assembly;
        var defs = new Dictionary<string, DialectDefinition>();
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("DbData.Dialect.dialects.", StringComparison.Ordinal) && n.EndsWith(".yaml", StringComparison.Ordinal)).Order())
        {
            using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var def = DialectYamlLoader.Load(reader.ReadToEnd(), name["DbData.Dialect.dialects.".Length..]);
            defs[def.Id] = def;
        }
        var catalog = new DialectCatalog(defs);
        foreach (var id in defs.Keys) catalog.Get(id);
        return catalog;
    }

    /// <summary>Convenience: the built-in dialect for an engine id.</summary>
    public static ISqlDialect For(EngineId id) => Builtin.Get(id);
}
