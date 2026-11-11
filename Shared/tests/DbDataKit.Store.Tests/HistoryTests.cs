using DbDataKit.DataModel;
using DbDataKit.DataModel.Storage;
using DbDataKit.Schema.Tests;

namespace DbDataKit.Store.Tests;

/// <summary>Effective-dated history (plan 19 Z05), app-managed, on SQL Server and PostgreSQL: versions of a row with as-of reads, a correction that adds none, an end that keeps the past, and the no_overlap rule.</summary>
public sealed class HistoryTests
{
    private const string Fixture = """
        schemaVersion: 1
        name: price_book
        storage: {connection: mdm_sql}
        entities:
          product:
            key: [sku]
            history: effective_dated
            attributes:
              sku:   {type: VARCHAR(20), required: true}
              name:  {type: VARCHAR(80), required: true, unique: true}
              price: {type: "DECIMAL(10,2)", min: 0}
          assignment:
            key: [assignment_id]
            history: effective_dated
            attributes:
              assignment_id: {type: INTEGER, required: true}
              rep:           {type: VARCHAR(40), required: true}
              sku:           {type: VARCHAR(20), references: {entity: product}}
            rules:
              - {kind: no_overlap, group_by: [sku]}
        """;

    private static readonly WriteActor Ada = new("u-ada", "Ada Lovelace");
    private static readonly DateTimeOffset Jan = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset Month(int m) => Jan.AddMonths(m - 1);

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static Dictionary<string, object?> Row(params (string, object?)[] values) => values.ToDictionary(v => v.Item1, v => v.Item2);

    private static async Task<(Engines Engines, ModelWriter Writer)> OpenAsync(string engine)
    {
        var engines = await Engines.OpenAsync(engine);
        var loaded = DataModelLoader.Load(Fixture, "model.yaml");
        Assert.True(loaded.Ok, string.Join("; ", loaded.Diagnostics.Select(d => d.Message)));
        await ModelStorage.CreateAsync(engines.Connection, ModelStorage.Plan(loaded.Model!, engine));
        return (engines, new ModelWriter(loaded.Model!, engines.Connection, engine, new Clock(Month(12))));
    }

    [Theory, MemberData(nameof(Engines.Both), MemberType = typeof(Engines))]
    public async Task A_change_from_a_date_closes_the_open_version_and_starts_the_next_and_the_past_is_read_as_of_a_date(string engine)
    {
        var (engines, writer) = await OpenAsync(engine);
        await using var _ = engines;
        var key = Row(("sku", "A-1"));
        await writer.ApplyAsync([RowChange.Insert("product", Row(("sku", "A-1"), ("name", "Anvil"), ("price", 10m)), Month(1))], Ada);
        await writer.ApplyAsync([RowChange.Update("product", key, 1, Row(("price", 12m)), Month(4))], Ada);
        await writer.ApplyAsync([RowChange.Update("product", key, 2, Row(("price", 15m)), Month(8))], Ada);

        var now = await writer.GetAsync("product", key);
        Assert.Equal((15m, 3L, Month(8), null), (now!.Values["price"], now.Version, now.ValidFrom, now.ValidTo));
        Assert.Equal(10m, (await writer.GetAsOfAsync("product", key, Month(2)))!.Values["price"]);
        Assert.Equal(12m, (await writer.GetAsOfAsync("product", key, Month(4)))!.Values["price"]);          // the new version holds from its first moment
        Assert.Equal(12m, (await writer.GetAsOfAsync("product", key, Month(7)))!.Values["price"]);
        Assert.Null(await writer.GetAsOfAsync("product", key, Jan.AddDays(-1)));
        var history = await writer.HistoryAsync("product", key);
        Assert.Equal([10m, 12m, 15m], history.Select(h => h.Values["price"]));
        Assert.Equal([Month(4), Month(8), (DateTimeOffset?)null], history.Select(h => h.ValidTo));
        Assert.Equal([Month(1), Month(4), Month(8)], history.Select(h => h.ValidFrom));
    }

    [Theory, MemberData(nameof(Engines.Both), MemberType = typeof(Engines))]
    public async Task A_correction_with_no_date_changes_the_open_version_in_place_and_adds_none(string engine)
    {
        var (engines, writer) = await OpenAsync(engine);
        await using var _ = engines;
        var key = Row(("sku", "A-1"));
        await writer.ApplyAsync([RowChange.Insert("product", Row(("sku", "A-1"), ("name", "Anvil"), ("price", 10m)), Month(1))], Ada);

        await writer.UpdateAsync("product", key, 1, Row(("price", 11m)), Ada);

        var history = await writer.HistoryAsync("product", key);
        var only = Assert.Single(history);
        Assert.Equal((11m, 2L), (only.Values["price"], only.Version));
    }

    [Theory, MemberData(nameof(Engines.Both), MemberType = typeof(Engines))]
    public async Task A_delete_ends_the_row_without_removing_the_past_and_the_key_can_begin_again_after_it(string engine)
    {
        var (engines, writer) = await OpenAsync(engine);
        await using var _ = engines;
        var key = Row(("sku", "A-1"));
        await writer.ApplyAsync([RowChange.Insert("product", Row(("sku", "A-1"), ("name", "Anvil"), ("price", 10m)), Month(1))], Ada);

        await writer.ApplyAsync([RowChange.Delete("product", key, 1, Month(6))], Ada);

        Assert.Null(await writer.GetAsync("product", key));
        Assert.Equal(10m, (await writer.GetAsOfAsync("product", key, Month(3)))!.Values["price"]);
        Assert.Null(await writer.GetAsOfAsync("product", key, Month(7)));
        var early = await Assert.ThrowsAsync<WriteRefusedException>(() => writer.ApplyAsync([RowChange.Insert("product", Row(("sku", "A-1"), ("name", "Anvil 2"), ("price", 20m)), Month(5))], Ada));
        Assert.Equal(["effective"], early.Violations.Select(v => v.Rule));
        await writer.ApplyAsync([RowChange.Insert("product", Row(("sku", "A-1"), ("name", "Anvil"), ("price", 20m)), Month(9))], Ada);     // the name of the ended version is free again
        Assert.Equal(2, (await writer.HistoryAsync("product", key)).Count);
    }

    [Theory, MemberData(nameof(Engines.Both), MemberType = typeof(Engines))]
    public async Task A_change_cannot_start_before_the_version_it_changes_and_a_stale_version_conflicts(string engine)
    {
        var (engines, writer) = await OpenAsync(engine);
        await using var _ = engines;
        var key = Row(("sku", "A-1"));
        await writer.ApplyAsync([RowChange.Insert("product", Row(("sku", "A-1"), ("name", "Anvil"), ("price", 10m)), Month(3))], Ada);

        var early = await Assert.ThrowsAsync<WriteRefusedException>(() => writer.ApplyAsync([RowChange.Update("product", key, 1, Row(("price", 11m)), Month(2))], Ada));
        Assert.Equal(["effective"], early.Violations.Select(v => v.Rule));
        await writer.ApplyAsync([RowChange.Update("product", key, 1, Row(("price", 11m)), Month(5))], Ada);
        await Assert.ThrowsAsync<VersionConflictException>(() => writer.ApplyAsync([RowChange.Update("product", key, 1, Row(("price", 12m)), Month(6))], Ada));
        Assert.Equal(2, (await writer.HistoryAsync("product", key)).Count);
    }

    [Theory, MemberData(nameof(Engines.Both), MemberType = typeof(Engines))]
    public async Task A_unique_value_and_a_reference_are_judged_against_the_open_versions(string engine)
    {
        var (engines, writer) = await OpenAsync(engine);
        await using var _ = engines;
        await writer.ApplyAsync([RowChange.Insert("product", Row(("sku", "A-1"), ("name", "Anvil"), ("price", 10m)), Month(1))], Ada);

        var duplicate = await Assert.ThrowsAsync<WriteRefusedException>(() => writer.ApplyAsync([RowChange.Insert("product", Row(("sku", "A-2"), ("name", "Anvil"), ("price", 1m)), Month(2))], Ada));
        Assert.Equal(["unique"], duplicate.Violations.Select(v => v.Rule));

        await writer.ApplyAsync([RowChange.Delete("product", Row(("sku", "A-1")), 1, Month(3))], Ada);
        var ended = await Assert.ThrowsAsync<WriteRefusedException>(() => writer.ApplyAsync([RowChange.Insert("assignment", Row(("assignment_id", 1), ("rep", "Grace"), ("sku", "A-1")), Month(4))], Ada));
        Assert.Equal(["reference"], ended.Violations.Select(v => v.Rule));
        await writer.ApplyAsync([RowChange.Insert("product", Row(("sku", "A-2"), ("name", "Anvil"), ("price", 1m)), Month(4))], Ada);      // the name of the ended one is free
    }

    [Theory, MemberData(nameof(Engines.Both), MemberType = typeof(Engines))]
    public async Task No_overlap_refuses_two_rows_of_a_group_that_hold_at_once_and_allows_one_after_the_other(string engine)
    {
        var (engines, writer) = await OpenAsync(engine);
        await using var _ = engines;
        await writer.ApplyAsync([RowChange.Insert("product", Row(("sku", "A-1"), ("name", "Anvil"), ("price", 10m)), Month(1))], Ada);
        await writer.ApplyAsync([RowChange.Insert("assignment", Row(("assignment_id", 1), ("rep", "Grace"), ("sku", "A-1")), Month(2))], Ada);

        var overlapping = await Assert.ThrowsAsync<WriteRefusedException>(() => writer.ApplyAsync([RowChange.Insert("assignment", Row(("assignment_id", 2), ("rep", "Linus"), ("sku", "A-1")), Month(3))], Ada));
        var broken = Assert.Single(overlapping.Violations);
        Assert.Equal("no_overlap", broken.Rule);
        Assert.Contains("sku = A-1", broken.Message);

        // Grace's assignment ends in month 6; Linus's begins in month 6: the periods touch and do not overlap, in one batch
        await writer.ApplyAsync(
        [
            RowChange.Delete("assignment", Row(("assignment_id", 1)), 1, Month(6)),
            RowChange.Insert("assignment", Row(("assignment_id", 2), ("rep", "Linus"), ("sku", "A-1")), Month(6)),
        ], Ada);
        Assert.Null(await writer.GetAsync("assignment", Row(("assignment_id", 1))));
        Assert.Equal("Linus", (await writer.GetAsOfAsync("assignment", Row(("assignment_id", 2)), Month(8)))!.Values["rep"]);
        Assert.Equal("Grace", (await writer.GetAsOfAsync("assignment", Row(("assignment_id", 1)), Month(4)))!.Values["rep"]);
    }

    [Fact]
    public async Task An_entity_without_history_has_no_past_to_read()
    {
        var loaded = DataModelLoader.Load("schemaVersion: 1\nname: m\nstorage: {connection: c}\nentities:\n  thing:\n    key: [id]\n    attributes:\n      id: {type: INTEGER, required: true}\n", "m.yaml");
        var writer = new ModelWriter(loaded.Model!, null!, "postgres");

        await Assert.ThrowsAsync<StoreNotSupportedException>(() => writer.HistoryAsync("thing", Row(("id", 1))));
    }
}
