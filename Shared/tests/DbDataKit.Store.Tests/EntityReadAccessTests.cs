using DbDataKit.Access;
using DbDataKit.DataModel;
using DbDataKit.DataModel.Storage;
using DbDataKit.Schema.Tests;

namespace DbDataKit.Store.Tests;

/// <summary>
/// Plan 22a GP11 (QG2, QR1, QR3, QR6), the Store's half, on SQL Server and PostgreSQL: the reads of a <see cref="ModelWriter"/> apply the caller's row rules as a semi-join to a rules table of the model's own database (include, exclude, strict values with NULL
/// matched only by NULL, several columns as a tuple), a person with no matching rule reads no row, a row they may not read is not found (never refused), the count of a list is over the filter, and a write is never filtered.
/// </summary>
public sealed class EntityReadAccessTests
{
    private const string Fixture = """
        schemaVersion: 2
        name: sales
        storage: {connection: mdm_sql}
        access:
          row:
            - dataset: territory
              groups:
                - id: by_region
                  rules:
                    - { id: region, effect: include, table: rules.region_access, subject: who, columns: { region: region_code } }
                    - { id: not_closed, effect: exclude, table: rules.closed_codes, subject: who, columns: { code: code } }
            - dataset: quota
              groups:
                - id: by_pair
                  rules:
                    - { id: pair, effect: include, table: rules.pair_access, subject: who, columns: { region: region_code, tier: tier_code } }
            - dataset: history_row
              groups:
                - id: history_by_region
                  rules:
                    - { id: history_region, effect: include, table: rules.region_access, subject: who, columns: { region: region_code } }
        entities:
          territory:
            key: [code]
            attributes:
              code: {type: VARCHAR(10), required: true}
              region: {type: VARCHAR(20)}
          quota:
            key: [id]
            attributes:
              id: {type: INTEGER, required: true}
              region: {type: VARCHAR(20)}
              tier: {type: VARCHAR(10)}
          history_row:
            key: [id]
            history: effective_dated
            attributes:
              id: {type: INTEGER, required: true}
              region: {type: VARCHAR(20)}
        """;

    private static readonly WriteActor Ada = new("u-ada", "Ada");

    private static Dictionary<string, object?> Row(params (string, object?)[] values) => values.ToDictionary(v => v.Item1, v => v.Item2);

    private sealed class Ledger : IRulesTableLedger
    {
        public RulesTableRecord Touch(string key, DateTimeOffset now) => new(key, now, null);
        public void Changed(string key, DateTimeOffset at, string product, string reason) { }
    }

    private sealed class Subjects(ModelWriter writer) : IRulesSubjects
    {
        public IReadOnlyCollection<string> Names(string table, string column) => writer.RulesSubjects(table, column);
    }

    private static async Task<(Engines Engines, DbDataKit.DataModel.DataModel Model)> OpenAsync(string engine)
    {
        var engines = await Engines.OpenAsync(engine);
        var loaded = DataModelLoader.Load(Fixture, "model.yaml");
        Assert.True(loaded.Ok, string.Join("; ", loaded.Diagnostics.Select(d => d.Message)));
        await ModelStorage.CreateAsync(engines.Connection, ModelStorage.Plan(loaded.Model!, engine));
        await engines.ExecuteAsync("CREATE SCHEMA rules");
        await engines.ExecuteAsync("CREATE TABLE rules.region_access (who VARCHAR(40), region_code VARCHAR(20))");
        await engines.ExecuteAsync("CREATE TABLE rules.closed_codes (who VARCHAR(40), code VARCHAR(10))");
        await engines.ExecuteAsync("CREATE TABLE rules.pair_access (who VARCHAR(40), region_code VARCHAR(20), tier_code VARCHAR(10))");
        // east: East (listed twice: a row of the entity is still once), minus the closed code E3; emea: both regions; nullers: the rows with no region; pairs: (East, gold) and (NULL, silver)
        await engines.ExecuteAsync("INSERT INTO rules.region_access VALUES ('east', 'East'), ('east', 'East'), ('emea', 'East'), ('emea', 'West'), ('nullers', NULL)");
        await engines.ExecuteAsync("INSERT INTO rules.closed_codes VALUES ('east', 'E3')");
        await engines.ExecuteAsync("INSERT INTO rules.pair_access VALUES ('pairs', 'East', 'gold'), ('pairs', NULL, 'silver')");
        var writer = new ModelWriter(loaded.Model!, engines.Connection, engine);
        foreach (var (code, region) in new[] { ("E1", "East"), ("E2", "East"), ("E3", "East"), ("W1", "West"), ("N1", "North"), ("X1", null) })
            await writer.InsertAsync("territory", Row(("code", code), ("region", region)), Ada);
        foreach (var (id, region, tier) in new[] { (1, "East", "gold"), (2, "East", "silver"), (3, null, "silver"), (4, null, "gold"), (5, "West", "silver") })
            await writer.InsertAsync("quota", Row(("id", id), ("region", region), ("tier", tier)), Ada);
        return (engines, loaded.Model!);
    }

    private static readonly AccessResolver Resolver = new(new Ledger());

    /// <summary>A writer that reads as the caller: the rules resolved as the Gateway resolves them (the names of each rules table on the writer's own connection).</summary>
    private static ModelWriter As(Engines engines, DbDataKit.DataModel.DataModel model, string id, params string[] groups)
    {
        var writer = new ModelWriter(model, engines.Connection, engines.Engine);
        var decision = Resolver.Resolve(new AccessCaller(id, id, groups), new AccessScope("entity-model/sales", model.Access!, t => t, new Subjects(writer)));
        writer.ReadAccess = entity => decision.DatasetOf(entity) is { } d ? new EntityReadAccess(RowPredicate.Of(d), d.Refusal) : null;
        return writer;
    }

    private static async Task<string[]> Codes(ModelWriter writer, string entity = "territory", string column = "code") =>
        [.. (await writer.ListAsync(entity)).Select(r => Convert.ToString(r.Values[column])!)];

    [Theory, MemberData(nameof(Engines.Both), MemberType = typeof(Engines))]
    public async Task A_person_in_a_group_reads_exactly_the_rows_the_rules_give_and_two_callers_two_answers(string engine)
    {
        var (engines, model) = await OpenAsync(engine);
        await using var _ = engines;
        // east: East but not the closed E3; the duplicate rule row does not repeat a row
        Assert.Equal(["E1", "E2"], await Codes(As(engines, model, "u1", "east")));
        Assert.Equal(["E1", "E2", "E3", "W1"], await Codes(As(engines, model, "u2", "emea")));
        // NULL is a value matched only by a NULL: nullers sees the row with no region and no other
        Assert.Equal(["X1"], await Codes(As(engines, model, "u3", "nullers")));
        // nobody (D2), and a person's own name is a subject too when a rule says so (the rules name groups here, so a person with a group's name as their id is not that group's)
        Assert.Empty(await Codes(As(engines, model, "u4", "nobody")));
        // nobody is exempt (QR3): an administrator is a caller like any other, the filter does not look at roles
        Assert.Empty(await Codes(As(engines, model, "root", "administrators")));
        // an entity with no rules is read in full, and a writer with no access reads everything (a runner landing rows)
        Assert.Equal(["E1", "E2", "E3", "N1", "W1", "X1"], await Codes(new ModelWriter(model, engines.Connection, engine)));
    }

    [Theory, MemberData(nameof(Engines.Both), MemberType = typeof(Engines))]
    public async Task Several_columns_match_together_as_a_tuple_and_a_null_matches_only_a_null(string engine)
    {
        var (engines, model) = await OpenAsync(engine);
        await using var _ = engines;
        // (East, gold) is row 1; (NULL, silver) is row 3: (East, silver), (NULL, gold) and (West, silver) match no pair
        Assert.Equal(["1", "3"], await Codes(As(engines, model, "u1", "pairs"), "quota", "id"));
    }

    [Theory, MemberData(nameof(Engines.Both), MemberType = typeof(Engines))]
    public async Task The_count_of_a_list_get_and_as_of_and_history_of_a_hidden_row_are_over_the_filter_and_never_say_forbidden(string engine)
    {
        var (engines, model) = await OpenAsync(engine);
        await using var _ = engines;
        var east = As(engines, model, "u1", "east");
        Assert.Equal(2, await east.CountAsync("territory"));
        Assert.Equal(6, await new ModelWriter(model, engines.Connection, engine).CountAsync("territory"));
        Assert.Single(await east.ListAsync("territory", 1, 1));   // paging is over the filtered rows
        Assert.NotNull(await east.GetAsync("territory", Row(("code", "E1"))));
        Assert.Null(await east.GetAsync("territory", Row(("code", "E3"))));    // excluded
        Assert.Null(await east.GetAsync("territory", Row(("code", "W1"))));    // another region
        Assert.Null(await east.GetAsync("territory", Row(("code", "NOPE"))));  // never there: the same answer, so existence is not revealed

        // a row with history: every version is a row of the dataset and is judged by its own values (v1 East from ten days ago, v2 West from five)
        var plain = new ModelWriter(model, engines.Connection, engine);
        var now = DateTimeOffset.UtcNow;
        await plain.ApplyAsync([RowChange.Insert("history_row", Row(("id", 1), ("region", "East")), now.AddDays(-10))], Ada);
        await plain.ApplyAsync([RowChange.Update("history_row", Row(("id", 1)), 1, Row(("region", "West")), now.AddDays(-5))], Ada);
        var key = Row(("id", 1));
        Assert.Null(await east.GetAsync("history_row", key));                                    // the open version is West
        Assert.NotNull(await east.GetAsOfAsync("history_row", key, now.AddDays(-7)));            // the version that held then was East
        Assert.Single(await east.HistoryAsync("history_row", key));                              // only the East version is a row east may read
        Assert.Equal(2, (await As(engines, model, "u2", "emea").HistoryAsync("history_row", key)).Count);
        Assert.Empty(await east.HistoryAsync("history_row", Row(("id", 99))));                   // a row that was never there: the same empty answer
    }

    [Theory, MemberData(nameof(Engines.Both), MemberType = typeof(Engines))]
    public async Task A_write_is_never_filtered_and_a_rules_table_that_cannot_be_read_refuses_the_read(string engine)
    {
        var (engines, model) = await OpenAsync(engine);
        await using var _ = engines;
        var nobody = As(engines, model, "u4", "nobody");
        // QR6: a person who cannot read a row may still change it (the Store's roles and approval decide), and the write's own reads see it
        await nobody.UpdateAsync("territory", Row(("code", "W1")), 1, Row(("region", "East")), Ada);
        Assert.Null(await nobody.GetAsync("territory", Row(("code", "W1"))));
        Assert.Equal(["E1", "E2", "W1"], await Codes(As(engines, model, "u1", "east", "emea")));   // east and emea together: E3 is excluded for east only, emea includes it
        // a rules table that is gone refuses (D2), and no row is served
        await engines.ExecuteAsync("DROP TABLE rules.closed_codes");
        var refused = As(engines, model, "u1", "east");
        await Assert.ThrowsAsync<StoreReadRefusedException>(() => refused.ListAsync("territory"));
    }

    [Theory, MemberData(nameof(Engines.Both), MemberType = typeof(Engines))]
    public async Task The_rows_of_a_rules_table_that_name_the_caller_and_the_values_no_rule_row_covers_are_read_for_the_explanation(string engine)
    {
        var (engines, model) = await OpenAsync(engine);
        await using var _ = engines;
        var writer = new ModelWriter(model, engines.Connection, engine);
        var region = model.Access!.RowRulesOf("territory")!.Groups[0].Rules[0];
        // the rows that name east, as the table spells them (a duplicate row is two rows), nothing for a name it does not hold
        var rows = await writer.RulesRowsAsync(region, ["east"], 10);
        Assert.Equal(["East", "East"], rows.Select(r => r.Values["region_code"]));
        Assert.All(rows, r => Assert.Equal("east", r.Subject));
        Assert.Single(await writer.RulesRowsAsync(region, ["nullers"], 10), r => r.Values["region_code"] is null);   // a NULL is a null value, not a text
        Assert.Empty(await writer.RulesRowsAsync(region, ["nobody"], 10));
        Assert.Single(await writer.RulesRowsAsync(region, ["east"], 1));   // limited

        // territory regions: East (3 rows), West, North, NULL; the table covers East, West and NULL: North is uncovered, with one row
        var uncovered = await writer.UncoveredAsync("territory", region, 10);
        Assert.Equal(1, uncovered.Count);
        Assert.Equal("North", uncovered.Values[0].Values["region"]);
        Assert.Equal(1, uncovered.Values[0].Rows);
        // without the NULL row of the table the NULL region is uncovered too, counted and listed as a null value
        await engines.ExecuteAsync("DELETE FROM rules.region_access WHERE region_code IS NULL");
        var again = await writer.UncoveredAsync("territory", region, 10);
        Assert.Equal(2, again.Count);
        Assert.Contains(again.Values, v => v.Values["region"] is null);
        // a pair of columns is matched together: quota has (East, gold) (East, silver) (NULL, silver) (NULL, gold) (West, silver); the table has (East, gold), (NULL, silver)
        var pair = model.Access!.RowRulesOf("quota")!.Groups[0].Rules[0];
        var pairs = await writer.UncoveredAsync("quota", pair, 2);
        Assert.Equal(3, pairs.Count);
        Assert.True(pairs.Truncated);
        Assert.Equal(2, pairs.Values.Count);
    }
}
