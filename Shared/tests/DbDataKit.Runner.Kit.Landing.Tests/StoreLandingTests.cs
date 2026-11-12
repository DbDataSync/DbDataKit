using System.Text;
using DbDataKit.DataModel;
using DbDataKit.DataModel.Storage;
using DbDataKit.Runner.Kit.Formats;
using DbDataKit.Schema.Tests;
using DbDataKit.Store;

namespace DbDataKit.Runner.Kit.Landing.Tests;

/// <summary>Plan 27a IX11, the Store half: records land through the Store's write path on SQL Server and PostgreSQL (a database of its own per test; the connection variables of the suite's test environment).</summary>
public sealed class StoreLandingTests
{
    private const string Fixture = """
        schemaVersion: 1
        name: land_territories
        storage: {connection: mdm_sql}
        entities:
          territory:
            key: [territory_code]
            attributes:
              territory_code: {type: VARCHAR(10), required: true, pattern: "^[A-Z]{2,4}-[0-9]{2}$"}
              name:           {type: VARCHAR(100), required: true, unique: true}
              tier:           {type: VARCHAR(10), allowed: [gold, silver]}
              quota:          {type: "DECIMAL(10,2)", min: 0, max: 1000000}
        """;

    private static readonly WriteActor Integration = new("i-crm-sync", "CRM sync");

    private static Dictionary<string, object?> Rec(string code, string name, string? tier = "gold", decimal quota = 100m) =>
        new() { ["territory_code"] = code, ["name"] = name, ["tier"] = tier, ["quota"] = quota };

    private static async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> Records(IEnumerable<Dictionary<string, object?>> records)
    {
        foreach (var record in records) yield return record;
        await Task.CompletedTask;
    }

    private static async Task<(Engines Engines, ModelWriter Writer, DbDataKit.DataModel.DataModel Model)> OpenAsync(string engine)
    {
        var engines = await Engines.OpenAsync(engine);
        var loaded = DataModelLoader.Load(Fixture, "model.yaml");
        Assert.True(loaded.Ok, string.Join("; ", loaded.Diagnostics.Select(d => d.Message)));
        await ModelStorage.CreateAsync(engines.Connection, ModelStorage.Plan(loaded.Model!, engine));
        return (engines, new ModelWriter(loaded.Model!, engines.Connection, engine), loaded.Model!);
    }

    private static async Task<long> AuditRowsAsync(Engines engines)
    {
        await using var command = engines.Connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM land_territories._audit";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    [Theory, MemberData(nameof(Engines.Both), MemberType = typeof(Engines))]
    public async Task Records_land_through_the_write_path_and_landing_them_again_changes_nothing(string engine)
    {
        var (engines, writer, model) = await OpenAsync(engine);
        await using var _ = engines;
        var lander = new StoreLander(writer, model, "territory", Integration);
        var records = new[] { Rec("EU-01", "Europe"), Rec("US-01", "United States", "silver"), Rec("AP-01", "Asia Pacific") };

        var first = await lander.LandAsync(Records(records));
        Assert.Equal((3L, 0L, 0L, 3L), (first.Inserted, first.Updated, first.Unchanged, first.Through));
        Assert.True(first.Complete);
        var audit = await AuditRowsAsync(engines);
        Assert.Equal(3, audit);                                                       // history and audit, as for a person's write

        var again = await lander.LandAsync(Records(records));
        Assert.Equal((0L, 0L, 3L, 3L), (again.Inserted, again.Updated, again.Unchanged, again.Through));
        Assert.Equal(audit, await AuditRowsAsync(engines));                           // no new version, no audit row
        Assert.Equal(1L, (await writer.GetAsync("territory", new Dictionary<string, object?> { ["territory_code"] = "EU-01" }))!.Version);

        var changed = await lander.LandAsync(Records([Rec("EU-01", "Europe", quota: 250m), Rec("US-01", "United States", "silver")]));
        Assert.Equal((0L, 1L, 1L), (changed.Inserted, changed.Updated, changed.Unchanged));
        var row = (await writer.GetAsync("territory", new Dictionary<string, object?> { ["territory_code"] = "EU-01" }))!;
        Assert.Equal((2L, 250m), (row.Version, row.Values["quota"]));
    }

    [Theory, MemberData(nameof(Engines.Both), MemberType = typeof(Engines))]
    public async Task A_batch_that_breaks_a_rule_is_refused_with_the_rule_and_the_landing_stops_at_the_last_good_point(string engine)
    {
        var (engines, writer, model) = await OpenAsync(engine);
        await using var _ = engines;
        var lander = new StoreLander(writer, model, "territory", Integration, new StoreLandOptions { BatchRecords = 2 });
        var records = new[]
        {
            Rec("EU-01", "Europe"), Rec("US-01", "United States"),             // batch 1: good
            Rec("AP-01", "Asia Pacific"), Rec("xx", "Nowhere"),                 // batch 2: the second breaks the pattern
            Rec("AF-01", "Africa"), Rec("SA-01", "South America"),              // batch 3: never tried
        };

        var result = await lander.LandAsync(Records(records));

        Assert.False(result.Complete);
        Assert.Equal(2, result.Through);                                         // the last good point: after record 2
        Assert.Equal(2, result.Inserted);
        var refusal = result.Refused!;
        Assert.Equal((3L, 4L, (long?)4), (refusal.FirstRecord, refusal.LastRecord, refusal.BadRecord));
        Assert.Contains(refusal.Violations, v => v.Rule == "pattern" && v.Attribute == "territory_code");
        Assert.Null(await writer.GetAsync("territory", new Dictionary<string, object?> { ["territory_code"] = "AP-01" }));         // all of the refused batch or none
        Assert.Null(await writer.GetAsync("territory", new Dictionary<string, object?> { ["territory_code"] = "AF-01" }));         // nothing after it was tried
        Assert.NotNull(await writer.GetAsync("territory", new Dictionary<string, object?> { ["territory_code"] = "US-01" }));

        // fixed, the same records again: the good point is found by the Store, not remembered
        records[3] = Rec("NW-01", "Nowhere");
        var fixedRun = await lander.LandAsync(Records(records));
        Assert.True(fixedRun.Complete);
        Assert.Equal((4L, 0L, 2L, 6L), (fixedRun.Inserted, fixedRun.Updated, fixedRun.Unchanged, fixedRun.Through));
    }

    [Theory, MemberData(nameof(Engines.Both), MemberType = typeof(Engines))]
    public async Task The_rows_of_a_file_land_and_a_value_of_the_wrong_type_is_a_rule_of_the_Store(string engine)
    {
        var (engines, writer, model) = await OpenAsync(engine);
        await using var _ = engines;
        var lander = new StoreLander(writer, model, "territory", Integration);
        await using var good = new MemoryStream(Encoding.UTF8.GetBytes("territory_code,name,tier,ignored\nEU-01,Europe,gold,x\n"));
        var landed = await lander.LandAsync(DelimitedReader.ReadAsync(good));
        Assert.Equal(1, landed.Inserted);

        await using var bad = new MemoryStream(Encoding.UTF8.GetBytes("territory_code,name,tier\nUS-01,United States,platinum\n"));
        var refused = await lander.LandAsync(DelimitedReader.ReadAsync(bad));
        Assert.Contains(refused.Refused!.Violations, v => v.Rule == "allowed" && v.Attribute == "tier");
    }
}
