using DbDataKit.DataModel;
using DbDataKit.DataModel.Storage;
using DbDataKit.Schema.Tests;

namespace DbDataKit.Store.Tests;

/// <summary>The Store's draft layer (plan 21 CH01) on SQL Server and PostgreSQL: a draft write is invisible in production and in another draft, reads show it on top of the real data, every rule applies to it, discard removes it, and nothing of it reaches history.</summary>
public sealed class DraftTests
{
    private const string Fixture = """
        schemaVersion: 1
        name: price_book
        storage: {connection: mdm_sql}
        entities:
          product:
            key: [sku]
            attributes:
              sku:   {type: VARCHAR(20), required: true}
              name:  {type: VARCHAR(80), required: true, unique: true}
              price: {type: "DECIMAL(10,2)", min: 0}
          part:
            key: [part_id]
            history: effective_dated
            attributes:
              part_id: {type: INTEGER, required: true}
              sku:     {type: VARCHAR(20), references: {entity: product}}
              note:    {type: VARCHAR(40)}
        """;

    private static readonly WriteActor Ada = new("u-ada", "Ada Lovelace");

    private static Dictionary<string, object?> Row(params (string, object?)[] values) => values.ToDictionary(v => v.Item1, v => v.Item2);

    private static async Task<(Engines Engines, DbDataKit.DataModel.DataModel Model)> OpenAsync(string engine)
    {
        var engines = await Engines.OpenAsync(engine);
        var loaded = DataModelLoader.Load(Fixture, "model.yaml");
        Assert.True(loaded.Ok, string.Join("; ", loaded.Diagnostics.Select(d => d.Message)));
        await ModelStorage.CreateAsync(engines.Connection, ModelStorage.Plan(loaded.Model!, engine));
        return (engines, loaded.Model!);
    }

    private static ModelWriter Writer(Engines engines, DbDataKit.DataModel.DataModel model, string? draft = null) => new(model, engines.Connection, engines.Engine, draft: draft);

    private static async Task<long> CountAsync(Engines engines, string table)
    {
        await using var command = engines.Connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM price_book.{table}";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    [Theory, MemberData(nameof(Engines.Both), MemberType = typeof(Engines))]
    public async Task A_draft_write_is_seen_in_its_own_draft_and_in_neither_production_nor_another_draft(string engine)
    {
        var (engines, model) = await OpenAsync(engine);
        await using var _ = engines;
        var production = Writer(engines, model);
        await production.InsertAsync("product", Row(("sku", "A-1"), ("name", "Anvil"), ("price", 10m)), Ada);
        var preview = Writer(engines, model, "acme~preview");
        var other = Writer(engines, model, "other~preview");

        await preview.InsertAsync("product", Row(("sku", "B-2"), ("name", "Bolt"), ("price", 2m)), Ada);
        await preview.UpdateAsync("product", Row(("sku", "A-1")), 1, Row(("price", 11m)), Ada);

        // the preview reads the real row with the draft on top, and its own new row
        Assert.Equal(11m, (await preview.GetAsync("product", Row(("sku", "A-1"))))!.Values["price"]);
        Assert.Equal(2L, (await preview.GetAsync("product", Row(("sku", "A-1"))))!.Version);
        Assert.Equal("Bolt", (await preview.GetAsync("product", Row(("sku", "B-2"))))!.Values["name"]);
        Assert.Equal(["A-1", "B-2"], (await preview.ListAsync("product")).Select(r => r.Values["sku"]));

        // production and another preview see neither
        Assert.Equal(10m, (await production.GetAsync("product", Row(("sku", "A-1"))))!.Values["price"]);
        Assert.Null(await production.GetAsync("product", Row(("sku", "B-2"))));
        Assert.Equal(["A-1"], (await production.ListAsync("product")).Select(r => r.Values["sku"]));
        Assert.Equal(10m, (await other.GetAsync("product", Row(("sku", "A-1"))))!.Values["price"]);
        Assert.Equal(["A-1"], (await other.ListAsync("product")).Select(r => r.Values["sku"]));

        // the tables and the audit are untouched: a draft is never history
        Assert.Equal(1L, await CountAsync(engines, "product"));
        Assert.Equal(1L, await CountAsync(engines, "_audit"));
        Assert.Equal(2L, await CountAsync(engines, "_draft"));
    }

    [Theory, MemberData(nameof(Engines.Both), MemberType = typeof(Engines))]
    public async Task A_draft_write_is_checked_by_every_rule_on_top_of_the_draft_and_nothing_is_kept_when_it_breaks_one(string engine)
    {
        var (engines, model) = await OpenAsync(engine);
        await using var _ = engines;
        await Writer(engines, model).InsertAsync("product", Row(("sku", "A-1"), ("name", "Anvil"), ("price", 10m)), Ada);
        var preview = Writer(engines, model, "p1");

        var unique = await Assert.ThrowsAsync<WriteRefusedException>(() => preview.InsertAsync("product", Row(("sku", "B-2"), ("name", "Anvil")), Ada));      // a name the real data has
        Assert.Contains(unique.Violations, v => v.Rule == "unique");
        await preview.InsertAsync("product", Row(("sku", "B-2"), ("name", "Bolt")), Ada);
        var again = await Assert.ThrowsAsync<WriteRefusedException>(() => preview.InsertAsync("product", Row(("sku", "C-3"), ("name", "Bolt")), Ada));            // a name only the draft has
        Assert.Contains(again.Violations, v => v.Rule == "unique");
        var reference = await Assert.ThrowsAsync<WriteRefusedException>(() => preview.InsertAsync("part", Row(("part_id", 1), ("sku", "NOPE")), Ada));
        Assert.Contains(reference.Violations, v => v.Rule == "reference");
        await preview.InsertAsync("part", Row(("part_id", 1), ("sku", "B-2"), ("note", "on a draft product")), Ada);                                            // a reference to a row only the draft has
        await Assert.ThrowsAsync<VersionConflictException>(() => preview.UpdateAsync("product", Row(("sku", "A-1")), 7, Row(("price", 1m)), Ada));

        Assert.Equal(2, (await preview.DraftEntriesAsync()).Count);
        await preview.ValidateAsync([RowChange.Insert("product", Row(("sku", "D-4"), ("name", "Drill")))], Ada);
        Assert.Equal(2, (await preview.DraftEntriesAsync()).Count);       // a validation keeps nothing
    }

    [Theory, MemberData(nameof(Engines.Both), MemberType = typeof(Engines))]
    public async Task Discarding_a_draft_removes_it_and_leaves_the_real_data_and_other_drafts(string engine)
    {
        var (engines, model) = await OpenAsync(engine);
        await using var _ = engines;
        await Writer(engines, model).InsertAsync("product", Row(("sku", "A-1"), ("name", "Anvil")), Ada);
        var one = Writer(engines, model, "one");
        var two = Writer(engines, model, "two");
        await one.InsertAsync("product", Row(("sku", "B-2"), ("name", "Bolt")), Ada);
        await one.InsertAsync("product", Row(("sku", "C-3"), ("name", "Clamp")), Ada);
        await two.InsertAsync("product", Row(("sku", "D-4"), ("name", "Drill")), Ada);

        var drafts = await Writer(engines, model).DraftsAsync();
        Assert.Equal([("one", 2), ("two", 1)], drafts.Select(d => (d.Id, d.Writes)));

        Assert.Equal(2, await one.DiscardDraftAsync());
        Assert.Equal(["A-1"], (await one.ListAsync("product")).Select(r => r.Values["sku"]));
        Assert.Equal(["A-1", "D-4"], (await two.ListAsync("product")).Select(r => r.Values["sku"]));
        Assert.Equal(0, await one.DiscardDraftAsync());
        Assert.Equal([("two", 1)], (await Writer(engines, model).DraftsAsync()).Select(d => (d.Id, d.Writes)));
    }

    [Theory, MemberData(nameof(Engines.Both), MemberType = typeof(Engines))]
    public async Task A_draft_entry_the_real_data_moved_away_from_is_marked_and_left_out_and_the_ones_after_it_still_show(string engine)
    {
        var (engines, model) = await OpenAsync(engine);
        await using var _ = engines;
        var production = Writer(engines, model);
        await production.InsertAsync("product", Row(("sku", "A-1"), ("name", "Anvil"), ("price", 10m)), Ada);
        var preview = Writer(engines, model, "p1");
        await preview.UpdateAsync("product", Row(("sku", "A-1")), 1, Row(("price", 11m)), Ada);
        await preview.InsertAsync("product", Row(("sku", "B-2"), ("name", "Bolt")), Ada);

        // production changes the row the draft read at version 1
        await production.UpdateAsync("product", Row(("sku", "A-1")), 1, Row(("price", 20m)), Ada);

        var entries = await preview.DraftEntriesAsync();
        Assert.Equal([false, true], entries.Select(e => e.Applies));
        Assert.Contains("version 2", entries[0].Problem);
        Assert.Equal(20m, (await preview.GetAsync("product", Row(("sku", "A-1"))))!.Values["price"]);
        Assert.Equal("Bolt", (await preview.GetAsync("product", Row(("sku", "B-2"))))!.Values["name"]);
    }

    [Theory, MemberData(nameof(Engines.Both), MemberType = typeof(Engines))]
    public async Task A_draft_on_an_entity_with_history_keeps_the_moment_of_the_write_and_shows_its_versions(string engine)
    {
        var (engines, model) = await OpenAsync(engine);
        await using var _ = engines;
        await Writer(engines, model).InsertAsync("product", Row(("sku", "A-1"), ("name", "Anvil")), Ada);
        var preview = Writer(engines, model, "p1");
        await preview.InsertAsync("part", Row(("part_id", 1), ("sku", "A-1"), ("note", "first")), Ada);
        await preview.UpdateAsync("part", Row(("part_id", 1)), 1, Row(("note", "second")), Ada);

        var row = await preview.GetAsync("part", Row(("part_id", 1)));
        Assert.Equal(("second", 2L), (row!.Values["note"], row.Version));          // a correction in place raises the version and adds no row
        Assert.Single(await preview.HistoryAsync("part", Row(("part_id", 1))));
        Assert.Null(await Writer(engines, model).GetAsync("part", Row(("part_id", 1))));
        var entries = await preview.DraftEntriesAsync();
        Assert.All(entries, e => Assert.Equal("u-ada", e.Actor.Id));
        Assert.NotNull(entries[0].Changes[0].EffectiveFrom);         // the insert kept the moment it was written
        Assert.Null(entries[1].Changes[0].EffectiveFrom);            // a correction has none
    }

    [Fact]
    public void A_draft_id_that_is_not_one_is_refused()
    {
        var loaded = DataModelLoader.Load(Fixture, "model.yaml");
        Assert.Throws<StoreNotSupportedException>(() => new ModelWriter(loaded.Model!, null!, "postgres", draft: "a b"));
        Assert.Throws<StoreNotSupportedException>(() => new ModelWriter(loaded.Model!, null!, "postgres", draft: "x'; DROP"));
    }

    [Fact]
    public void A_draft_table_is_planned_for_both_engines_and_leaves_the_models_definition_alone()
    {
        var loaded = DataModelLoader.Load(Fixture, "model.yaml");
        foreach (var engine in new[] { "sqlserver", "postgres" })
        {
            var statements = DraftStorage.Plan(engine, "price_book", shared: false);
            Assert.Equal(2, statements.Count);
            Assert.Contains("_draft", statements[0].Text);
            Assert.DoesNotContain(ModelStorage.Plan(loaded.Model!, engine).Statements, s => s.Text.Contains("_draft"));
            Assert.Contains("_instance", DraftStorage.Plan(engine, "price_book", shared: true)[0].Text);
        }
    }
}
