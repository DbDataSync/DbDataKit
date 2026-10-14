using Apache.Arrow;
using Apache.Arrow.Types;
using DbDataProxy.Data;
using DuckDB.NET.Data;

namespace DbDataProxy.Tests.Data;

public class TypeMappingTests
{
    private static DbQueryExecutor DuckDb() =>
        new(new DelegateConnectionFactory(cs => new DuckDBConnection(cs)), "Data Source=:memory:");

    private static async Task<(Schema, List<RecordBatch>)> RunAsync(string sql, int batchSize = 1000)
    {
        await using var result = await DuckDb().ExecuteQueryAsync(new QueryRequest(sql, batchSize), CancellationToken.None);
        var batches = new List<RecordBatch>();
        await foreach (var b in result.ReadBatchesAsync(CancellationToken.None)) batches.Add(b);
        return (result.Schema, batches);
    }

    [Fact]
    public async Task Maps_common_scalar_types()
    {
        var (schema, batches) = await RunAsync(@"
            SELECT 42::INTEGER AS i, 7::BIGINT AS l, 1.5::DOUBLE AS d, true AS b, 'héllo' AS s,
                   12345.67::DECIMAL(10,2) AS dec, DATE '2020-01-02' AS dt,
                   TIMESTAMP '2020-01-02 03:04:05.123456' AS ts");
        var batch = Assert.Single(batches);
        Assert.Equal(1, batch.Length);

        Assert.IsType<Int32Type>(schema.GetFieldByName("i").DataType);
        Assert.Equal(42, ((Int32Array)batch.Column("i")).GetValue(0));
        Assert.Equal(7L, ((Int64Array)batch.Column("l")).GetValue(0));
        Assert.Equal(1.5, ((DoubleArray)batch.Column("d")).GetValue(0));
        Assert.True(((BooleanArray)batch.Column("b")).GetValue(0));
        Assert.Equal("héllo", ((StringArray)batch.Column("s")).GetString(0));

        var decType = Assert.IsType<Decimal128Type>(schema.GetFieldByName("dec").DataType);
        Assert.Equal((10, 2), (decType.Precision, decType.Scale));
        Assert.Equal(12345.67m, ((Decimal128Array)batch.Column("dec")).GetValue(0));

        Assert.IsType<Date32Type>(schema.GetFieldByName("dt").DataType);
        Assert.Equal(new DateTimeOffset(2020, 1, 2, 0, 0, 0, TimeSpan.Zero), ((Date32Array)batch.Column("dt")).GetDateTimeOffset(0));

        Assert.IsType<TimestampType>(schema.GetFieldByName("ts").DataType);
        var ts = ((TimestampArray)batch.Column("ts")).GetTimestamp(0)!.Value;
        Assert.Equal(new DateTime(2020, 1, 2, 3, 4, 5).AddTicks(1234560), ts.UtcDateTime);
    }

    [Fact]
    public async Task Preserves_nulls()
    {
        var (_, batches) = await RunAsync("SELECT NULL::INTEGER AS i, NULL::VARCHAR AS s, NULL::DECIMAL(5,1) AS d");
        var batch = Assert.Single(batches);
        Assert.True(batch.Column("i").IsNull(0));
        Assert.True(batch.Column("s").IsNull(0));
        Assert.True(batch.Column("d").IsNull(0));
    }

    [Fact]
    public async Task Splits_results_into_batches_of_requested_size()
    {
        var (_, batches) = await RunAsync("SELECT i FROM range(0, 25) t(i)", batchSize: 10);
        Assert.Equal([10, 10, 5], batches.Select(b => b.Length).ToArray());
        var all = batches.SelectMany(b => Enumerable.Range(0, b.Length).Select(r => ((Int64Array)b.Column(0)).GetValue(r)!.Value));
        Assert.Equal(Enumerable.Range(0, 25).Select(i => (long)i), all);
    }

    [Fact]
    public async Task Empty_result_has_schema_but_no_batches()
    {
        var (schema, batches) = await RunAsync("SELECT 1 AS i WHERE false");
        Assert.Single(schema.FieldsList);
        Assert.Empty(batches);
    }
}
