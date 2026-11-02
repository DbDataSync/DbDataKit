using DbDataKit.DataModel;
using DbDataKit.DataModel.Storage;
using DbDataKit.Schema.Tests;

namespace DbDataKit.Store.Tests;

/// <summary>
/// A model whose instances share its tables and are split by an instance id column (plan 26 section 6.2, Z12b), on SQL Server and PostgreSQL: the writer stamps the id on every row and adds it to every statement, so two instances may hold the
/// same business key, a unique value, a reference, a sum or a period without ever seeing each other.
/// </summary>
public sealed class SharedInstancesTests
{
    private const string Shared = """
        schemaVersion: 1
        name: shared
        storage: {connection: mdm, instances: column}
        entities:
          territory:
            key: [code]
            attributes:
              code: {type: VARCHAR(10), required: true}
              name: {type: VARCHAR(100), required: true, unique: true}
              parent: {type: VARCHAR(10), references: {entity: territory}}
              quota: {type: "DECIMAL(10,2)", min: 0}
            rules:
              - {kind: no_cycle, attribute: parent}
          share:
            key: [id]
            attributes:
              id: {type: INT, required: true}
              region: {type: VARCHAR(10)}
              pct: {type: "DECIMAL(5,4)"}
            rules:
              - {kind: sum_equals, attribute: pct, group_by: [region], value: 1}
          booking:
            key: [id]
            attributes:
              id: {type: INT, required: true}
              room: {type: VARCHAR(10), required: true}
            history: effective_dated
            rules:
              - {kind: no_overlap, group_by: [room]}
        """;

    private static readonly WriteActor Ada = new("u-ada", "Ada Lovelace");

    private static Dictionary<string, object?> Row(params (string, object?)[] values) => values.ToDictionary(v => v.Item1, v => v.Item2);

    private static DbDataKit.DataModel.DataModel Model()
    {
        var loaded = DataModelLoader.Load(Shared, "shared.yaml");
        Assert.True(loaded.Ok, string.Join("; ", loaded.Diagnostics.Select(d => d.Message)));
        return loaded.Model!;
    }

    private static async Task<(Engines Engines, ModelWriter Acme, ModelWriter Globex)> OpenAsync(string engine)
    {
        var engines = await Engines.OpenAsync(engine);
        var model = Model();
        await ModelStorage.CreateAsync(engines.Connection, ModelStorage.Plan(model, engine));
        return (engines, new ModelWriter(model, engines.Connection, engine, instance: "acme"), new ModelWriter(model, engines.Connection, engine, instance: "globex"));
    }

    [Theory, MemberData(nameof(Engines.Both), MemberType = typeof(Engines))]
    public async Task Two_instances_hold_the_same_key_and_the_same_unique_value_and_never_see_each_other(string engine)
    {
        var (engines, acme, globex) = await OpenAsync(engine);
        await using var _ = engines;

        await acme.InsertAsync("territory", Row(("code", "NA"), ("name", "North"), ("quota", 10m)), Ada);
        await globex.InsertAsync("territory", Row(("code", "NA"), ("name", "North"), ("quota", 99m)), Ada);   // same key and same name, another instance
        await globex.InsertAsync("territory", Row(("code", "EU"), ("name", "Europe")), Ada);

        Assert.Equal(10m, (await acme.GetAsync("territory", Row(("code", "NA"))))!.Values["quota"]);
        Assert.Equal(99m, (await globex.GetAsync("territory", Row(("code", "NA"))))!.Values["quota"]);
        Assert.Null(await acme.GetAsync("territory", Row(("code", "EU"))));
        Assert.Equal(["NA"], (await acme.ListAsync("territory")).Select(r => (string)r.Values["code"]!));
        Assert.Equal(["EU", "NA"], (await globex.ListAsync("territory")).Select(r => (string)r.Values["code"]!));

        // the rules still hold inside one instance
        var duplicate = await Assert.ThrowsAsync<WriteRefusedException>(() => acme.InsertAsync("territory", Row(("code", "XX"), ("name", "North")), Ada));
        Assert.Contains(duplicate.Violations, v => v.Rule == "unique");
        var sameKey = await Assert.ThrowsAsync<WriteRefusedException>(() => acme.InsertAsync("territory", Row(("code", "NA"), ("name", "Another")), Ada));
        Assert.Contains(sameKey.Violations, v => v.Rule == "key");
    }

    [Theory, MemberData(nameof(Engines.Both), MemberType = typeof(Engines))]
    public async Task An_update_and_a_delete_touch_only_their_own_instances_row_and_versions_are_separate(string engine)
    {
        var (engines, acme, globex) = await OpenAsync(engine);
        await using var _ = engines;
        await acme.InsertAsync("territory", Row(("code", "NA"), ("name", "A North")), Ada);
        await globex.InsertAsync("territory", Row(("code", "NA"), ("name", "G North")), Ada);

        await acme.UpdateAsync("territory", Row(("code", "NA")), 1, Row(("name", "A North 2")), Ada);

        Assert.Equal(2, (await acme.GetAsync("territory", Row(("code", "NA"))))!.Version);
        var other = (await globex.GetAsync("territory", Row(("code", "NA"))))!;
        Assert.Equal((1, "G North"), (other.Version, (string)other.Values["name"]!));

        await globex.DeleteAsync("territory", Row(("code", "NA")), 1, Ada);
        Assert.Null(await globex.GetAsync("territory", Row(("code", "NA"))));
        Assert.NotNull(await acme.GetAsync("territory", Row(("code", "NA"))));
    }

    [Theory, MemberData(nameof(Engines.Both), MemberType = typeof(Engines))]
    public async Task A_reference_and_a_cycle_are_checked_inside_the_instance(string engine)
    {
        var (engines, acme, globex) = await OpenAsync(engine);
        await using var _ = engines;
        await acme.InsertAsync("territory", Row(("code", "EU"), ("name", "Europe")), Ada);

        // globex has no EU, so pointing at it is refused, though another instance has it
        var missing = await Assert.ThrowsAsync<WriteRefusedException>(() => globex.InsertAsync("territory", Row(("code", "FR"), ("name", "France"), ("parent", "EU")), Ada));
        Assert.Contains(missing.Violations, v => v.Rule == "reference");
        await acme.InsertAsync("territory", Row(("code", "FR"), ("name", "France"), ("parent", "EU")), Ada);

        // a deleted parent is refused while a child of the same instance points at it, not because of another instance's
        var referenced = await Assert.ThrowsAsync<WriteRefusedException>(() => acme.DeleteAsync("territory", Row(("code", "EU")), 1, Ada));
        Assert.Contains(referenced.Violations, v => v.Rule == "referenced");
        await globex.InsertAsync("territory", Row(("code", "EU"), ("name", "Globex Europe")), Ada);
        await globex.DeleteAsync("territory", Row(("code", "EU")), 1, Ada);   // acme's child of EU does not stop this

        // a cycle is found in the instance's own tree
        await acme.InsertAsync("territory", Row(("code", "A"), ("name", "A")), Ada);
        await acme.InsertAsync("territory", Row(("code", "B"), ("name", "B"), ("parent", "A")), Ada);
        var cycle = await Assert.ThrowsAsync<WriteRefusedException>(() => acme.UpdateAsync("territory", Row(("code", "A")), 1, Row(("parent", "B")), Ada));
        Assert.Contains(cycle.Violations, v => v.Rule == "no_cycle");
    }

    [Theory, MemberData(nameof(Engines.Both), MemberType = typeof(Engines))]
    public async Task A_sum_and_an_overlap_rule_look_at_one_instance_only(string engine)
    {
        var (engines, acme, globex) = await OpenAsync(engine);
        await using var _ = engines;

        // acme's shares of region r add up to 1; globex's own single share of 1 must not be added to them
        await acme.ApplyAsync([RowChange.Insert("share", Row(("id", 1), ("region", "r"), ("pct", 0.6m))), RowChange.Insert("share", Row(("id", 2), ("region", "r"), ("pct", 0.4m)))], Ada);
        await globex.InsertAsync("share", Row(("id", 1), ("region", "r"), ("pct", 1m)), Ada);
        var broken = await Assert.ThrowsAsync<WriteRefusedException>(() => globex.InsertAsync("share", Row(("id", 2), ("region", "r"), ("pct", 0.1m)), Ada));
        Assert.Contains(broken.Violations, v => v.Rule == "sum_equals");

        // the same room at the same time in two instances is not an overlap; in one instance it is
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await acme.InsertAsync("booking", Row(("id", 1), ("room", "R1")), Ada);
        await globex.InsertAsync("booking", Row(("id", 2), ("room", "R1")), Ada);
        var overlap = await Assert.ThrowsAsync<WriteRefusedException>(() => acme.InsertAsync("booking", Row(("id", 3), ("room", "R1")), Ada));
        Assert.Contains(overlap.Violations, v => v.Rule == "no_overlap");
        Assert.True(start < DateTimeOffset.UtcNow);
    }

    [Theory, MemberData(nameof(Engines.Both), MemberType = typeof(Engines))]
    public async Task The_instance_is_stamped_on_every_row_and_audit_record_and_a_body_cannot_name_it(string engine)
    {
        var (engines, acme, globex) = await OpenAsync(engine);
        await using var _ = engines;
        await acme.InsertAsync("territory", Row(("code", "NA"), ("name", "A")), Ada);
        await globex.InsertAsync("territory", Row(("code", "NA"), ("name", "G")), Ada);

        await using var command = engines.Connection.CreateCommand();
        command.CommandText = "SELECT _instance, name FROM shared.territory ORDER BY _instance";
        var rows = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync()) while (await reader.ReadAsync()) rows.Add($"{reader.GetString(0)}:{reader.GetString(1)}");
        Assert.Equal(["acme:A", "globex:G"], rows);

        await using var audit = engines.Connection.CreateCommand();
        audit.CommandText = "SELECT _instance, action FROM shared._audit ORDER BY _instance";
        var records = new List<string>();
        await using (var reader = await audit.ExecuteReaderAsync()) while (await reader.ReadAsync()) records.Add($"{reader.GetString(0)}:{reader.GetString(1)}");
        Assert.Equal(["acme:insert", "globex:insert"], records);

        var named = await Assert.ThrowsAsync<WriteRefusedException>(() => acme.InsertAsync("territory", Row(("code", "ZZ"), ("name", "Z"), ("_instance", "globex")), Ada));
        Assert.Contains(named.Violations, v => v.Rule == "attribute");
    }

    [Fact]
    public void An_instance_id_that_could_be_more_than_a_name_is_refused_before_any_statement()
    {
        var model = Model();
        foreach (var bad in new[] { "acme'; DROP TABLE x;--", "Acme", "", "-x", new string('a', 65), "a b" })
            Assert.Throws<StoreNotSupportedException>(() => new ModelWriter(model, null!, "postgres", instance: bad));
    }

    [Fact]
    public void The_plan_puts_the_instance_first_in_every_key_and_leaves_foreign_keys_to_the_write_rules()
    {
        var plan = ModelStorage.Plan(Model(), "postgres");
        var text = string.Join("\n", plan.Statements.Select(s => s.Text));

        Assert.Contains("\"_instance\" varchar(64) NOT NULL", text);
        Assert.Contains("PRIMARY KEY (\"_instance\", \"code\")", text);
        Assert.DoesNotContain("FOREIGN KEY", text);
        Assert.Contains(plan.Notes, n => n.Contains("territory.parent") && n.Contains("within the instance"));
    }
}
