using System.Data.Common;
using DbDataKit.DataModel.Storage;
using DbDataKit.Schema.Tests;

namespace DbDataKit.DataModel.Tests;

/// <summary>Storage for a new model (plan 19 Z03): the statements per engine, and the tables made for real on SQL Server and PostgreSQL (<c>testenv/testenv.sh up mssql postgres</c>).</summary>
public sealed class ModelStorageTests
{
    private const string Fixture = """
        schemaVersion: 1
        name: sales_territories
        title: Sales territories
        storage: {connection: mdm_sql}
        entities:
          territory:
            key: [territory_code]
            attributes:
              territory_code: {type: VARCHAR(10), required: true}
              name:           {type: VARCHAR(100), required: true, unique: true}
              region:         {type: VARCHAR(40), references: {model: crm_reference, entity: region}}
              active:         {type: BOOLEAN, default: true}
              budget:         {type: "DECIMAL(12,2)"}
          territory_share:
            key: [territory_code, region]
            attributes:
              territory_code: {type: VARCHAR(10), references: {entity: territory}}
              region:         {type: VARCHAR(40)}
              share:          {type: "DECIMAL(5,4)", min: 0, max: 1}
        """;

    private static DataModel Model(string text = Fixture)
    {
        var loaded = DataModelLoader.Load(text, "sales_territories.yaml");
        Assert.True(loaded.Ok, string.Join("; ", loaded.Diagnostics.Select(d => d.Message)));
        return loaded.Model!;
    }

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("postgres")]
    public void A_model_is_a_schema_a_table_per_entity_their_keys_and_the_foreign_keys_its_references_imply(string engine)
    {
        var plan = ModelStorage.Plan(Model(), engine);

        Assert.Equal(["sales_territories.territory", "sales_territories.territory_share", "sales_territories._audit"], plan.Tables);
        Assert.Equal(
            ["create schema sales_territories", "create table sales_territories.territory", "create table sales_territories.territory_share", "create table sales_territories._audit",
             "primary key of sales_territories.territory", "unique name of sales_territories.territory", "primary key of sales_territories.territory_share",
             "primary key of sales_territories._audit", "index of sales_territories._audit", "foreign key territory_code of sales_territories.territory_share"],
            plan.Statements.Select(s => s.Description));
        var territory = plan.Statements[1].Text;
        var key = territory.Split('\n').Single(l => l.Contains("territory_code"));
        Assert.EndsWith("NOT NULL", key.TrimEnd(',', ' '));                          // required, and a key
        var budget = territory.Split('\n').Single(l => l.Contains("budget"));
        Assert.EndsWith(" NULL", budget.TrimEnd(',', ' '));                          // neither required nor key
        Assert.DoesNotContain("NOT NULL", budget);
        Assert.Contains("FOREIGN KEY", plan.Statements[^1].Text);
        Assert.Contains("_version", territory);                                      // the version a write names
        Assert.Contains(plan.Notes, n => n.Contains("crm_reference.region") && n.Contains("no foreign key"));
    }

    [Fact]
    public void An_engine_it_does_not_store_on_and_a_reference_between_two_types_are_refused_in_words()
    {
        Assert.Contains("`mysql` is not one of them", Assert.Throws<StorageException>(() => ModelStorage.Plan(Model(), "mysql")).Message);
        var mismatched = Model(Fixture.Replace("territory_code: {type: VARCHAR(10), references: {entity: territory}}", "territory_code: {type: VARCHAR(12), references: {entity: territory}}"));
        Assert.Contains("a reference needs the same type at both ends", Assert.Throws<StorageException>(() => ModelStorage.Plan(mismatched, "postgres")).Message);
    }

    [Theory, MemberData(nameof(Engines.Both), MemberType = typeof(Engines))]
    public async Task The_fixture_model_creates_its_tables_on_the_engine_and_the_keys_hold(string engine)
    {
        await using var engines = await Engines.OpenAsync(engine);
        var plan = ModelStorage.Plan(Model(), engine);

        await ModelStorage.CreateAsync(engines.Connection, plan);

        Assert.Equal(plan.Tables, await ModelStorage.ExistingAsync(engines.Connection, plan));
        var s = engine == Engines.SqlServer ? "[sales_territories]" : "sales_territories";
        await engines.ExecuteAsync($"INSERT INTO {s}.territory (territory_code, name, _version) VALUES ('EMEA-01', 'Europe', 1)");
        await engines.ExecuteAsync($"INSERT INTO {s}.territory_share (territory_code, region, share, _version) VALUES ('EMEA-01', 'DE', 0.5, 1)");
        await Assert.ThrowsAnyAsync<DbException>(() => engines.ExecuteAsync($"INSERT INTO {s}.territory (territory_code, name, _version) VALUES ('EMEA-01', 'Again', 1)"));        // the key
        await Assert.ThrowsAnyAsync<DbException>(() => engines.ExecuteAsync($"INSERT INTO {s}.territory (territory_code, name, _version) VALUES ('EMEA-02', 'Europe', 1)"));       // unique
        await Assert.ThrowsAnyAsync<DbException>(() => engines.ExecuteAsync($"INSERT INTO {s}.territory (territory_code, _version) VALUES ('EMEA-03', 1)"));                        // required
        await Assert.ThrowsAnyAsync<DbException>(() => engines.ExecuteAsync($"INSERT INTO {s}.territory_share (territory_code, region, _version) VALUES ('NOWHERE', 'DE', 1)"));  // the reference
    }

    [Theory, MemberData(nameof(Engines.Both), MemberType = typeof(Engines))]
    public async Task Creating_again_is_refused_before_anything_is_written_and_a_failing_statement_leaves_nothing(string engine)
    {
        await using var engines = await Engines.OpenAsync(engine);
        var plan = ModelStorage.Plan(Model(), engine);
        await ModelStorage.CreateAsync(engines.Connection, plan);

        var again = await Assert.ThrowsAsync<StorageException>(() => ModelStorage.CreateAsync(engines.Connection, plan));
        Assert.Contains("storage here only creates", again.Message);

        var broken = new StoragePlan(engine, "other_model", [new("other_model", "create schema", plan.Statements[0].Text.Replace("sales_territories", "other_model")), new("x", "fails", "THIS IS NOT SQL")], ["other_model.thing"], []);
        await Assert.ThrowsAnyAsync<DbException>(() => ModelStorage.CreateAsync(engines.Connection, broken));
        await using var command = engines.Connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM information_schema.schemata WHERE schema_name = 'other_model'";
        Assert.Equal(0, Convert.ToInt32(await command.ExecuteScalarAsync()));
    }
}
