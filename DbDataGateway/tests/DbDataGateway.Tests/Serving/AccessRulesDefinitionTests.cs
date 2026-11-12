using DbDataGateway.Server.Serving;
using DbDataGateway.Tests.Config;
using DbDataKit.State;
using System.Text.Json;

namespace DbDataGateway.Tests.Serving;

/// <summary>
/// Plan 22a GP02, on SQLite, SQL Server and PostgreSQL: a definition with an `access` section is stored with its rules (the version row carries the format and the text), the hash covers the rules, the same rules are no new version, a rollback
/// returns the old rules with the old version, and a promotion between channels moves definition and rules together. The Store's entity model carries its section the same way. A section the loaders refuse is refused by the kind's validator.
/// </summary>
public sealed class AccessRulesDefinitionTests : IDisposable
{
    private readonly List<IDisposable> _cleanups = [];
    private readonly List<string> _files = [];

    public void Dispose()
    {
        foreach (var cleanup in _cleanups) cleanup.Dispose();
        foreach (var path in _files.SelectMany(f => new[] { f, f + "-wal", f + "-shm" }).Where(File.Exists)) File.Delete(path);
    }

    private DefinitionStore Open(string engine)
    {
        StateDatabase database;
        if (engine == "sqlite")
        {
            var file = Path.Combine(Path.GetTempPath(), "dbdata-access-rules-" + Guid.NewGuid().ToString("N") + ".db");
            _files.Add(file);
            database = new StateDatabase(file, StateLayout.Create(StateEngineIds.Sqlite, [DefinitionsModule.Module], new StateScope(Group: "main")));
        }
        else
        {
            var scratch = ScratchStateDatabase.For(engine);
            _cleanups.Add(scratch);
            database = scratch.OpenState(DefinitionsModule.Module);
        }

        return ServingKinds.StoreOf(database);
    }

    private static string Catalog(string effect = "include", bool access = true)
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "semantic", "catalog.yaml")).Replace("\ntables:\n", "\ntables:\n  - name: region_access\n    x_source: sqlserver_sales\n    source: region_access\n    columns:\n      - {name: who, datatype: String, expression: {dialects: [{dialect: ANSI_SQL, expression: who}]}}\n      - {name: region_code, datatype: String, expression: {dialects: [{dialect: ANSI_SQL, expression: region_code}]}}\n", StringComparison.Ordinal).TrimEnd() + "\n";
        return access ? text + $"access:\n  row:\n    - dataset: dim_customer\n      groups:\n        - id: by_region\n          rules:\n            - {{ id: r1, effect: {effect}, table: region_access, subject: who, columns: {{ region: region_code }} }}\n            - {{ id: r2, effect: include, table: region_access, subject: who, columns: {{ region: region_code }} }}\n  columns:\n    - {{ subject: interns, dataset: dim_customer, column: customer, treatment: hide }}\n" : text;
    }

    private static string Manifest(string catalog) => JsonSerializer.Serialize(new { catalog });

    private const string Model = "schemaVersion: 2\nname: territories\nstorage: {connection: mdm_sql}\nentities:\n  territory:\n    key: [code]\n    attributes:\n      code: {type: VARCHAR(10)}\n      region: {type: VARCHAR(20)}\n";

    private static string EntityManifest(string model) => JsonSerializer.Serialize(new { model, dataSource = "mdm_sql" });

    [Theory]
    [InlineData("sqlite")]
    [InlineData("mssql")]
    [InlineData("postgres")]
    public void The_version_carries_the_rules_the_hash_covers_them_and_a_rollback_returns_the_old_ones(string engine)
    {
        var store = Open(engine);
        var plain = store.Publish("semantic-model", "sales_catalog", Manifest(Catalog(access: false)), "ada", format: 1);
        var v2 = store.Publish("semantic-model", "sales_catalog", Manifest(Catalog()), "ada", format: 2);
        Assert.Equal((1, 2), (plain.Version, v2.Version));
        Assert.Equal(2, store.Current("semantic-model", "sales_catalog")!.Format);
        Assert.Contains("by_region", store.Current("semantic-model", "sales_catalog")!.Content);
        Assert.NotEqual(plain.Hash, v2.Hash);

        // the same rules again are no new version; a changed rule is one
        Assert.Equal(2, store.Publish("semantic-model", "sales_catalog", Manifest(Catalog()), "ada", format: 2).Version);
        var v3 = store.Publish("semantic-model", "sales_catalog", Manifest(Catalog("exclude")), "ada", format: 2);
        Assert.Equal(3, v3.Version);
        Assert.NotEqual(v2.Hash, v3.Hash);

        var back = store.Rollback("semantic-model", "sales_catalog", "ada");
        Assert.Equal((2, v2.Hash), (back.Version, back.Hash));
        Assert.Contains("effect: include", store.Current("semantic-model", "sales_catalog")!.Content);
        Assert.Equal(3, store.Get("semantic-model", "sales_catalog", 3)!.Version);   // the version rolled back from stays in the history
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("mssql")]
    [InlineData("postgres")]
    public void A_promotion_between_channels_moves_the_definition_and_its_rules_together(string engine)
    {
        var store = Open(engine);
        var published = store.Publish("semantic-model", "sales_catalog", Manifest(Catalog()), "ada", format: 2, channel: "test");
        var promoted = store.Publish("semantic-model", "sales_catalog", published.Content, "ada", format: 2);
        Assert.Equal(published.Hash, promoted.Hash);
        Assert.Contains("by_region", store.Current("semantic-model", "sales_catalog")!.Content);
        Assert.Contains("interns", store.Current("semantic-model", "sales_catalog")!.Content);
        Assert.Equal(published.Content, store.Current("semantic-model", "sales_catalog", "test")!.Content);
        // each channel keeps its own rules: a change on one is not on the other
        store.Publish("semantic-model", "sales_catalog", Manifest(Catalog("exclude")), "ada", format: 2, channel: "test");
        Assert.Contains("effect: include", store.Current("semantic-model", "sales_catalog")!.Content);
        Assert.Contains("effect: exclude", store.Current("semantic-model", "sales_catalog", "test")!.Content);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("mssql")]
    [InlineData("postgres")]
    public void An_entity_model_carries_its_section_in_format_2_and_a_bad_section_is_refused_by_the_validator(string engine)
    {
        var store = Open(engine);
        var rules = "access:\n  row:\n    - dataset: territory\n      groups:\n        - id: g\n          rules:\n            - { id: r, effect: include, table: rules.region_access, subject: who, columns: { region: region_code } }\n";
        var version = store.Publish("entity-model", "territories", EntityManifest(Model + rules), "ada", format: 2);
        Assert.Equal(2, version.Format);
        Assert.Contains("rules.region_access", store.Current("entity-model", "territories")!.Content);

        var refused = Assert.Throws<PublishRefusedException>(() => store.Publish("entity-model", "bad", EntityManifest(Model + rules.Replace("table: rules.region_access", "table: territories.territory")), "ada", format: 2));
        Assert.Equal(PublishRefusal.Invalid, refused.Reason);
        Assert.Contains("Store table is never read directly", refused.Message);
        var model1 = Assert.Throws<PublishRefusedException>(() => store.Publish("entity-model", "old", EntityManifest((Model + rules).Replace("schemaVersion: 2", "schemaVersion: 1")), "ada", format: 1));
        Assert.Contains("format 1", model1.Message);
        Assert.Null(store.Current("entity-model", "bad"));
    }

    /// <summary>The done-when of plan 23a FQ05: the sections `checks` (and `cache`) of format 2 are stored and carried as the `access` section is.</summary>
    [Theory]
    [InlineData("sqlite")]
    [InlineData("mssql")]
    [InlineData("postgres")]
    public void A_definition_with_checks_publishes_its_version_carries_them_and_a_rollback_restores_the_old_checks(string engine)
    {
        var store = Open(engine);
        const string FreshDay = "checks:\n  - {id: fresh, kind: freshness, dataset: fact_sales, column: ordered_at, max_age: 1d}\n";
        const string FreshHour = "checks:\n  - {id: fresh, kind: freshness, dataset: fact_sales, column: ordered_at, max_age: 1h}\n";
        var plain = store.Publish("semantic-model", "sales_catalog", Manifest(Catalog(access: false)), "ada", format: 1);
        var v2 = store.Publish("semantic-model", "sales_catalog", Manifest(Catalog(access: false) + FreshDay), "ada", format: 2);
        var v3 = store.Publish("semantic-model", "sales_catalog", Manifest(Catalog(access: false) + FreshHour), "ada", format: 2);
        Assert.Equal((1, 2, 3), (plain.Version, v2.Version, v3.Version));
        Assert.Equal(2, store.Current("semantic-model", "sales_catalog")!.Format);
        Assert.Contains("max_age: 1h", store.Current("semantic-model", "sales_catalog")!.Content);
        Assert.Equal((2, v2.Hash), (store.Rollback("semantic-model", "sales_catalog", "ada").Version, v2.Hash));
        Assert.Contains("max_age: 1d", store.Current("semantic-model", "sales_catalog")!.Content);

        var model = store.Publish("entity-model", "territories", EntityManifest(Model + "checks:\n  - {id: fresh, kind: freshness, dataset: territory, max_age: 6h}\n"), "ada", format: 2);
        Assert.Equal(2, model.Format);
        Assert.Contains("max_age: 6h", store.Current("entity-model", "territories")!.Content);
    }
}
