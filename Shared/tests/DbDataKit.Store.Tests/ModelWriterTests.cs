using DbDataKit.DataModel;
using DbDataKit.DataModel.Storage;
using DbDataKit.Schema.Tests;

namespace DbDataKit.Store.Tests;

/// <summary>The write path (plan 19 Z04) on SQL Server and PostgreSQL (<c>testenv/testenv.sh up mssql postgres</c>): rules refuse with the rule's name, versions guard updates, every write is audited.</summary>
public sealed class ModelWriterTests
{
    private const string Fixture = """
        schemaVersion: 1
        name: sales_territories
        storage: {connection: mdm_sql}
        entities:
          territory:
            key: [territory_code]
            attributes:
              territory_code: {type: VARCHAR(10), required: true, pattern: "^[A-Z]{2,4}-[0-9]{2}$"}
              name:           {type: VARCHAR(100), required: true, unique: true}
              tier:           {type: VARCHAR(10), allowed: [gold, silver]}
              active:         {type: BOOLEAN, default: true}
              quota:          {type: "DECIMAL(10,2)", min: 0, max: 1000000}
              parent_code:    {type: VARCHAR(10), references: {entity: territory}}
            rules:
              - {kind: no_cycle, attribute: parent_code}
          territory_share:
            key: [territory_code, region]
            attributes:
              territory_code: {type: VARCHAR(10), references: {entity: territory}}
              region:         {type: VARCHAR(40)}
              share:          {type: "DECIMAL(5,4)", min: 0, max: 1}
            rules:
              - {kind: sum_equals, attribute: share, group_by: [region], value: 1, tolerance: 0.0001}
        """;

    private static readonly WriteActor Ada = new("u-ada", "Ada Lovelace");

    private static Dictionary<string, object?> Row(params (string, object?)[] values) => values.ToDictionary(v => v.Item1, v => v.Item2);

    private static DbDataKit.DataModel.DataModel Model(string text = Fixture)
    {
        var loaded = DataModelLoader.Load(text, "model.yaml");
        Assert.True(loaded.Ok, string.Join("; ", loaded.Diagnostics.Select(d => d.Message)));
        return loaded.Model!;
    }

    private static async Task<(Engines Engines, ModelWriter Writer)> OpenAsync(string engine, string text = Fixture)
    {
        var engines = await Engines.OpenAsync(engine);
        var model = Model(text);
        await ModelStorage.CreateAsync(engines.Connection, ModelStorage.Plan(model, engine));
        return (engines, new ModelWriter(model, engines.Connection, engine));
    }

    private static async Task<List<string[]>> AuditAsync(Engines engines)
    {
        await using var command = engines.Connection.CreateCommand();
        command.CommandText = "SELECT entity, action, actor_id, actor_name, version_before, version_after, before_json, after_json FROM sales_territories._audit ORDER BY at, action";
        var rows = new List<string[]>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "" : Convert.ToString(reader.GetValue(i))!).ToArray());
        return rows;
    }

    private static async Task<long> CountAsync(Engines engines, string table)
    {
        await using var command = engines.Connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM sales_territories.{table}";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    [Theory, MemberData(nameof(Engines.Both), MemberType = typeof(Engines))]
    public async Task An_insert_applies_defaults_starts_at_version_one_and_is_audited_with_who_made_it(string engine)
    {
        var (engines, writer) = await OpenAsync(engine);
        await using var _ = engines;

        await writer.InsertAsync("territory", Row(("territory_code", "EMEA-01"), ("name", "Europe"), ("quota", 1500.5m)), Ada);

        var stored = await writer.GetAsync("territory", Row(("territory_code", "EMEA-01")));
        Assert.NotNull(stored);
        Assert.Equal(1, stored.Version);
        Assert.Equal(true, stored.Values["active"]);                       // the default
        Assert.Equal(1500.5m, stored.Values["quota"]);
        var audit = Assert.Single(await AuditAsync(engines));
        Assert.Equal(["territory", "insert", "u-ada", "Ada Lovelace"], audit[..4]);
        Assert.Equal(["", "1"], audit[4..6]);
        Assert.Contains("\"name\":\"Europe\"", audit[7]);
    }

    [Theory, MemberData(nameof(Engines.Both), MemberType = typeof(Engines))]
    public async Task A_page_of_rows_comes_in_key_order_and_a_page_size_out_of_range_is_refused(string engine)
    {
        var (engines, writer) = await OpenAsync(engine);
        await using var _ = engines;
        foreach (var code in new[] { "CC-03", "AA-01", "BB-02" })
            await writer.InsertAsync("territory", Row(("territory_code", code), ("name", "n" + code)), Ada);

        var page = await writer.ListAsync("territory", limit: 2, offset: 1);

        Assert.Equal(["BB-02", "CC-03"], page.Select(r => (string)r.Values["territory_code"]!));
        Assert.Empty(await writer.ListAsync("territory", offset: 3));
        await Assert.ThrowsAsync<StoreNotSupportedException>(() => writer.ListAsync("territory", limit: 0));
    }

    [Theory, MemberData(nameof(Engines.Both), MemberType = typeof(Engines))]
    public async Task Every_rule_a_row_breaks_is_named_in_one_refusal_and_nothing_is_written(string engine)
    {
        var (engines, writer) = await OpenAsync(engine);
        await using var _ = engines;

        var refused = await Assert.ThrowsAsync<WriteRefusedException>(() => writer.InsertAsync("territory",
            Row(("territory_code", "bad code"), ("tier", "bronze"), ("quota", -5m)), Ada));      // pattern, required name, allowed, min

        Assert.Equal(["allowed", "min", "pattern", "required"], refused.Violations.Select(v => v.Rule).Order().ToArray());
        Assert.Contains(refused.Violations, v => v.Rule == "pattern" && v.Attribute == "territory_code" && v.Message.Contains("bad code"));
        Assert.Equal(0, await CountAsync(engines, "territory"));
        Assert.Empty(await AuditAsync(engines));

        var typed = await Assert.ThrowsAsync<WriteRefusedException>(() => writer.InsertAsync("territory",
            Row(("territory_code", "EMEA-02"), ("name", new string('x', 101)), ("quota", 1.234m), ("active", "maybe")), Ada));
        Assert.Equal(["length", "precision", "type"], typed.Violations.Select(v => v.Rule).Order().ToArray());
    }

    [Theory, MemberData(nameof(Engines.Both), MemberType = typeof(Engines))]
    public async Task A_duplicate_key_a_duplicate_unique_value_and_a_missing_reference_are_refused_by_name(string engine)
    {
        var (engines, writer) = await OpenAsync(engine);
        await using var _ = engines;
        await writer.InsertAsync("territory", Row(("territory_code", "EMEA-01"), ("name", "Europe")), Ada);

        Assert.Equal(["key", "unique"], (await Assert.ThrowsAsync<WriteRefusedException>(() => writer.InsertAsync("territory", Row(("territory_code", "EMEA-01"), ("name", "Europe")), Ada))).Violations.Select(v => v.Rule).Order());
        Assert.Equal(["unique"], (await Assert.ThrowsAsync<WriteRefusedException>(() => writer.InsertAsync("territory", Row(("territory_code", "EMEA-02"), ("name", "Europe")), Ada))).Violations.Select(v => v.Rule));
        var missing = await Assert.ThrowsAsync<WriteRefusedException>(() => writer.InsertAsync("territory_share", Row(("territory_code", "NOWHERE-1"), ("region", "DE"), ("share", 1m)), Ada));
        Assert.Contains(missing.Violations, v => v.Rule == "reference" && v.Attribute == "territory_code");
        Assert.Equal(1, await CountAsync(engines, "territory"));
    }

    [Theory, MemberData(nameof(Engines.Both), MemberType = typeof(Engines))]
    public async Task An_update_names_the_version_it_read_and_a_stale_one_is_a_conflict_that_says_the_current_version(string engine)
    {
        var (engines, writer) = await OpenAsync(engine);
        await using var _ = engines;
        var key = Row(("territory_code", "EMEA-01"));
        await writer.InsertAsync("territory", Row(("territory_code", "EMEA-01"), ("name", "Europe")), Ada);

        await writer.UpdateAsync("territory", key, 1, Row(("name", "Europe, Middle East")), Ada);
        Assert.Equal(2, (await writer.GetAsync("territory", key))!.Version);

        var stale = await Assert.ThrowsAsync<VersionConflictException>(() => writer.UpdateAsync("territory", key, 1, Row(("name", "Again")), Ada));
        Assert.Equal((1L, 2L), (stale.Expected, stale.Current));
        await Assert.ThrowsAsync<RowNotFoundException>(() => writer.UpdateAsync("territory", Row(("territory_code", "NONE-00")), 1, Row(("name", "x")), Ada));
        Assert.Equal(["key_immutable"], (await Assert.ThrowsAsync<WriteRefusedException>(() => writer.UpdateAsync("territory", key, 2, Row(("territory_code", "EMEA-09")), Ada))).Violations.Select(v => v.Rule));

        await writer.UpdateAsync("territory", key, 2, Row(("name", "Europe, Middle East")), Ada);       // nothing changed: no new version, no audit
        Assert.Equal(2, (await writer.GetAsync("territory", key))!.Version);
        var audit = await AuditAsync(engines);
        Assert.Equal(["insert", "update"], audit.Select(a => a[1]));
        Assert.Equal(["1", "2"], audit[1][4..6]);
        Assert.Contains("\"name\":\"Europe\"", audit[1][6]);
        Assert.Contains("\"name\":\"Europe, Middle East\"", audit[1][7]);
    }

    [Theory, MemberData(nameof(Engines.Both), MemberType = typeof(Engines))]
    public async Task A_delete_needs_the_version_leaves_nothing_pointing_at_the_row_and_is_audited(string engine)
    {
        var (engines, writer) = await OpenAsync(engine);
        await using var _ = engines;
        await writer.InsertAsync("territory", Row(("territory_code", "EMEA-01"), ("name", "Europe")), Ada);
        await writer.InsertAsync("territory_share", Row(("territory_code", "EMEA-01"), ("region", "DE"), ("share", 1m)), Ada);
        var key = Row(("territory_code", "EMEA-01"));

        var pointed = await Assert.ThrowsAsync<WriteRefusedException>(() => writer.DeleteAsync("territory", key, 1, Ada));
        Assert.Equal(["referenced"], pointed.Violations.Select(v => v.Rule));

        await writer.DeleteAsync("territory_share", Row(("territory_code", "EMEA-01"), ("region", "DE")), 1, Ada);
        await Assert.ThrowsAsync<VersionConflictException>(() => writer.DeleteAsync("territory", key, 7, Ada));
        await writer.DeleteAsync("territory", key, 1, Ada);

        Assert.Null(await writer.GetAsync("territory", key));
        Assert.Equal(["delete", "delete", "insert", "insert"], (await AuditAsync(engines)).Select(a => a[1]).Order());
    }

    [Theory, MemberData(nameof(Engines.Both), MemberType = typeof(Engines))]
    public async Task A_sum_rule_is_checked_after_the_whole_batch_so_a_total_can_move_between_rows(string engine)
    {
        var (engines, writer) = await OpenAsync(engine);
        await using var _ = engines;
        await writer.ApplyAsync(
        [
            RowChange.Insert("territory", Row(("territory_code", "EMEA-01"), ("name", "Europe"))),
            RowChange.Insert("territory", Row(("territory_code", "EMEA-02"), ("name", "Africa"))),
        ], Ada);

        var alone = await Assert.ThrowsAsync<WriteRefusedException>(() => writer.InsertAsync("territory_share", Row(("territory_code", "EMEA-01"), ("region", "DE"), ("share", 0.6m)), Ada));
        var broken = Assert.Single(alone.Violations);
        Assert.Equal("sum_equals", broken.Rule);
        Assert.Contains("region = DE", broken.Message);
        Assert.Equal(0, await CountAsync(engines, "territory_share"));

        await writer.ApplyAsync(
        [
            RowChange.Insert("territory_share", Row(("territory_code", "EMEA-01"), ("region", "DE"), ("share", 0.6m))),
            RowChange.Insert("territory_share", Row(("territory_code", "EMEA-02"), ("region", "DE"), ("share", 0.4m))),
        ], Ada);
        Assert.Equal(2, await CountAsync(engines, "territory_share"));

        // move 0.1 from one to the other in one batch: the total holds; alone, each half would not
        await writer.ApplyAsync(
        [
            RowChange.Update("territory_share", Row(("territory_code", "EMEA-01"), ("region", "DE")), 1, Row(("share", 0.5m))),
            RowChange.Update("territory_share", Row(("territory_code", "EMEA-02"), ("region", "DE")), 1, Row(("share", 0.5m))),
        ], Ada);
        await Assert.ThrowsAsync<WriteRefusedException>(() => writer.DeleteAsync("territory_share", Row(("territory_code", "EMEA-02"), ("region", "DE")), 2, Ada));
    }

    [Theory, MemberData(nameof(Engines.Both), MemberType = typeof(Engines))]
    public async Task A_cycle_in_a_hierarchy_is_refused_and_one_bad_change_in_a_batch_writes_none_of_it(string engine)
    {
        var (engines, writer) = await OpenAsync(engine);
        await using var _ = engines;
        await writer.InsertAsync("territory", Row(("territory_code", "EMEA-01"), ("name", "Europe")), Ada);
        await writer.InsertAsync("territory", Row(("territory_code", "EMEA-02"), ("name", "Germany"), ("parent_code", "EMEA-01")), Ada);

        var cycle = await Assert.ThrowsAsync<WriteRefusedException>(() => writer.UpdateAsync("territory", Row(("territory_code", "EMEA-01")), 1, Row(("parent_code", "EMEA-02")), Ada));
        Assert.Equal(["no_cycle"], cycle.Violations.Select(v => v.Rule));
        Assert.Null((await writer.GetAsync("territory", Row(("territory_code", "EMEA-01"))))!.Values["parent_code"]);

        await Assert.ThrowsAsync<WriteRefusedException>(() => writer.ApplyAsync(
        [
            RowChange.Insert("territory", Row(("territory_code", "EMEA-03"), ("name", "France"))),
            RowChange.Insert("territory", Row(("territory_code", "EMEA-04"), ("name", "Spain"), ("tier", "bronze"))),
        ], Ada));
        Assert.Equal(2, await CountAsync(engines, "territory"));
    }

    [Fact]
    public void A_rule_the_write_path_cannot_enforce_yet_stops_the_writer_instead_of_being_skipped()
    {
        var model = Model(Fixture + "\n" + """
          territory_period:
            key: [territory_code, valid_from]
            attributes:
              territory_code: {type: VARCHAR(10), references: {entity: territory}}
              valid_from:     {type: DATE}
              valid_to:       {type: DATE}
            rules:
              - {kind: no_overlap, group_by: [territory_code]}
        """.Replace("\n", "\n"));

        var stopped = Assert.Throws<StoreNotSupportedException>(() => new ModelWriter(model, null!, "postgres"));
        Assert.Contains("no_overlap", stopped.Message);
    }
}
