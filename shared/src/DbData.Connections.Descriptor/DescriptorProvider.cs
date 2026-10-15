using System.Data.Common;
using DbData.Dialect;

namespace DbData.Connections.Descriptor;

/// <summary>A provider built from an <see cref="EngineDescriptor"/> over a <see cref="DbProviderFactory"/>.</summary>
public sealed class DescriptorProvider : DbProviderBase
{
    private readonly DbProviderFactory _factory;
    private readonly DialectCatalog _catalog;

    public DescriptorProvider(EngineDescriptor descriptor, DbProviderFactory factory, DialectCatalog? dialects = null)
        : base(new EngineId(descriptor.Id), CreateComposer(descriptor), CreateReadOnly(descriptor), null, dialects)
    {
        Descriptor = descriptor;
        _factory = factory;
        _catalog = dialects ?? DialectCatalog.Builtin;
    }

    public EngineDescriptor Descriptor { get; }

    protected override string TestSql => Descriptor.TestQuery ?? "SELECT 1";

    public override EngineCaps Caps => _catalog.TryGet(new EngineId(Descriptor.Dialect), out var d) ? d!.Caps : EngineCaps.None;

    /// <summary>The connection's own <c>dialect</c> override if it names a known dialect, otherwise the descriptor's.</summary>
    public override ISqlDialect Dialect(ConnectionDefinition definition) =>
        definition.Dialect is { } id && _catalog.TryGet(new EngineId(id), out var over) ? over! : _catalog.Get(new EngineId(Descriptor.Dialect));

    protected override DbConnection NewConnection(string connectionString, ConnectionContext context)
    {
        var c = _factory.CreateConnection() ?? throw new InvalidOperationException($"The factory for '{Descriptor.Id}' did not create a connection.");
        c.ConnectionString = connectionString;
        return c;
    }

    private static string? Key(string value) => string.IsNullOrWhiteSpace(value) || value.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : value;

    private static IConnectionStringComposer CreateComposer(EngineDescriptor d)
    {
        var k = d.ConnectionStringKeys;
        var keys = new GenericKeys
        {
            Host = k.Host, Port = Key(k.Port), Database = k.Database, Username = k.Username, Password = k.Password,
            ConnectTimeout = Key(k.ConnectTimeout), CommandTimeout = Key(k.CommandTimeout), IntegratedSecurity = Key(k.IntegratedSecurity),
            Encrypt = Key(k.EncryptKey) is { } ek ? (ek, k.EncryptOn, k.EncryptOff) : null,
            Verify = Key(k.TrustKey) is { } tk ? (tk, k.VerifyValue, k.TrustValue) : null,
        };
        return new GenericAdoComposer(new EngineId(d.Id), new GenericComposerOptions
        {
            Keys = keys,
            OdbcQuoting = d.OdbcQuoting,
            FixedProperties = d.FixedProperties,
            OverridableFixed = new HashSet<string>(d.OverridableProperties, StringComparer.OrdinalIgnoreCase),
            ReadOnlyKeys = d.ReadOnly.Keys,
            DefaultDatabase = d.DefaultDatabase,
        });
    }

    private static ReadOnlySession CreateReadOnly(EngineDescriptor d)
    {
        var ro = d.ReadOnly;
        if (ro.Keys.Count == 0 && ro.Statements.Count == 0)
            return ReadOnlySession.NoneAvailable(d.Id, ro.Notes ?? "This engine descriptor defines no read-only recipe; use a read-only login.");
        return new ReadOnlySession(d.Id, ro.Statements.Count > 0 ? ReadOnlyEnforcement.SessionDefault : ReadOnlyEnforcement.Routing, ro.Statements, ro.Keys, ro.Notes);
    }
}

/// <summary>Loads descriptors and registers the providers they describe.</summary>
public static class DescriptorRegistration
{
    /// <summary>Loads every <c>*.engine.yaml</c> in a directory. Returns what could not be used with its diagnostics; good files are still registered.</summary>
    public static IReadOnlyDictionary<string, DescriptorLoadResult> RegisterDirectory(
        ProviderRegistry registry, string directory, IProviderFactoryResolver resolver, DialectCatalog? dialects = null)
    {
        var results = new Dictionary<string, DescriptorLoadResult>();
        if (!Directory.Exists(directory)) return results;
        foreach (var file in Directory.EnumerateFiles(directory, "*.engine.yaml").Order(StringComparer.Ordinal))
        {
            var r = DescriptorLoader.Load(File.ReadAllText(file), file, dialects, resolver);
            results[file] = r;
            if (r.Ok) Register(registry, r.Descriptor!, resolver, dialects);
        }
        return results;
    }

    public static DescriptorProvider Register(ProviderRegistry registry, EngineDescriptor descriptor, IProviderFactoryResolver resolver, DialectCatalog? dialects = null)
    {
        var factory = resolver.Resolve(descriptor.Library) ?? throw new InvalidOperationException($"The library '{descriptor.Library}' did not resolve to a provider factory.");
        var provider = new DescriptorProvider(descriptor, factory, dialects);
        registry.Register(provider);
        return provider;
    }
}
