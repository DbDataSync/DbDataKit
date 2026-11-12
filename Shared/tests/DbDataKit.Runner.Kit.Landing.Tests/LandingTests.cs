using System.Data.Common;
using System.Text;
using DbDataKit.Dialect;
using DbDataKit.Runner.Kit.Formats;
using DbDataKit.Runner.Kit.Landing;
using DbDataKit.Transfer;
using DbDataKit.Transfer.Conformance;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace DbDataKit.Runner.Kit.Landing.Tests;

/// <summary>
/// Plan 27a IX11 on three engines: DuckDB always (in process), PostgreSQL when <c>DBDATAKIT_TEST_POSTGRES</c> holds a connection string and SQL Server when <c>DBDATAKIT_TEST_SQLSERVER</c> does (the suite's test environment sets both:
/// <c>eval "$(testenv/testenv.sh env dbdatakit)"</c>). Every test makes a table of its own and drops it.
/// </summary>
public class LandingTests
{
    public static IEnumerable<TheoryDataRow<string>> Engines() => [new("duckdb"), new("postgres"), new("sqlserver")];

    private static readonly TransferSchema Schema = new(
    [
        new("id", CanonicalType.Of(CanonicalKind.Int64), Nullable: false),
        new("name", CanonicalType.String(100)),
        new("amount", new CanonicalType(CanonicalKind.Decimal, Precision: 18, Scale: 2)),
        new("seen", new CanonicalType(CanonicalKind.Timestamp, Scale: 6)),
        new("active", CanonicalType.Of(CanonicalKind.Boolean)),
        new("day", CanonicalType.Of(CanonicalKind.Date)),
    ]);

    private sealed class Harness(string name, ISqlDialect dialect, DbConnection connection, WriteGate gate, TableName table) : IAsyncDisposable
    {
        public string Name => name;
        public ISqlDialect Dialect => dialect;
        public WriteGate Gate => gate;
        public TableName Table => table;

        public StagingLander Lander(LandOptions? options = null) => new(gate, dialect, table, Schema, options);

        public async Task<List<object?[]>> ReadAsync(string key)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {string.Join(", ", Schema.Columns.Select(c => dialect.Quote(c.Name)))}, {dialect.Quote("__row")} FROM {dialect.QuoteQualified(table.Parts)} WHERE {dialect.Quote("__batch")} = '{key}' ORDER BY {dialect.Quote("__row")}";
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<object?[]>();
            while (await reader.ReadAsync())
            {
                var row = new object[reader.FieldCount];
                reader.GetValues(row);
                rows.Add(row.Select(v => v is DBNull ? null : v).ToArray());
            }

            return rows;
        }

        public async Task<long> TotalAsync()
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {dialect.QuoteQualified(table.Parts)}";
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"DROP TABLE IF EXISTS {dialect.QuoteQualified(table.Parts)}";
                await command.ExecuteNonQueryAsync();
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }
    }

    private static async Task<Harness?> OpenAsync(string engine)
    {
        var tag = "ddk_land_" + Guid.NewGuid().ToString("N")[..10];
        DbConnection connection;
        ISqlDialect dialect;
        switch (engine)
        {
            case "duckdb":
                dialect = DialectCatalog.For(EngineId.DuckDb);
                connection = await DuckDbEngine.Create().OpenAsync(CancellationToken.None);
                break;
            case "postgres":
                if (Environment.GetEnvironmentVariable("DBDATAKIT_TEST_POSTGRES") is not { Length: > 0 } pg) return null;
                dialect = DialectCatalog.For(EngineId.Postgres);
                connection = new NpgsqlConnection(pg);
                await connection.OpenAsync();
                break;
            default:
                if (Environment.GetEnvironmentVariable("DBDATAKIT_TEST_SQLSERVER") is not { Length: > 0 } ms) return null;
                dialect = DialectCatalog.For(EngineId.SqlServer);
                connection = new SqlConnection(ms);
                await connection.OpenAsync();
                break;
        }

        var gate = new WriteGate(new NoDispose(new AdoWriteExecutor(connection, dialect)), new WriteGateOptions("landing-test", new MemoryStatementLog()));
        return new Harness(engine, dialect, connection, gate, new TableName(engine == "sqlserver" ? "dbo" : null, tag));
    }

    private sealed class NoDispose(IWriteExecutor inner) : IWriteExecutor
    {
        public Task BeginAsync(CancellationToken ct) => inner.BeginAsync(ct);
        public Task CommitAsync(CancellationToken ct) => inner.CommitAsync(ct);
        public Task RollbackAsync(CancellationToken ct) => inner.RollbackAsync(ct);
        public Task<long> ExecuteAsync(SqlStatement statement, CancellationToken ct) => inner.ExecuteAsync(statement, ct);
        public Task<object?> ScalarAsync(SqlStatement statement, CancellationToken ct) => inner.ScalarAsync(statement, ct);
        public Task<bool> TryLockAsync(string resource, bool shared, CancellationToken ct) => inner.TryLockAsync(resource, shared, ct);
        public Task UnlockAsync(string resource, bool shared, CancellationToken ct) => inner.UnlockAsync(resource, shared, ct);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async IAsyncEnumerable<IReadOnlyList<object?>> Typed(params object?[][] rows)
    {
        foreach (var row in rows) yield return row;
        await Task.CompletedTask;
    }

    private static readonly DateTime Seen = new(2026, 11, 11, 9, 30, 15, 250, DateTimeKind.Utc);

    private static object?[][] Batch(int from, int count) =>
        [.. Enumerable.Range(from, count).Select(i => new object?[] { (long)i, "name " + i, i * 1.25m, Seen.AddMinutes(i), i % 2 == 0, new DateOnly(2026, 1, 1).AddDays(i) })];

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task A_batch_lands_in_a_staging_table_and_landing_it_again_under_the_same_key_changes_nothing(string engine)
    {
        await using var h = await OpenAsync(engine);
        Assert.SkipUnless(h is not null, $"set the connection variable of {engine} to run");
        var lander = h!.Lander();
        await lander.EnsureTableAsync();
        await lander.EnsureTableAsync();                                   // already there: nothing happens

        var first = await lander.LandAsync("file-a", Typed(Batch(1, 5)));
        Assert.Equal(new LandingResult("file-a", 5, false), first);
        var before = await h.ReadAsync("file-a");
        Assert.Equal(5, before.Count);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, before.Select(r => Convert.ToInt64(r[0])).ToArray());
        Assert.Equal("name 3", before[2][1]);
        Assert.Equal(3.75m, Convert.ToDecimal(before[2][2]));
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, before.Select(r => Convert.ToInt64(r[6])).ToArray());   // the record number

        var again = await lander.LandAsync("file-a", Typed(Batch(1, 5)));
        Assert.True(again.AlreadyLanded);
        Assert.Equal(5, again.Rows);
        Assert.Equal(5, await h.TotalAsync());
        var after = await h.ReadAsync("file-a");
        Assert.Equal(before.Select(r => string.Join("|", r)), after.Select(r => string.Join("|", r)));

        var other = await lander.LandAsync("file-b", Typed(Batch(10, 2)));
        Assert.False(other.AlreadyLanded);
        Assert.Equal(7, await h.TotalAsync());
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task A_csv_lands_with_its_text_coerced_to_the_columns_and_a_bad_record_is_refused_with_nothing_written(string engine)
    {
        await using var h = await OpenAsync(engine);
        Assert.SkipUnless(h is not null, $"set the connection variable of {engine} to run");
        var lander = h!.Lander(new LandOptions { Source = "orders.csv" });
        await lander.EnsureTableAsync();
        var good = "id,name,amount,seen,active,day\n1,Ann,12.50,2026-11-11T09:30:15Z,true,2026-11-11\n2,\"Bo, Jr\",3,2026-11-12T00:00:00Z,0,2026-11-12\n";
        var result = await lander.LandAsync("orders-1", DelimitedReader.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(good)), DelimitedOptions.Csv with { EmptyAsNull = true }));
        Assert.Equal(2, result.Rows);
        var rows = await h.ReadAsync("orders-1");
        Assert.Equal("Bo, Jr", rows[1][1]);
        Assert.Equal(12.5m, Convert.ToDecimal(rows[0][2]));
        Assert.Equal(false, rows[1][4]);

        var bad = "id,name,amount,seen,active,day\n3,Cy,1.00,2026-11-11T09:30:15Z,true,2026-11-11\nnot-a-number,Di,1.00,2026-11-11T09:30:15Z,true,2026-11-11\n";
        var ex = await Assert.ThrowsAsync<BadRowException>(() => lander.LandAsync("orders-2", DelimitedReader.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(bad)))));
        Assert.Equal(2, ex.Row.Row);
        Assert.Contains("orders.csv", ex.Message);
        Assert.Contains("column id", ex.Message);
        Assert.DoesNotContain("not-a-number", ex.Message);
        Assert.Equal(0, await lander.CountAsync("orders-2"));              // the batch is all or nothing: record 1 was rolled back with it
        Assert.Equal(2, await h.TotalAsync());
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task Collect_lands_the_good_records_and_keeps_where_the_bad_ones_were_and_replace_lands_a_batch_anew(string engine)
    {
        await using var h = await OpenAsync(engine);
        Assert.SkipUnless(h is not null, $"set the connection variable of {engine} to run");
        var bad = new BadRows();
        var lander = h!.Lander(new LandOptions { OnBadRow = BadRowDecision.Collect, Bad = bad });
        await lander.EnsureTableAsync();
        var text = "id,name,amount,seen,active,day\n1,Ann,1,2026-11-11T09:30:15Z,true,2026-11-11\nx,Bo,1,2026-11-11T09:30:15Z,true,2026-11-11\n3,Cy,1,2026-11-11T09:30:15Z,maybe,2026-11-11\n4,Di,1,2026-11-11T09:30:15Z,true,2026-11-11\n";
        var result = await lander.LandAsync("mixed", DelimitedReader.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(text))));
        Assert.Equal(2, result.Rows);
        Assert.Equal(2, result.Rejected);
        Assert.Equal(new long[] { 2, 3 }, bad.Rows.Select(b => b.Row).ToArray());
        Assert.DoesNotContain(bad.Rows, b => b.Reason.Contains("maybe") || b.Reason.Contains("Bo"));

        var replace = h.Lander(new LandOptions { Replace = true });
        var replaced = await replace.LandAsync("mixed", Typed(Batch(7, 3)));
        Assert.False(replaced.AlreadyLanded);
        Assert.Equal(3, await h.TotalAsync());
        Assert.Equal(new long[] { 7, 8, 9 }, (await h.ReadAsync("mixed")).Select(r => Convert.ToInt64(r[0])).ToArray());
    }

    [Fact]
    public async Task A_read_only_gate_refuses_to_make_the_table()
    {
        var dialect = DialectCatalog.For(EngineId.DuckDb);
        await using var connection = await DuckDbEngine.Create().OpenAsync(CancellationToken.None);
        var readOnly = new WriteGate(new NoDispose(new AdoWriteExecutor(connection, dialect)), new WriteGateOptions("ro", new MemoryStatementLog()) { Access = AccessMode.ReadOnly });
        var lander = new StagingLander(readOnly, dialect, new TableName(null, "ddk_ro"), Schema);
        await Assert.ThrowsAsync<GateRefusedException>(() => lander.EnsureTableAsync());
    }

    [Theory]
    [InlineData(CanonicalKind.Int32, "42", 42)]
    [InlineData(CanonicalKind.Int64, "-9000000000", -9000000000L)]
    [InlineData(CanonicalKind.Boolean, "TRUE", true)]
    [InlineData(CanonicalKind.Double, "1e3", 1000.0)]
    public void Text_is_read_as_the_column_type(CanonicalKind kind, string text, object expected) =>
        Assert.Equal(expected, ValueCoercion.Coerce(text, CanonicalType.Of(kind)));

    [Theory]
    [InlineData(CanonicalKind.Int32, "4.5")]
    [InlineData(CanonicalKind.Int32, "99999999999")]
    [InlineData(CanonicalKind.Int8, "300")]
    [InlineData(CanonicalKind.Boolean, "yes")]
    [InlineData(CanonicalKind.Date, "11/12/2026")]
    [InlineData(CanonicalKind.Guid, "nope")]
    public void A_value_that_does_not_fit_is_refused_not_rounded_or_guessed_and_the_message_holds_no_value(CanonicalKind kind, string text)
    {
        var ex = Assert.Throws<FormatException>(() => ValueCoercion.Coerce(text, CanonicalType.Of(kind)));
        Assert.DoesNotContain(text, ex.Message);
    }

    [Fact]
    public void Numbers_and_dates_from_excel_or_json_are_coerced_without_losing_anything()
    {
        Assert.Equal(7, ValueCoercion.Coerce(7L, CanonicalType.Of(CanonicalKind.Int32)));
        Assert.Equal(7, ValueCoercion.Coerce(7.0, CanonicalType.Of(CanonicalKind.Int32)));
        Assert.Throws<FormatException>(() => ValueCoercion.Coerce(7.5, CanonicalType.Of(CanonicalKind.Int32)));
        Assert.Equal(2.5m, ValueCoercion.Coerce(2.5, new CanonicalType(CanonicalKind.Decimal, Precision: 10, Scale: 2)));
        Assert.Equal(new DateOnly(2026, 11, 11), ValueCoercion.Coerce(new DateTime(2026, 11, 11, 13, 0, 0), CanonicalType.Of(CanonicalKind.Date)));
        Assert.Null(ValueCoercion.Coerce(null, CanonicalType.Of(CanonicalKind.Int32)));
    }
}
