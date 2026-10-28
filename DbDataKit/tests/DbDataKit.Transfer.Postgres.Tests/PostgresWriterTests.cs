using DbDataKit.Dialect;
using DbDataKit.Transfer.Conformance;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace DbDataKit.Transfer.Postgres.Tests;

public class PostgresTypesTests
{
    public static TheoryData<string, NpgsqlDbType> Types => new()
    {
        { "boolean", NpgsqlDbType.Boolean },
        { "int8", NpgsqlDbType.Smallint },
        { "int8(unsigned)", NpgsqlDbType.Smallint },
        { "int16", NpgsqlDbType.Smallint },
        { "int16(unsigned)", NpgsqlDbType.Integer },
        { "int32", NpgsqlDbType.Integer },
        { "int32(unsigned)", NpgsqlDbType.Bigint },
        { "int64", NpgsqlDbType.Bigint },
        { "int64(unsigned)", NpgsqlDbType.Numeric },
        { "decimal(18,2)", NpgsqlDbType.Numeric },
        { "float", NpgsqlDbType.Real },
        { "double", NpgsqlDbType.Double },
        { "string(20)", NpgsqlDbType.Text },
        { "binary", NpgsqlDbType.Bytea },
        { "date", NpgsqlDbType.Date },
        { "time(6)", NpgsqlDbType.Time },
        { "timestamp(6)", NpgsqlDbType.Timestamp },
        { "timestamp(tz,6)", NpgsqlDbType.TimestampTz },
        { "guid", NpgsqlDbType.Uuid },
        { "json", NpgsqlDbType.Jsonb },
        { "xml", NpgsqlDbType.Xml },
        { "interval", NpgsqlDbType.Interval },
    };

    [Theory]
    [MemberData(nameof(Types))]
    public void Canonical_type_to_copy_type(string canonical, NpgsqlDbType expected) =>
        Assert.Equal(expected, PostgresTypes.For(CanonicalType.Parse(canonical)));

    [Fact]
    public void Json_columns_of_type_json_can_be_selected() =>
        Assert.Equal(NpgsqlDbType.Json, PostgresTypes.For(CanonicalType.Of(CanonicalKind.Json), jsonColumnIsJson: true));

    [Fact]
    public void Unmappable_types_are_refused() =>
        Assert.Throws<TransferException>(() => PostgresTypes.For(CanonicalType.Unmappable));

    [Fact]
    public void Every_widened_clr_type_has_a_copy_type_that_accepts_it()
    {
        // the value ParameterValues produces must be writable with the type PostgresTypes picks
        (string Canonical, Type Clr)[] expected =
        [("int8", typeof(short)), ("int16(unsigned)", typeof(int)), ("int32(unsigned)", typeof(long)), ("int64(unsigned)", typeof(decimal))];
        foreach (var (c, clr) in expected)
            Assert.Equal(clr, ParameterValues.ClrTypeFor(EngineId.Postgres, CanonicalType.Parse(c)));
    }

    [Fact]
    public void Copy_command_quotes_identifiers_and_lists_columns_in_order()
    {
        var schema = new TransferSchema([new("a\"b", CanonicalType.Of(CanonicalKind.Int32)), new("c", CanonicalType.String())]);
        Assert.Equal("COPY \"s\"\"x\".\"t\" (\"a\"\"b\", \"c\") FROM STDIN (FORMAT BINARY)", PostgresCopy.Command(new WriteTarget(new TableName("s\"x", "t"), schema)));
    }

    [Fact]
    public void Rows_per_copy_follows_the_transaction_mode()
    {
        Assert.Equal(int.MaxValue, PostgresCopy.RowsPerCopy(new BulkOptions()));
        Assert.Equal(int.MaxValue, PostgresCopy.RowsPerCopy(new BulkOptions { Transaction = TransactionMode.Single }));
        Assert.Equal(250, PostgresCopy.RowsPerCopy(new BulkOptions { Transaction = TransactionMode.PerBatch, BatchSize = 250 }));
        Assert.Equal(1, PostgresCopy.RowsPerCopy(new BulkOptions { Transaction = TransactionMode.PerBatch, BatchSize = 0 }));
    }
}

/// <summary>Live tests: skipped unless DBDATA_TEST_POSTGRES holds an Npgsql connection string to a scratch database.</summary>
public class PostgresLiveTests
{
    private static string? ConnectionString => Environment.GetEnvironmentVariable("DBDATA_TEST_POSTGRES");

    private static async Task<ConformanceEngine> EngineAsync(CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(ConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SHOW server_version_num";
        var num = int.Parse((string)(await cmd.ExecuteScalarAsync(ct))!);
        return new ConformanceEngine("postgres", DialectCatalog.For(EngineId.Postgres), new EngineVersion(num / 10000, num % 10000 / 100), async token =>
        {
            var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync(token);
            return conn;
        });
    }

    public static IEnumerable<TheoryDataRow<string>> Names() =>
        StrategyCases.All.Select(c => new TheoryDataRow<string>(c.Name));

    [Theory]
    [MemberData(nameof(Names))]
    public async Task Strategy_conformance_on_postgres(string name)
    {
        Assert.SkipUnless(ConnectionString is not null, "set DBDATA_TEST_POSTGRES to run");
        var engine = await EngineAsync(TestContext.Current.CancellationToken);
        var c = StrategyCases.All.Single(x => x.Name == name);
        var result = await ConformanceRunner.RunAsync(engine, c, TestContext.Current.CancellationToken);
        Assert.True(result.Passed, string.Join("\n", result.Differences));
    }

    [Fact]
    public async Task A_value_binary_copy_cannot_write_fails_naming_the_column_and_never_the_value()
    {
        Assert.SkipUnless(ConnectionString is not null, "set DBDATA_TEST_POSTGRES to run");
        var ct = TestContext.Current.CancellationToken;
        var d = DialectCatalog.For(EngineId.Postgres);
        var schema = new TransferSchema([new("id", CanonicalType.Of(CanonicalKind.Int32)), new("at", CanonicalType.Timestamp(6, tz: true))]);
        var table = new TableName("public", "ddk_copyfail_" + Guid.NewGuid().ToString("N")[..8]);
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using (var create = conn.CreateCommand())
        {
            create.CommandText = $"CREATE TABLE {d.QuoteQualified(table.Parts)} (id integer, at timestamptz)";
            await create.ExecuteNonQueryAsync(ct);
        }
        try
        {
            async IAsyncEnumerable<DataBatch> Rows()
            {
                // Npgsql refuses a DateTime that is not UTC for timestamptz
                yield return new RowDataBatch(schema, [[1, DateTime.SpecifyKind(new DateTime(2026, 1, 1, 9, 0, 0), DateTimeKind.Unspecified)]]);
                await Task.CompletedTask;
            }
            var ex = await Assert.ThrowsAsync<PostgresCopyValueException>(() => new PostgresBulkWriter(conn).WriteAsync(new WriteTarget(table, schema), Rows(), new BulkOptions(), ct));
            Assert.Equal("at", ex.Column);
            Assert.Equal(typeof(DateTime), ex.ClrType);
            Assert.Equal(NpgsqlDbType.TimestampTz, ex.CopyType);
            Assert.DoesNotContain("2026", ex.Message);
        }
        finally
        {
            await using var drop = new NpgsqlConnection(ConnectionString);
            await drop.OpenAsync(ct);
            await using var cmd = drop.CreateCommand();
            cmd.CommandText = $"DROP TABLE IF EXISTS {d.QuoteQualified(table.Parts)}";
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    [Theory]
    [InlineData(TransactionMode.None)]
    [InlineData(TransactionMode.PerBatch)]
    [InlineData(TransactionMode.Single)]
    public async Task Binary_copy_writes_every_row(TransactionMode mode)
    {
        Assert.SkipUnless(ConnectionString is not null, "set DBDATA_TEST_POSTGRES to run");
        var ct = TestContext.Current.CancellationToken;
        var d = DialectCatalog.For(EngineId.Postgres);
        var schema = new TransferSchema([new("id", CanonicalType.Of(CanonicalKind.Int32)), new("name", CanonicalType.String(20)), new("at", CanonicalType.Timestamp(6, tz: true)), new("u", new CanonicalType(CanonicalKind.Int16, Unsigned: true))]);
        var table = new TableName("public", "ddk_copy_" + Guid.NewGuid().ToString("N")[..8]);
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(ct);
        var gate = new WriteGate(new AdoWriteExecutor(conn, d), new WriteGateOptions("live", new MemoryStatementLog()));
        await gate.ExecuteAsync(new SqlStatement("c", $"CREATE TABLE {d.QuoteQualified(table.Parts)} (id integer, name varchar(20), at timestamptz, u integer)", [], new WriteIntent("c", WriteKind.Ddl, table.ToString())), ct);
        try
        {
            async IAsyncEnumerable<DataBatch> Rows()
            {
                for (var b = 0; b < 5; b++)
                    yield return new RowDataBatch(schema, [.. Enumerable.Range(b * 1000, 1000).Select(i => new object?[] { i, i % 3 == 0 ? null : "n" + i, new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(i), (ushort)(i % 60000) })]);
                await Task.CompletedTask;
            }
            var n = await gate.BulkWriteAsync(new WriteIntent("bulk", WriteKind.Insert, table.ToString()), new PostgresBulkWriter(conn), new WriteTarget(table, schema), Rows(), new BulkOptions { Transaction = mode, BatchSize = 1500 }, ct);
            Assert.Equal(5000, n);
            var count = await gate.ScalarAsync(new SqlStatement("n", $"SELECT COUNT(*) FROM {d.QuoteQualified(table.Parts)}", [], new WriteIntent("n", WriteKind.None, table.ToString()), StatementRole.Scalar), ct);
            Assert.Equal(5000L, Convert.ToInt64(count));
        }
        finally
        {
            await gate.ExecuteAsync(new SqlStatement("d", $"DROP TABLE IF EXISTS {d.QuoteQualified(table.Parts)}", [], new WriteIntent("d", WriteKind.Ddl, table.ToString())), CancellationToken.None);
        }
    }
}
