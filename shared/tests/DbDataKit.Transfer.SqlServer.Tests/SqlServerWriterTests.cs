using DbDataKit.Dialect;
using DbDataKit.Transfer.Conformance;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DbDataKit.Transfer.SqlServer.Tests;

public class SqlServerBulkSettingsTests
{
    [Fact]
    public void Defaults_keep_nulls_and_have_no_timeout()
    {
        var s = SqlServerBulkSettings.From(new BulkOptions(), hasExternalTransaction: false);
        Assert.Equal(SqlBulkCopyOptions.KeepNulls, s.CopyOptions);
        Assert.Equal(20_000, s.BatchSize);
        Assert.Equal(0, s.TimeoutSeconds);
        Assert.Equal(0, s.NotifyAfter);
    }

    [Fact]
    public void Options_map_to_flags_timeout_and_progress()
    {
        var o = new BulkOptions { KeepIdentity = true, KeepNulls = false, BatchSize = 500, Timeout = TimeSpan.FromMilliseconds(1500), Progress = new Progress<long>(_ => { }) };
        var s = SqlServerBulkSettings.From(o, false);
        Assert.Equal(SqlBulkCopyOptions.KeepIdentity, s.CopyOptions);
        Assert.Equal(2, s.TimeoutSeconds);          // rounded up
        Assert.Equal(500, s.NotifyAfter);
    }

    [Fact]
    public void Per_batch_transactions_use_the_internal_transaction_and_refuse_an_external_one()
    {
        var o = new BulkOptions { Transaction = TransactionMode.PerBatch };
        Assert.True(SqlServerBulkSettings.From(o, false).CopyOptions.HasFlag(SqlBulkCopyOptions.UseInternalTransaction));
        Assert.Throws<ArgumentException>(() => SqlServerBulkSettings.From(o, true));
    }

    [Fact]
    public void Single_transaction_needs_an_external_one()
    {
        var o = new BulkOptions { Transaction = TransactionMode.Single };
        Assert.Throws<ArgumentException>(() => SqlServerBulkSettings.From(o, false));
        Assert.False(SqlServerBulkSettings.From(o, true).CopyOptions.HasFlag(SqlBulkCopyOptions.UseInternalTransaction));
    }

    [Fact]
    public void Negative_batch_size_is_refused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => SqlServerBulkSettings.From(new BulkOptions { BatchSize = -1 }, false));

    [Fact]
    public void Target_is_quoted_and_columns_map_by_name()
    {
        var schema = new TransferSchema([new("a]b", CanonicalType.Of(CanonicalKind.Int32)), new("c", CanonicalType.String())]);
        var (table, map) = SqlServerBulkTarget.Describe(new WriteTarget(new TableName("dbo", "t]1"), schema));
        Assert.Equal("[dbo].[t]]1]", table);
        Assert.Equal([(0, "a]b"), (1, "c")], map);
    }
}

public class ChainedReaderTests
{
    private static readonly TransferSchema Schema = new(
    [
        new("tiny", CanonicalType.Of(CanonicalKind.Int8)),
        new("u16", new CanonicalType(CanonicalKind.Int16, Unsigned: true)),
        new("u64", new CanonicalType(CanonicalKind.Int64, Unsigned: true)),
        new("day", CanonicalType.Of(CanonicalKind.Date)),
        new("t", CanonicalType.Time(3)),
        new("s", CanonicalType.String()),
    ]);

    private static async IAsyncEnumerable<DataBatch> Batches(params object?[][][] sets)
    {
        foreach (var s in sets) { await Task.Yield(); yield return new RowDataBatch(Schema, s); }
    }

    private static object?[] Row(int i) => [(sbyte)-i, (ushort)(60000 + i), (ulong)ulong.MaxValue - (ulong)i, new DateOnly(2024, 1, 1 + i), new TimeOnly(1, 2, i), i % 2 == 0 ? null : "x" + i];

    [Fact]
    public async Task Rows_of_many_batches_stream_through_one_reader_with_adapted_values()
    {
        await using var r = new ChainedBatchReader(Batches([Row(1), Row(2)], [], [Row(3)]), Schema, EngineId.SqlServer, TestContext.Current.CancellationToken);
        Assert.Equal(typeof(short), r.GetFieldType(0));
        Assert.Equal(typeof(int), r.GetFieldType(1));
        Assert.Equal(typeof(decimal), r.GetFieldType(2));
        Assert.Equal(typeof(DateTime), r.GetFieldType(3));
        Assert.Equal(typeof(TimeSpan), r.GetFieldType(4));
        var seen = new List<object[]>();
        while (await r.ReadAsync(TestContext.Current.CancellationToken))
        {
            var v = new object[6];
            r.GetValues(v);
            seen.Add(v);
        }
        Assert.Equal(3, seen.Count);
        Assert.Equal(3, r.RowsRead);
        Assert.Equal((short)-1, seen[0][0]);
        Assert.Equal(60002, seen[1][1]);
        Assert.Equal((decimal)ulong.MaxValue - 3, seen[2][2]);
        Assert.Equal(new DateTime(2024, 1, 3), seen[1][3]);
        Assert.Equal(new TimeSpan(1, 2, 3), seen[2][4]);
        Assert.Equal(DBNull.Value, seen[1][5]);
        Assert.Equal("x1", seen[0][5]);
    }

    [Fact]
    public void The_synchronous_read_path_works_too()
    {
        using var r = new ChainedBatchReader(Batches([Row(1)], [Row(2)]), Schema, EngineId.SqlServer, TestContext.Current.CancellationToken);
        Assert.True(r.Read()); Assert.True(r.Read()); Assert.False(r.Read());
        Assert.False(r.Read());
    }

    [Fact]
    public async Task An_empty_stream_reads_nothing_and_a_row_is_required_before_values()
    {
        await using var r = new ChainedBatchReader(Batches(), Schema, EngineId.SqlServer, TestContext.Current.CancellationToken);
        Assert.Throws<InvalidOperationException>(() => r.GetValue(0));
        Assert.False(await r.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, r.RowsRead);
    }

    [Fact]
    public async Task A_batch_with_a_different_schema_is_refused()
    {
        var other = new TransferSchema([new("x", CanonicalType.String())]);
        async IAsyncEnumerable<DataBatch> Wrong() { yield return new RowDataBatch(other, [["a"]]); await Task.CompletedTask; }
        await using var r = new ChainedBatchReader(Wrong(), Schema, EngineId.SqlServer, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<TransferException>(() => r.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Only_one_batch_is_pulled_at_a_time()
    {
        var pulled = 0;
        async IAsyncEnumerable<DataBatch> Counting()
        {
            for (var i = 0; i < 5; i++) { pulled++; yield return new RowDataBatch(Schema, [Row(i)]); }
            await Task.CompletedTask;
        }
        await using var r = new ChainedBatchReader(Counting(), Schema, EngineId.SqlServer, TestContext.Current.CancellationToken);
        Assert.True(await r.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, pulled);
        Assert.True(await r.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, pulled);
    }

    [Fact]
    public void Value_adaptation_by_engine()
    {
        Assert.Equal((short)5, ParameterValues.Adapt(EngineId.Postgres, (sbyte)5));
        Assert.Equal(new DateOnly(2024, 1, 2), ParameterValues.Adapt(EngineId.Postgres, new DateOnly(2024, 1, 2)));
        Assert.Equal(new DateTime(2024, 1, 2), ParameterValues.Adapt(EngineId.SqlServer, new DateOnly(2024, 1, 2)));
        Assert.Equal((sbyte)5, ParameterValues.Adapt(EngineId.DuckDb, (sbyte)5));
        Assert.Null(ParameterValues.Adapt(EngineId.SqlServer, DBNull.Value));
    }
}

/// <summary>Live tests: skipped unless DBDATA_TEST_SQLSERVER holds a connection string to a scratch database.</summary>
public class SqlServerLiveTests
{
    private static string? ConnectionString => Environment.GetEnvironmentVariable("DBDATA_TEST_SQLSERVER");

    private static async Task<ConformanceEngine> EngineAsync(CancellationToken ct)
    {
        await using var c = new SqlConnection(ConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT CONVERT(int, SERVERPROPERTY('ProductMajorVersion'))";
        var major = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        return new ConformanceEngine("sqlserver", DialectCatalog.For(EngineId.SqlServer), new EngineVersion(major), async token =>
        {
            var conn = new SqlConnection(ConnectionString);
            await conn.OpenAsync(token);
            return conn;
        });
    }

    public static IEnumerable<TheoryDataRow<string>> Names() =>
        StrategyCases.All.Select(c => new TheoryDataRow<string>(c.Name));

    [Theory]
    [MemberData(nameof(Names))]
    public async Task Strategy_conformance_on_sql_server(string name)
    {
        Assert.SkipUnless(ConnectionString is not null, "set DBDATA_TEST_SQLSERVER to run");
        var engine = await EngineAsync(TestContext.Current.CancellationToken);
        var c = StrategyCases.All.Single(x => x.Name == name);
        var result = await ConformanceRunner.RunAsync(engine, c, TestContext.Current.CancellationToken);
        Assert.True(result.Passed, string.Join("\n", result.Differences));
    }

    [Fact]
    public async Task Bulk_copy_streams_many_batches_into_a_table()
    {
        Assert.SkipUnless(ConnectionString is not null, "set DBDATA_TEST_SQLSERVER to run");
        var ct = TestContext.Current.CancellationToken;
        var d = DialectCatalog.For(EngineId.SqlServer);
        var schema = new TransferSchema([new("id", CanonicalType.Of(CanonicalKind.Int32)), new("name", CanonicalType.String(20)), new("day", CanonicalType.Of(CanonicalKind.Date)), new("tiny", new CanonicalType(CanonicalKind.Int8, Unsigned: true))]);
        var table = new TableName("dbo", "ddk_bulk_" + Guid.NewGuid().ToString("N")[..8]);
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync(ct);
        var gate = new WriteGate(new AdoWriteExecutor(conn, d), new WriteGateOptions("live", new MemoryStatementLog()));
        await gate.ExecuteAsync(new SqlStatement("c", $"CREATE TABLE {d.QuoteQualified(table.Parts)} ([id] int, [name] nvarchar(20), [day] date, [tiny] tinyint)", [], new WriteIntent("c", WriteKind.Ddl, table.ToString())), ct);
        try
        {
            async IAsyncEnumerable<DataBatch> Rows()
            {
                for (var b = 0; b < 10; b++)
                    yield return new RowDataBatch(schema, [.. Enumerable.Range(b * 1000, 1000).Select(i => new object?[] { i, i % 3 == 0 ? null : "n" + i, new DateOnly(2024, 1, 1 + i % 28), (byte)(i % 200) })]);
                await Task.CompletedTask;
            }
            var n = await gate.BulkWriteAsync(new WriteIntent("bulk", WriteKind.Insert, table.ToString()), new SqlServerBulkWriter(conn), new WriteTarget(table, schema), Rows(), new BulkOptions { BatchSize = 3000 }, ct);
            Assert.Equal(10_000, n);
            var count = await gate.ScalarAsync(new SqlStatement("n", $"SELECT COUNT(*) FROM {d.QuoteQualified(table.Parts)}", [], new WriteIntent("n", WriteKind.None, table.ToString()), StatementRole.Scalar), ct);
            Assert.Equal(10_000, Convert.ToInt32(count));
        }
        finally
        {
            await gate.ExecuteAsync(new SqlStatement("d", $"DROP TABLE IF EXISTS {d.QuoteQualified(table.Parts)}", [], new WriteIntent("d", WriteKind.Ddl, table.ToString())), CancellationToken.None);
        }
    }
}
