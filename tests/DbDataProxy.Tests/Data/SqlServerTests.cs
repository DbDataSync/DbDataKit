using Apache.Arrow;
using Apache.Arrow.Types;
using DbDataProxy.Data;
using DbDataProxy.Server.Data;
using DbDataProxy.Tests.Support;
using Microsoft.Data.SqlClient;

namespace DbDataProxy.Tests.Data;

/// <summary>
/// Runs against a local SQL Server / SQL Express instance (Windows authentication) or the server named by the
/// DBPROXY_TEST_SQLSERVER* variables (see <see cref="SqlServerEnv"/>) when one is reachable;
/// otherwise the tests are skipped.
/// </summary>
public class SqlServerTests
{
    private const string AllTypes = """
        SELECT CAST(1 AS tinyint) AS c_tinyint, CAST(-2 AS smallint) AS c_smallint, CAST(3 AS int) AS c_int,
               CAST(4 AS bigint) AS c_bigint, CAST(1 AS bit) AS c_bit, CAST(12345.6789 AS decimal(18,4)) AS c_decimal,
               CAST(2.5 AS float) AS c_float, CAST(N'héllo wörld' AS nvarchar(50)) AS c_nvarchar, CAST('abc' AS varchar(10)) AS c_varchar,
               CAST('2024-03-05' AS date) AS c_date, CAST('2024-03-05 06:07:08.1234567' AS datetime2(7)) AS c_datetime2,
               CAST('2024-03-05 06:07:08 +02:00' AS datetimeoffset) AS c_dto, CAST('01:02:03' AS time(0)) AS c_time,
               CAST('6F9619FF-8B86-D011-B42D-00C04FC964FF' AS uniqueidentifier) AS c_guid, CAST(0x01FF AS varbinary(10)) AS c_binary,
               CAST(NULL AS int) AS c_null
        """;

    private static async Task<DbQueryExecutor> SqlClientAsync()
    {
        await SqlServerEnv.EnsureReachableAsync();
        return new DbQueryExecutor(new DelegateConnectionFactory(c => new SqlConnection(c)), SqlServerEnv.SqlClient(), ParameterStyle.NamedAtSign);
    }

    private static DbQueryExecutor Odbc(string driver) =>
        new(new DelegateConnectionFactory(c => new System.Data.Odbc.OdbcConnection(c)), SqlServerEnv.Odbc(driver));

    private static async Task<(Schema, RecordBatch)> RunAsync(DbQueryExecutor executor, string sql)
    {
        await using var result = await executor.ExecuteQueryAsync(new QueryRequest(sql), CancellationToken.None);
        var batches = new List<RecordBatch>();
        await foreach (var b in result.ReadBatchesAsync(CancellationToken.None)) batches.Add(b);
        return (result.Schema, Assert.Single(batches));
    }

    [Fact]
    public async Task SqlClient_maps_all_common_sql_server_types()
    {
        var (schema, batch) = await RunAsync(await SqlClientAsync(), AllTypes);

        Assert.Equal((byte)1, ((UInt8Array)batch.Column("c_tinyint")).GetValue(0));
        Assert.Equal((short)-2, ((Int16Array)batch.Column("c_smallint")).GetValue(0));
        Assert.Equal(3, ((Int32Array)batch.Column("c_int")).GetValue(0));
        Assert.Equal(4L, ((Int64Array)batch.Column("c_bigint")).GetValue(0));
        Assert.True(((BooleanArray)batch.Column("c_bit")).GetValue(0));

        var dec = Assert.IsType<Decimal128Type>(schema.GetFieldByName("c_decimal").DataType);
        Assert.Equal((18, 4), (dec.Precision, dec.Scale));
        Assert.Equal(12345.6789m, ((Decimal128Array)batch.Column("c_decimal")).GetValue(0));

        Assert.Equal(2.5, ((DoubleArray)batch.Column("c_float")).GetValue(0));
        Assert.Equal("héllo wörld", ((StringArray)batch.Column("c_nvarchar")).GetString(0));
        Assert.Equal("abc", ((StringArray)batch.Column("c_varchar")).GetString(0));

        Assert.IsType<Date32Type>(schema.GetFieldByName("c_date").DataType);
        Assert.Equal(new DateTimeOffset(2024, 3, 5, 0, 0, 0, TimeSpan.Zero), ((Date32Array)batch.Column("c_date")).GetDateTimeOffset(0));

        var ts = ((TimestampArray)batch.Column("c_datetime2")).GetTimestamp(0)!.Value;
        Assert.Equal(new DateTime(2024, 3, 5, 6, 7, 8).AddTicks(1234560), ts.UtcDateTime); // microsecond precision

        var dto = ((TimestampArray)batch.Column("c_dto")).GetTimestamp(0)!.Value;
        Assert.Equal(new DateTime(2024, 3, 5, 4, 7, 8, DateTimeKind.Utc), dto.UtcDateTime);

        Assert.Equal(TimeSpan.FromSeconds(3723).Ticks / 10, ((Time64Array)batch.Column("c_time")).GetValue(0));
        Assert.Equal("6f9619ff-8b86-d011-b42d-00c04fc964ff", ((StringArray)batch.Column("c_guid")).GetString(0), ignoreCase: true);
        Assert.Equal(new byte[] { 0x01, 0xFF }, ((BinaryArray)batch.Column("c_binary")).GetBytes(0).ToArray());
        Assert.True(batch.Column("c_null").IsNull(0));
    }

    [Theory]
    [InlineData("ODBC Driver 17 for SQL Server")]
    [InlineData("SQL Server")]
    [InlineData("ODBC Driver 18 for SQL Server")]
    public async Task Odbc_maps_the_same_types_for_the_same_query(string driver)
    {
        await SqlClientAsync(); // skips when no server is available
        var (_, batch) = await RunOdbcOrSkipAsync(Odbc(driver), AllTypes.Replace("CAST('2024-03-05 06:07:08 +02:00' AS datetimeoffset)", "CAST(NULL AS int)").Replace("CAST('01:02:03' AS time(0))", "CAST(NULL AS int)"));

        Assert.Equal(3, Convert.ToInt32(GetValue(batch, "c_int")));
        Assert.Equal(4L, Convert.ToInt64(GetValue(batch, "c_bigint")));
        Assert.Equal("héllo wörld", ((StringArray)batch.Column("c_nvarchar")).GetString(0));
        Assert.Equal(12345.6789m, ((Decimal128Array)batch.Column("c_decimal")).GetValue(0));
        Assert.True(batch.Column("c_null").IsNull(0));
    }

    /// <summary>Runs through an ODBC driver, skipping the test when this machine does not have that driver installed.</summary>
    private static async Task<(Schema, RecordBatch)> RunOdbcOrSkipAsync(DbQueryExecutor executor, string sql)
    {
        try
        {
            return await RunAsync(executor, sql);
        }
        catch (System.Data.Odbc.OdbcException ex) when (IsMissingDriver(ex))
        {
            Assert.Skip("The ODBC driver is not installed: " + ex.Message);
            throw;
        }
    }

    // Windows reports a missing driver as IM002; unixODBC reports "Can't open lib '<driver>' : file not found".
    internal static bool IsMissingDriver(System.Data.Odbc.OdbcException ex) =>
        ex.Errors.Cast<System.Data.Odbc.OdbcError>().Any(e => e.SQLState == "IM002")
        || ex.Message.Contains("Can't open lib", StringComparison.Ordinal);

    [Fact]
    public async Task Named_parameters_bind_for_question_mark_and_at_sign_statements()
    {
        var executor = await SqlClientAsync();

        var (_, positional) = await RunWithAsync(executor, "SELECT CAST(? AS int) + 1 AS n, CAST(? AS nvarchar(20)) AS s, CAST(? AS date) AS d, CAST(? AS int) AS missing",
            [new("p0", "int64", "41"), new("p1", "string", "it's"), new("p2", "date", new DateTime(2024, 3, 5).ToString("o")), new("p3", "null", null)]);
        Assert.Equal(42, ((Int32Array)positional.Column("n")).GetValue(0));
        Assert.Equal("it's", ((StringArray)positional.Column("s")).GetString(0));
        Assert.Equal(new DateTimeOffset(2024, 3, 5, 0, 0, 0, TimeSpan.Zero), ((Date32Array)positional.Column("d")).GetDateTimeOffset(0));
        Assert.True(positional.Column("missing").IsNull(0));

        var (_, named) = await RunWithAsync(executor, "SELECT @a + @b AS total", [new("a", "int64", "40"), new("@b", "int64", "2")]);
        Assert.Equal(42L, Convert.ToInt64(GetValue(named, "total")));
    }

    private static async Task<(Schema, RecordBatch)> RunWithAsync(DbQueryExecutor executor, string sql, QueryParameter[] parameters)
    {
        await using var result = await executor.ExecuteQueryAsync(new QueryRequest(sql, Parameters: parameters), CancellationToken.None);
        var batches = new List<RecordBatch>();
        await foreach (var b in result.ReadBatchesAsync(CancellationToken.None)) batches.Add(b);
        return (result.Schema, Assert.Single(batches));
    }

    private static object? GetValue(RecordBatch batch, string column) => batch.Column(column) switch
    {
        Int32Array a => a.GetValue(0),
        Int64Array a => a.GetValue(0),
        _ => throw new NotSupportedException(),
    };

    [Fact]
    public async Task Large_result_streams_in_bounded_batches()
    {
        var executor = await SqlClientAsync();
        await using var result = await executor.ExecuteQueryAsync(
            new QueryRequest("SELECT TOP 50000 a.object_id AS id FROM sys.all_objects a CROSS JOIN sys.all_objects b", BatchSize: 4096),
            CancellationToken.None);

        long rows = 0;
        int maxBatch = 0;
        await foreach (var batch in result.ReadBatchesAsync(CancellationToken.None))
        {
            rows += batch.Length;
            maxBatch = Math.Max(maxBatch, batch.Length);
        }
        Assert.Equal(50000, rows);
        Assert.Equal(4096, maxBatch);
    }

    [Fact]
    public void Credentials_are_injected_into_provider_connection_strings_with_proper_quoting()
    {
        var sql = new SqlServerProvider().ApplyCredentials("Server=x;Integrated Security=True", "bob", "p;ass=word'\"");
        var builder = new SqlConnectionStringBuilder(sql);
        Assert.False(builder.IntegratedSecurity);
        Assert.Equal("bob", builder.UserID);
        Assert.Equal("p;ass=word'\"", builder.Password);

        var odbc = new OdbcProvider().ApplyCredentials("Driver={SQL Server};Server=x;Trusted_Connection=Yes;UID=old", "bob", "p};ass");
        var parsed = new System.Data.Odbc.OdbcConnectionStringBuilder(odbc);
        Assert.Equal("bob", parsed["UID"]);
        Assert.Contains("PWD={p}};ass}", odbc); // braces are escaped so the value cannot break out of the connection string
        Assert.False(parsed.ContainsKey("Trusted_Connection"));
    }
}

public class OdbcUnsupportedTypeTests
{
    [Fact]
    public async Task Unreadable_odbc_types_fail_up_front_with_advice_to_cast()
    {
        await SqlServerEnv.EnsureReachableAsync();
        var executor = new DbQueryExecutor(
            new DelegateConnectionFactory(c => new System.Data.Odbc.OdbcConnection(c)),
            SqlServerEnv.Odbc(OperatingSystem.IsWindows() ? "ODBC Driver 17 for SQL Server" : "ODBC Driver 18 for SQL Server"));

        NotSupportedException? ex = null;
        try
        {
            await using var _ = await executor.ExecuteQueryAsync(
                new QueryRequest("SELECT SYSDATETIMEOFFSET() AS stamp"), CancellationToken.None);
        }
        catch (NotSupportedException e) { ex = e; }
        catch (System.Data.Odbc.OdbcException odbc) when (SqlServerTests.IsMissingDriver(odbc)) { Assert.Skip("The ODBC driver is not installed."); }

        Assert.NotNull(ex);
        Assert.Contains("'stamp'", ex.Message);
        Assert.Contains("CAST", ex.Message);
    }
}
