using DbDataKit.Connections.DuckDb;
using DbDataKit.Connections.MySql;
using DbDataKit.Connections.Odbc;
using DbDataKit.Connections.Oracle;
using DbDataKit.Connections.Postgres;
using DbDataKit.Connections.SqlServer;
using DbDataKit.Dialect;

namespace DbDataKit.Connections.Descriptor.Tests;

/// <summary>The registry (plan 14 R5, plan 19 J06) is one list of engines: what it says about dialects, providers and catalog readers is what the code has.</summary>
public class EngineRegistryTests
{
    private static readonly EngineRegistry Registry = EngineRegistry.Builtin;

    private static IEnumerable<IDbProvider> Providers() =>
        [new SqlServerProvider(), new PostgresProvider(), new MySqlProvider(), new OracleProvider(), new DuckDbProvider(), new OdbcProvider()];

    [Fact]
    public void EverySharedDialectHasADescriptorAndEveryDescriptorsDialectExists()
    {
        foreach (var dialect in DialectCatalog.Builtin.Ids)
            Assert.NotNull(Registry.Find(dialect));
        foreach (var engine in Registry.Engines)
            Assert.True(DialectCatalog.Builtin.TryGet(new EngineId(engine.Dialect), out _), $"{engine.Id}: dialect {engine.Dialect}");
    }

    [Fact]
    public void AnIdAnAliasAndAnOdbcNameFindTheirEngine()
    {
        Assert.Equal("sqlserver", Registry.Find("mssql")!.Id);
        Assert.Equal("postgres", Registry.Find("PG")!.Id);
        Assert.Equal("odbc", Registry.Find("odbc:legacy_dsn")!.Id);
        Assert.Null(Registry.Find("nosuchengine"));
    }

    [Fact]
    public void EveryDescriptorsProviderIsTheOneThatServesItAndEveryProviderHasADescriptor()
    {
        var providers = Providers().ToList();
        foreach (var provider in providers)
        {
            var engine = Registry.Find(provider.Engine.Value);
            Assert.NotNull(engine);
            Assert.Equal(provider.Engine.Value, engine!.Provider);
            // the dialect a provider writes for the engine is the dialect the descriptor names
            Assert.Equal(engine.Dialect, provider.Dialect(new ConnectionDefinition { Name = "x", Engine = engine.Id }).Id.Value);
        }
        foreach (var engine in Registry.Engines.Where(e => e.Provider is not null))
            Assert.Contains(providers, p => p.Engine.Value == engine.Provider);
    }

    [Fact]
    public void TheProviderRegistrysAliasesAreTheDescriptorsAliases()
    {
        var providers = new ProviderRegistry(Providers());
        foreach (var engine in Registry.Engines.Where(e => e.Provider is not null))
            foreach (var alias in engine.Aliases)
            {
                Assert.True(providers.TryGet(alias, out var provider), alias);
                Assert.Equal(engine.Provider, provider!.Engine.Value);
            }
        foreach (var alias in new[] { "mssql", "sql-server", "postgresql", "pgsql", "pg", "duck" })
            Assert.Contains(Registry.Engines, e => e.Aliases.Contains(alias));
    }

    [Fact]
    public void EveryCatalogReaderNamedExistsInMetadata()
    {
        var types = typeof(DbDataKit.Metadata.InformationSchemaCatalogReader).Assembly.GetTypes().Select(t => t.Name).ToHashSet();
        var names = new Dictionary<string, string>
        {
            ["sqlserver"] = "SqlServerCatalogReader", ["postgres"] = "PostgresCatalogReader", ["mysql"] = "MySqlCatalogReader", ["oracle"] = "OracleCatalogReader",
            ["duckdb"] = "DuckDbCatalogReader", ["odbc"] = "OdbcCatalogReader", ["information_schema"] = "InformationSchemaCatalogReader",
        };
        foreach (var engine in Registry.Engines.Where(e => e.CatalogReader is not null))
        {
            Assert.True(names.TryGetValue(engine.CatalogReader!, out var type), $"{engine.Id}: reader {engine.CatalogReader} is not one the registry knows");
            Assert.Contains(type, types);
        }
    }

    [Fact]
    public void TheRolesTheDescriptorsClaimAreTheOnesTheProductsDocument()
    {
        // what the products' READMEs and plan 14 say, as a table: the engines the registry lists in each role
        Assert.Equal(["duckdb", "flightsql", "mysql", "oracle", "postgres", "sqlserver"], Registry.Supporting(EngineRoles.SyncSource).Select(e => e.Id).Order());
        Assert.Equal(["mysql", "oracle", "postgres", "sqlserver"], Registry.Supporting(EngineRoles.SyncTarget).Select(e => e.Id).Order());
        Assert.Equal(["fabric", "postgres", "sqlserver"], Registry.Supporting(EngineRoles.BuildTarget).Select(e => e.Id).Order());
        Assert.Equal(["duckdb", "odbc", "sqlserver"], Registry.Supporting(EngineRoles.ProxySource).Select(e => e.Id).Order());
        Assert.Equal(["duckdb", "sqlserver"], Registry.Supporting(EngineRoles.SemanticEmitter).Select(e => e.Id).Order());
        Assert.Equal(["fabric"], Registry.Supporting(EngineRoles.BuildTarget, EngineSupport.Unverified).Where(e => e.Role(EngineRoles.BuildTarget).Status == EngineSupport.Unverified).Select(e => e.Id));
    }

    // ---- the files are checked as strictly as a connection file ----

    private const string Minimal = "schema: engine/1\nid: {0}\ndisplay_name: X\nlibrary: none\ndialect: {1}\n";

    private static EngineRegistryException Rejects(params (string File, string Yaml)[] docs) =>
        Assert.Throws<EngineRegistryException>(() => EngineRegistry.Load(docs));

    [Fact]
    public void ADialectWithoutADescriptorIsAnError()
    {
        var e = Rejects(("ansi.engine.yaml", string.Format(Minimal, "ansi", "ansi")));
        Assert.Contains(e.Diagnostics, d => d.Code == "DBD-ENG-010" && d.Message.Contains("`sqlserver`"));
    }

    [Fact]
    public void AnUnknownRoleNamesTheFileAndTheLine()
    {
        var yaml = string.Format(Minimal, "x", "ansi") + "roles:\n  teleport: { status: supported }\n";
        var e = Assert.Throws<EngineRegistryException>(() => EngineRegistry.Load([("x.engine.yaml", yaml)]));
        var d = Assert.Single(e.Diagnostics, x => x.Code == DescriptorLoader.UnknownRole);
        Assert.Equal("x.engine.yaml", d.Path);
        Assert.Equal(7, d.Line);
    }

    [Fact]
    public void APlannedOrUnverifiedRoleNeedsItsNote()
    {
        var yaml = string.Format(Minimal, "x", "ansi") + "roles:\n  sync_source: { status: planned }\n";
        var e = Assert.Throws<EngineRegistryException>(() => EngineRegistry.Load([("x.engine.yaml", yaml)]));
        Assert.Contains(e.Diagnostics, d => d.Code == DescriptorLoader.RoleNeedsNote);
    }

    [Fact]
    public void AnAliasIsOneEnginesOnly()
    {
        var all = DialectCatalog.Builtin.Ids.Select(id => ($"{id}.engine.yaml", string.Format(Minimal, id, id) + (id == "postgres" ? "aliases: [pg]\n" : id == "mysql" ? "aliases: [pg]\n" : ""))).ToArray();
        var e = Rejects(all);
        Assert.Contains(e.Diagnostics, d => d.Code == "DBD-ENG-009" && d.Message.Contains("`pg`"));
    }

    [Fact]
    public void ADescriptorWithoutARolesBlockIsStillAnEngine()
    {
        var all = DialectCatalog.Builtin.Ids.Select(id => ($"{id}.engine.yaml", string.Format(Minimal, id, id))).ToArray();
        var registry = EngineRegistry.Load(all);
        Assert.All(registry.Engines, e => Assert.Equal(EngineSupport.No, e.Role(EngineRoles.SyncSource).Status));
    }
}
