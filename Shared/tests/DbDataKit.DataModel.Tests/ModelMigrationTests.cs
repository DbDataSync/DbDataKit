using DbDataKit.DataModel.Storage;
using DbDataKit.Schema.Tests;

namespace DbDataKit.DataModel.Tests;

/// <summary>Bringing an instance's tables up to a changed model by adding only (plan 26, Z13), on SQL Server and PostgreSQL (<c>testenv/testenv.sh up mssql postgres</c>).</summary>
public sealed class ModelMigrationTests
{
    private const string V1 = """
        schemaVersion: 1
        name: sales
        storage: {connection: mdm, instances: schema}
        entities:
          territory:
            key: [code]
            attributes:
              code: {type: VARCHAR(10), required: true}
              name: {type: VARCHAR(100), required: true}
              note: {type: VARCHAR(100)}
        """;

    // v2 adds a required column and a new entity with a reference, and drops `note` from the model
    private const string V2 = """
        schemaVersion: 1
        name: sales
        storage: {connection: mdm, instances: schema}
        entities:
          territory:
            key: [code]
            attributes:
              code: {type: VARCHAR(10), required: true}
              name: {type: VARCHAR(100), required: true}
              region: {type: VARCHAR(40), required: true}
          office:
            key: [id]
            attributes:
              id: {type: INT, required: true}
              territory: {type: VARCHAR(10), references: {entity: territory}}
        """;

    private static DataModel Model(string text)
    {
        var loaded = DataModelLoader.Load(text, "sales.yaml");
        Assert.True(loaded.Ok, string.Join("; ", loaded.Diagnostics.Select(d => d.Message)));
        return loaded.Model!;
    }

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("postgres")]
    public void The_definition_hash_changes_with_the_structure_and_not_with_the_instance(string engine)
    {
        var v1 = Model(V1);

        Assert.Equal(ModelStorage.DefinitionHash(v1, engine), ModelStorage.DefinitionHash(v1, engine));
        Assert.NotEqual(ModelStorage.DefinitionHash(v1, engine), ModelStorage.DefinitionHash(Model(V2), engine));
        Assert.NotEqual(ModelStorage.DefinitionHash(v1, "sqlserver"), ModelStorage.DefinitionHash(v1, "postgres"));
        Assert.Equal(64, ModelStorage.DefinitionHash(v1, engine).Length);
    }

    [Theory, MemberData(nameof(Engines.Both), MemberType = typeof(Engines))]
    public async Task A_new_column_and_a_new_table_are_added_and_nothing_is_removed(string engine)
    {
        await using var engines = await Engines.OpenAsync(engine);
        var v1 = Model(V1);
        var v2 = Model(V2);
        await ModelStorage.CreateAsync(engines.Connection, ModelStorage.Plan(v1, engine, "sales__acme"));
        await Run(engines, engine == "sqlserver" ? "INSERT INTO [sales__acme].[territory] (code, name, note, _version) VALUES ('NA', 'North', 'keep me', 1)" : "INSERT INTO sales__acme.territory (code, name, note, _version) VALUES ('NA', 'North', 'keep me', 1)");

        var plan = ModelStorage.PlanExpand(v2, engine, "sales__acme", await ModelStorage.ReadLiveAsync(engines.Connection, "sales__acme"));

        Assert.Empty(plan.Blocked);
        Assert.Contains(plan.Statements, s => s.Description == "add column territory.region");
        Assert.Contains(plan.Statements, s => s.Description == "create table sales__acme.office");
        Assert.Contains(plan.Notes, n => n.Contains("territory.region") && n.Contains("added as nullable"));
        Assert.Contains(plan.Notes, n => n.Contains("territory.note") && n.Contains("left in place"));
        Assert.DoesNotContain(plan.Statements, s => s.Text.Contains("DROP", StringComparison.OrdinalIgnoreCase));

        await ModelStorage.ApplyExpandAsync(engines.Connection, plan);

        var live = await ModelStorage.ReadLiveAsync(engines.Connection, "sales__acme");
        Assert.Contains(live, c => c is { Table: "territory", Column: "region", Nullable: true });
        Assert.Contains(live, c => c is { Table: "territory", Column: "note" });   // kept, with its data
        Assert.Contains(live, c => c.Table == "office");
        Assert.True(ModelStorage.PlanExpand(v2, engine, "sales__acme", live).UpToDate);
        await using var count = engines.Connection.CreateCommand();
        count.CommandText = engine == "sqlserver" ? "SELECT COUNT(*) FROM [sales__acme].[territory] WHERE note = 'keep me'" : "SELECT COUNT(*) FROM sales__acme.territory WHERE note = 'keep me'";
        Assert.Equal(1L, Convert.ToInt64(await count.ExecuteScalarAsync()));
    }

    [Theory, MemberData(nameof(Engines.Both), MemberType = typeof(Engines))]
    public async Task Another_instance_still_at_the_old_version_keeps_working_and_is_reported_behind(string engine)
    {
        await using var engines = await Engines.OpenAsync(engine);
        var v1 = Model(V1);
        var v2 = Model(V2);
        await ModelStorage.CreateAsync(engines.Connection, ModelStorage.Plan(v1, engine, "sales__acme"));
        await ModelStorage.CreateAsync(engines.Connection, ModelStorage.Plan(v1, engine, "sales__globex"));

        await ModelStorage.ApplyExpandAsync(engines.Connection, ModelStorage.PlanExpand(v2, engine, "sales__acme", await ModelStorage.ReadLiveAsync(engines.Connection, "sales__acme")));

        var acme = ModelStorage.PlanExpand(v2, engine, "sales__acme", await ModelStorage.ReadLiveAsync(engines.Connection, "sales__acme"));
        var globex = ModelStorage.PlanExpand(v2, engine, "sales__globex", await ModelStorage.ReadLiveAsync(engines.Connection, "sales__globex"));
        Assert.True(acme.UpToDate);
        Assert.False(globex.UpToDate);
        // globex's tables are untouched by acme's migration, and v1 still describes them exactly
        Assert.True(ModelStorage.PlanExpand(v1, engine, "sales__globex", await ModelStorage.ReadLiveAsync(engines.Connection, "sales__globex")).UpToDate);
    }

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("postgres")]
    public void A_schema_with_no_tables_is_blocked_and_a_change_of_how_rows_are_kept_is_left_to_a_planned_change(string engine)
    {
        Assert.Contains("create its storage first", Assert.Single(ModelStorage.PlanExpand(Model(V1), engine, "sales__none", []).Blocked));

        // the live table has no _version, _valid_from or _instance: a model that now keeps history or shares its tables cannot be reached by adding
        var live = new[] { new LiveColumn("territory", "code", false), new LiveColumn("territory", "name", false), new LiveColumn("territory", "_version", false), new LiveColumn("_audit", "audit_id", false) };
        var plan = ModelStorage.PlanExpand(Model(V1.Replace("instances: schema", "instances: column")), engine, "sales", live);

        Assert.Contains(plan.Blocked, b => b.Contains("territory") && b.Contains("_instance") && b.Contains("planned change"));
    }

    private static async Task Run(Engines engines, string sql)
    {
        await using var command = engines.Connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
