using System.Diagnostics.CodeAnalysis;
using DbData.Dialect;

namespace DbData.Connections;

/// <summary>Engine id to provider. Ids and aliases are case-insensitive; <c>odbc:name</c> resolves to <c>odbc</c>.</summary>
public sealed class ProviderRegistry
{
    private readonly Dictionary<string, IDbProvider> _providers = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> BuiltinAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mssql"] = "sqlserver", ["sql-server"] = "sqlserver", ["postgresql"] = "postgres", ["pgsql"] = "postgres", ["pg"] = "postgres", ["duck"] = "duckdb",
    };

    public ProviderRegistry() { }

    public ProviderRegistry(IEnumerable<IDbProvider> providers)
    {
        foreach (var p in providers) Register(p);
    }

    public IReadOnlyCollection<string> Engines => _providers.Keys.Order().ToList();

    public ProviderRegistry Register(IDbProvider provider, params string[] aliases)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _providers[provider.Engine.Value] = provider;
        foreach (var a in aliases) _providers[a.Trim()] = provider;
        return this;
    }

    public bool TryGet(string engine, [NotNullWhen(true)] out IDbProvider? provider)
    {
        provider = null;
        if (string.IsNullOrWhiteSpace(engine)) return false;
        engine = engine.Trim();
        if (_providers.TryGetValue(engine, out provider)) return true;
        if (BuiltinAliases.TryGetValue(engine, out var canonical) && _providers.TryGetValue(canonical, out provider)) return true;
        int colon = engine.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 && _providers.TryGetValue(engine[..colon], out provider);
    }

    public bool TryGet(EngineId engine, [NotNullWhen(true)] out IDbProvider? provider) => TryGet(engine.Value, out provider);

    /// <summary>The provider, or a <see cref="NotSupportedException"/> that lists what is registered (engine ids are not secrets).</summary>
    public IDbProvider Get(string engine) =>
        TryGet(engine, out var p) ? p : throw new NotSupportedException($"No provider is registered for engine '{engine}'. Registered: {string.Join(", ", Engines)}.");

    public IDbProvider For(ConnectionDefinition definition) => Get(definition.Engine);
}
