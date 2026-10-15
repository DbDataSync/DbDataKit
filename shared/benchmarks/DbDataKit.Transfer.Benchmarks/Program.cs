using System.Data;
using System.Diagnostics;
using DbDataKit.Dialect;
using DbDataKit.Transfer;

// Benchmark stub (Plan 07 phase 0/2): compares the two ways of feeding a bulk route from batches, without a database.
//   reader : ChainedBatchReader over DataBatch readers (what SqlServerBulkWriter does)
//   table  : buffering rows into a DataTable per 20,000 rows (what Build's BulkCopySqlServerAsync did)
// Run: dotnet run -c Release --project shared/benchmarks/DbDataKit.Transfer.Benchmarks [rows]
// A live throughput comparison against a real SqlBulkCopy / COPY target is the missing half (see the Transfer README).

var total = args.Length > 0 ? int.Parse(args[0]) : 1_000_000;
var schema = new TransferSchema(
[
    new("id", CanonicalType.Of(CanonicalKind.Int64)),
    new("name", CanonicalType.String(40)),
    new("amount", CanonicalType.Decimal(18, 2)),
    new("at", CanonicalType.Timestamp(6)),
]);

static async IAsyncEnumerable<DataBatch> Batches(TransferSchema schema, int total, int batchSize)
{
    var at = new DateTime(2024, 1, 1);
    for (var start = 0; start < total; start += batchSize)
    {
        var n = Math.Min(batchSize, total - start);
        var rows = new object?[n][];
        for (var i = 0; i < n; i++) rows[i] = [(long)(start + i), "name-" + (start + i), (decimal)(start + i) / 100, at.AddSeconds(start + i)];
        yield return new RowDataBatch(schema, rows);
        await Task.Yield();
    }
}

static async Task<(TimeSpan Time, long Allocated, long Rows)> Measure(string name, Func<Task<long>> run)
{
    GC.Collect(); GC.WaitForPendingFinalizers();
    var before = GC.GetTotalAllocatedBytes(precise: true);
    var sw = Stopwatch.StartNew();
    var rows = await run();
    sw.Stop();
    var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
    Console.WriteLine($"{name,-8} {rows,10:N0} rows  {sw.Elapsed.TotalMilliseconds,8:N0} ms  {allocated / 1024.0 / 1024.0,8:N1} MB allocated");
    return (sw.Elapsed, allocated, rows);
}

Console.WriteLine($"rows: {total:N0}");
await Measure("reader", async () =>
{
    await using var reader = new ChainedBatchReader(Batches(schema, total, 8192), schema, EngineId.SqlServer);
    long n = 0;
    var values = new object[schema.Count];
    while (await reader.ReadAsync(CancellationToken.None)) { reader.GetValues(values); n++; }
    return n;
});

await Measure("table", async () =>
{
    var table = new DataTable();
    foreach (var c in schema.Columns) table.Columns.Add(c.Name, ParameterValues.ClrTypeFor(EngineId.SqlServer, c.Type));
    long n = 0;
    await foreach (var batch in Batches(schema, total, 8192))
    {
        using var r = batch.AsDataReader();
        var values = new object[schema.Count];
        while (r.Read())
        {
            r.GetValues(values);
            table.Rows.Add(values);
            n++;
            if (table.Rows.Count >= 20_000) table.Clear();
        }
    }
    return n;
});
