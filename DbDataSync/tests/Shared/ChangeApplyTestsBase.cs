using System.Data.Common;
using System.Globalization;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using Xunit;

namespace DbDataSync.TestSupport;

/// <summary>
/// The scenarios of the <c>ChangeApply</c> writer, written once and run on every engine (linked into each driver's test project, like <c>TestEngines</c>): changes
/// are staged through the driver's own staging provider and applied through the driver's own registered writer, then the target is read back. A subclass gives the
/// engine's connection, DDL spelling and names; the scenarios do not know the engine.
/// <para>
/// A pass here is a list of changes in arrival order, with an operation each, exactly what a reader hands over: the writer sees them through the real staging table
/// (its <c>__Operation</c> marker and <c>__Ordinal</c> arrival order) and nothing is faked.
/// </para>
/// </summary>
public abstract class ChangeApplyTestsBase : IAsyncLifetime
{
    private readonly List<string> _tables = [];
    private DbConnection _connection = null!;

    // ---- what the engine supplies

    protected abstract DbConnection OpenConnection();
    protected abstract IStagingProvider Staging { get; }
    protected abstract IChangeWriter Writer { get; }
    protected abstract TableRef TargetRef(string table);
    protected abstract string IntType { get; }
    protected abstract string TextType { get; }
    protected abstract string DecimalType { get; }
    protected virtual string Quote(string identifier) => $"\"{identifier}\"";
    /// <summary>The name as the engine stores it (Oracle folds to upper case).</summary>
    protected virtual string N(string name) => name;
    protected virtual string DropSql(string table) => $"DROP TABLE {Quote(table)}";
    protected virtual string Qualified(string table) => Quote(table);

    // ---- the tables

    private string ItemsSql(string table) =>
        $"CREATE TABLE {Qualified(table)} ({Quote(N("id"))} {IntType} NOT NULL PRIMARY KEY, {Quote(N("name"))} {TextType} NULL, {Quote(N("amount"))} {DecimalType} NULL)";

    private string PairsSql(string table) =>
        $"CREATE TABLE {Qualified(table)} ({Quote(N("a"))} {IntType} NOT NULL, {Quote(N("b"))} {IntType} NOT NULL, {Quote(N("v"))} {TextType} NULL, PRIMARY KEY ({Quote(N("a"))}, {Quote(N("b"))}))";

    private string UniqueSql(string table) =>
        $"CREATE TABLE {Qualified(table)} ({Quote(N("id"))} {IntType} NOT NULL PRIMARY KEY, {Quote(N("code"))} {TextType} NULL, CONSTRAINT {Quote("UQ_" + table)} UNIQUE ({Quote(N("code"))}))";

    private string KeylessSql(string table) =>
        $"CREATE TABLE {Qualified(table)} ({Quote(N("id"))} {IntType} NULL, {Quote(N("name"))} {TextType} NULL, {Quote(N("amount"))} {DecimalType} NULL)";

    private CachedColumn[] ItemsColumns(bool key = true) =>
    [
        new(N("id"), IntType, !key, key, false),
        new(N("name"), TextType, true, false, false),
        new(N("amount"), DecimalType, true, false, false),
    ];

    private CachedColumn[] PairsColumns() =>
    [
        new(N("a"), IntType, false, true, false, 1),
        new(N("b"), IntType, false, true, false, 2),
        new(N("v"), TextType, true, false, false),
    ];

    private CachedColumn[] UniqueColumns() =>
    [
        new(N("id"), IntType, false, true, false),
        new(N("code"), TextType, true, false, false),
    ];

    private async Task<string> CreateAsync(string kind, Func<string, string> sql)
    {
        var table = N($"ca_{kind}_{Guid.NewGuid():N}");
        await ExecuteAsync(sql(table));
        _tables.Add(table);
        return table;
    }

    public async Task InitializeAsync() => _connection = await Task.Run(OpenConnection);

    public async Task DisposeAsync()
    {
        foreach (var table in _tables)
        {
            try { await ExecuteAsync(DropSql(table)); }
            catch { /* best effort: the scratch database goes with its tables */ }
        }
        await _connection.DisposeAsync();
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    // ---- staging, applying, reading back

    protected sealed record Change(ChangeOperation Op, params object?[] Values);

    private static Change I(params object?[] v) => new(ChangeOperation.Insert, v);
    private static Change U(params object?[] v) => new(ChangeOperation.Update, v);
    private static Change D(params object?[] v) => new(ChangeOperation.Delete, v);

    private static async IAsyncEnumerable<ChangeRow> RowsOf(ChangeSchema schema, IEnumerable<Change> changes)
    {
        foreach (var c in changes)
            yield return new ChangeRow(c.Op, schema, c.Values);
        await Task.CompletedTask;
    }

    private async Task<long> PassAsync(
        string table, IReadOnlyList<string> columnNames, IReadOnlyList<CachedColumn> columns, IEnumerable<Change> changes,
        IReadOnlyDictionary<string, string>? options = null)
    {
        options ??= new Dictionary<string, string>();
        var schema = new ChangeSchema(columnNames.Select(N).ToList());
        var mappings = columnNames.Select(c => new ColumnMapping { SourceColumn = N(c), TargetColumn = N(c) }).ToList();
        var target = TargetRef(table);
        var staged = await Staging.StageAsync(_connection, target, RowsOf(schema, changes), mappings, "change-apply", columns.ToList(), options, CancellationToken.None);
        try
        {
            return (await Writer.ApplyAsync(_connection, target, staged, mappings, "change-apply", columns.ToList(), options, CancellationToken.None)).RowsWritten;
        }
        finally
        {
            await Staging.CleanupAsync(_connection, staged, CancellationToken.None);
        }
    }

    private async Task<List<string>> RowsAsync(string table, params string[] columns)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT {string.Join(", ", columns.Select(c => Quote(N(c))))} FROM {Qualified(table)}";
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
        {
            // Oracle hands every NUMBER back as decimal: only the amount column is a money value, the others are whole numbers
            rows.Add(string.Join("|", Enumerable.Range(0, columns.Length).Select(i =>
                reader.IsDBNull(i) ? "<null>"
                : reader.GetValue(i) switch
                {
                    decimal m when columns[i] == "amount" => m.ToString("0.00", CultureInfo.InvariantCulture),
                    decimal m => m.ToString("0", CultureInfo.InvariantCulture),
                    var v => Convert.ToString(v, CultureInfo.InvariantCulture)!.Trim(),
                })));
        }
        rows.Sort(StringComparer.Ordinal);
        return rows;
    }

    private static List<string> Expect(params string[] rows) => [.. rows.Order(StringComparer.Ordinal)];

    private static readonly string[] ItemColumnNames = ["id", "name", "amount"];

    private Task<long> ItemsPassAsync(string table, IEnumerable<Change> changes, IReadOnlyDictionary<string, string>? options = null) =>
        PassAsync(table, ItemColumnNames, ItemsColumns(), changes, options);

    private Task<List<string>> ItemsAsync(string table) => RowsAsync(table, "id", "name", "amount");

    private async Task SeedItemsAsync(string table, params (int Id, string? Name, decimal? Amount)[] rows)
    {
        foreach (var (id, name, amount) in rows)
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"INSERT INTO {Qualified(table)} ({Quote(N("id"))}, {Quote(N("name"))}, {Quote(N("amount"))}) VALUES ({id}, {(name is null ? "NULL" : $"'{name}'")}, {(amount is null ? "NULL" : amount.Value.ToString(CultureInfo.InvariantCulture))})";
            await cmd.ExecuteNonQueryAsync();
        }
    }

    // ---- the scenarios

    [Fact]
    public async Task Inserts_updates_and_deletes_reach_the_target_and_other_rows_are_left_alone()
    {
        var t = await CreateAsync("mix", ItemsSql);
        await SeedItemsAsync(t, (1, "a", 1m), (2, "b", 2m), (3, "c", 3m), (7, "g", 7m));

        await ItemsPassAsync(t, [U(2, "b2", 20m), D(3, null, null), I(4, "d", 4m), D(5, null, null)]);

        Assert.Equal(Expect("1|a|1.00", "2|b2|20.00", "4|d|4.00", "7|g|7.00"), await ItemsAsync(t));
    }

    [Fact]
    public async Task A_replayed_pass_gives_the_same_target_however_often_it_runs()
    {
        var t = await CreateAsync("replay", ItemsSql);
        await SeedItemsAsync(t, (1, "a", 1m), (2, "b", 2m), (3, "c", 3m));
        Change[] pass = [U(2, "b2", 20m), D(3, null, null), I(4, "d", 4m), I(5, "e", 5m), U(5, "e2", 50m), I(6, "f", 6m), D(6, null, null)];
        var expected = Expect("1|a|1.00", "2|b2|20.00", "4|d|4.00", "5|e2|50.00");

        for (var run = 1; run <= 3; run++)
        {
            await ItemsPassAsync(t, pass);
            Assert.Equal(expected, await ItemsAsync(t));
        }
    }

    [Fact]
    public async Task Several_changes_to_one_key_collapse_to_the_last_by_arrival()
    {
        var t = await CreateAsync("collapse", ItemsSql);
        await SeedItemsAsync(t, (2, "b", 2m), (3, "c", 3m), (4, "d", 4m), (6, "f", 6m), (8, "h", 8m));

        await ItemsPassAsync(t,
        [
            I(10, "ten", 10m), U(10, "ten-final", 11m),      // insert then update: an insert of the final values
            I(11, "eleven", 11m), D(11, null, null),         // insert then delete: nothing
            U(2, "b-mid", 20m), D(2, null, null),            // update then delete: a delete
            U(3, "c1", 31m), U(3, "c2", 32m), U(3, "c3", 33m), // repeated updates: the last
            D(4, null, null), I(4, "d-reborn", 44m),         // delete then insert: the final values
            D(6, null, null), I(60, "f-moved", 6m),          // a key change: delete of the old key, insert of the new one
            U(8, "h2", 82m), D(8, null, null), I(8, "h3", 83m), // update, delete, insert: the insert's values
        ]);

        Assert.Equal(Expect("3|c3|33.00", "4|d-reborn|44.00", "10|ten-final|11.00", "60|f-moved|6.00", "8|h3|83.00"), await ItemsAsync(t));
    }

    [Fact]
    public async Task Nulls_in_non_key_columns_are_carried_both_ways()
    {
        var t = await CreateAsync("nulls", ItemsSql);
        await SeedItemsAsync(t, (1, "a", 1m), (2, null, null));

        await ItemsPassAsync(t, [U(1, null, null), U(2, "named", 5m), I(3, null, 3m)]);

        Assert.Equal(Expect("1|<null>|<null>", "2|named|5.00", "3|<null>|3.00"), await ItemsAsync(t));
    }

    [Fact]
    public async Task A_composite_key_matches_on_both_columns()
    {
        var t = await CreateAsync("pairs", PairsSql);
        foreach (var (a, b, v) in new[] { (1, 1, "a"), (1, 2, "b"), (2, 1, "c") })
            await ExecuteAsync($"INSERT INTO {Qualified(t)} ({Quote(N("a"))}, {Quote(N("b"))}, {Quote(N("v"))}) VALUES ({a}, {b}, '{v}')");

        await PassAsync(t, ["a", "b", "v"], PairsColumns(),
        [
            U(1, 2, "b2"), D(2, 1, null), I(2, 2, "d"), U(2, 2, "d2"), I(1, 3, "e"),
            I(1, 1, "a-first"), D(1, 1, null),               // (1,1) goes; (1,2) and (2,1) are other rows
        ]);

        Assert.Equal(Expect("1|2|b2", "1|3|e", "2|2|d2"), await RowsAsync(t, "a", "b", "v"));
    }

    [Fact]
    public async Task A_unique_value_can_move_from_a_deleted_row_to_an_inserted_one()
    {
        var t = await CreateAsync("unique", UniqueSql);
        await ExecuteAsync($"INSERT INTO {Qualified(t)} ({Quote(N("id"))}, {Quote(N("code"))}) VALUES (1, 'X')");

        // the insert is listed first: the writer deletes before it inserts, so the unique index never sees two 'X'
        await PassAsync(t, ["id", "code"], UniqueColumns(), [I(2, "X"), D(1, null)]);

        Assert.Equal(Expect("2|X"), await RowsAsync(t, "id", "code"));
    }

    [Fact]
    public async Task A_small_apply_batch_splits_every_statement_into_slices_and_changes_nothing_in_the_result()
    {
        var t = await CreateAsync("chunk", ItemsSql);
        await SeedItemsAsync(t, (1, "a", 1m), (2, "b", 2m), (3, "c", 3m), (4, "d", 4m));
        var options = new Dictionary<string, string> { [ApplyBatch.OptionName] = "2" };

        // 9 staged rows, 7 after the collapse: the slices (2 rows each) do not line up with the collapsed keys
        await ItemsPassAsync(t,
        [
            U(1, "a1", 10m), U(1, "a2", 20m), D(2, null, null), U(3, "c2", 30m), I(5, "e", 5m),
            I(6, "f", 6m), I(7, "g", 7m), D(4, null, null), I(8, "h", 8m),
        ], options);

        Assert.Equal(Expect("1|a2|20.00", "3|c2|30.00", "5|e|5.00", "6|f|6.00", "7|g|7.00", "8|h|8.00"), await ItemsAsync(t));
    }

    [Fact]
    public async Task An_empty_pass_changes_nothing()
    {
        var t = await CreateAsync("empty", ItemsSql);
        await SeedItemsAsync(t, (1, "a", 1m));

        var written = await ItemsPassAsync(t, []);

        Assert.Equal(0, written);
        Assert.Equal(Expect("1|a|1.00"), await ItemsAsync(t));
    }

    [Fact]
    public async Task A_target_without_a_primary_key_is_refused_before_anything_is_written()
    {
        var t = await CreateAsync("keyless", KeylessSql);
        await SeedItemsAsync(t, (1, "a", 1m));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => PassAsync(t, ItemColumnNames, ItemsColumns(key: false), [I(2, "b", 2m)]));

        Assert.Contains("no primary key", ex.Message);
        Assert.Equal(Expect("1|a|1.00"), await ItemsAsync(t));
    }

    [Fact]
    public async Task The_writer_does_not_reconcile_and_is_the_registered_kind()
    {
        Assert.Equal("ChangeApply", Writer.Kind);
        Assert.False(Writer.SupportsReconciliation);
        await Task.CompletedTask;
    }
}
