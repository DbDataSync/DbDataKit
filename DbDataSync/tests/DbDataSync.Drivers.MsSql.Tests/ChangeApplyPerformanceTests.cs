using System.Diagnostics;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using Microsoft.Data.SqlClient;
using Xunit.Abstractions;

namespace DbDataSync.Drivers.MsSql.Tests;

/// <summary>
/// A sanity measurement, not a benchmark (plan 28c, CW01c): 100,000 changed rows (60,000 updates, 20,000 deletes, 20,000 inserts) against a 100,000-row target, applied by
/// <c>MsSqlMerge</c> and by <c>ChangeApply</c> on the same SQL Server, staged the same way (SqlBulkCopy into a temp table). The numbers go to the test output; the assertions are the
/// resulting rows and a loose bound that only catches a writer that has become quadratic. No claim about being faster or slower is made by this file: read the output of the run you have.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "Performance")]
public sealed class ChangeApplyPerformanceTests(MsSqlTestDatabase db, ITestOutputHelper output) : IClassFixture<MsSqlTestDatabase>
{
    private const int Rows = 100_000, Updates = 60_000, Deletes = 20_000, Inserts = 20_000;

    private static readonly MsSqlDriver Driver = new();
    private static readonly List<CachedColumn> Columns =
    [
        new("id", "int", false, true, false),
        new("name", "nvarchar(100)", true, false, false),
        new("amount", "decimal(18,2)", true, false, false),
        new("note", "nvarchar(200)", true, false, false),
    ];

    private static readonly List<ColumnMapping> Mappings = Columns.Select(c => new ColumnMapping { SourceColumn = c.Name, TargetColumn = c.Name }).ToList();

    private static async IAsyncEnumerable<ChangeRow> ChangeSet()
    {
        var schema = new ChangeSchema(Columns.Select(c => c.Name).ToList());
        for (var id = 1; id <= Updates; id++)
            yield return new ChangeRow(ChangeOperation.Update, schema, [id, $"updated-{id}", id * 2m, $"note for {id}, changed"]);
        for (var id = Updates + 1; id <= Updates + Deletes; id++)
            yield return new ChangeRow(ChangeOperation.Delete, schema, [id, null, null, null]);
        for (var id = Rows + 1; id <= Rows + Inserts; id++)
            yield return new ChangeRow(ChangeOperation.Insert, schema, [id, $"inserted-{id}", id * 3m, $"note for {id}"]);
        await Task.CompletedTask;
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 300;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<(double StageMs, double ApplyMs)> RunAsync(string writerKind, string? batchSize)
    {
        await using var connection = db.OpenConnection();
        var table = $"perf_{Guid.NewGuid():N}";
        await ExecuteAsync(connection, $"CREATE TABLE dbo.[{table}] (id INT NOT NULL PRIMARY KEY, name NVARCHAR(100) NULL, amount DECIMAL(18,2) NULL, note NVARCHAR(200) NULL);");
        await ExecuteAsync(connection, $"""
            INSERT INTO dbo.[{table}] (id, name, amount, note)
            SELECT TOP ({Rows}) n, CONCAT(N'old-', n), n, CONCAT(N'note for ', n)
            FROM (SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM sys.all_objects a CROSS JOIN sys.all_objects b CROSS JOIN sys.all_objects c) t;
            """);
        try
        {
            var target = new TableRef { ConnectionName = "t", Database = db.DatabaseName, Schema = "dbo", Table = table };
            var staging = Driver.StagingProviders.Single(p => p.Kind == MsSqlDriverKinds.StagingTable);
            var writer = Driver.Writers.Single(w => w.Kind == writerKind);
            var options = batchSize is null ? new Dictionary<string, string>() : new Dictionary<string, string> { [ApplyBatch.OptionName] = batchSize };

            var watch = Stopwatch.StartNew();
            var staged = await staging.StageAsync(connection, target, ChangeSet(), Mappings, "perf", Columns, options, CancellationToken.None);
            var stageMs = watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            await writer.ApplyAsync(connection, target, staged, Mappings, "perf", Columns, options, CancellationToken.None);
            var applyMs = watch.Elapsed.TotalMilliseconds;
            await staging.CleanupAsync(connection, staged, CancellationToken.None);

            await using var check = connection.CreateCommand();
            check.CommandText = $"SELECT COUNT_BIG(*), SUM(CASE WHEN name LIKE N'updated-%' THEN 1 ELSE 0 END), SUM(CASE WHEN id BETWEEN {Updates + 1} AND {Updates + Deletes} THEN 1 ELSE 0 END), SUM(CASE WHEN name LIKE N'inserted-%' THEN 1 ELSE 0 END) FROM dbo.[{table}];";
            await using var reader = await check.ExecuteReaderAsync();
            await reader.ReadAsync();
            Assert.Equal((long)(Rows - Deletes + Inserts), reader.GetInt64(0));
            Assert.Equal(Updates, reader.GetInt32(1));
            Assert.Equal(0, reader.GetInt32(2));
            Assert.Equal(Inserts, reader.GetInt32(3));
            return (stageMs, applyMs);
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP TABLE dbo.[{table}];");
        }
    }

    [Fact]
    public async Task AHundredThousandChangedRows_AreAppliedByEachWriterToTheSameRows_AndTheTimesAreReported()
    {
        // a first run of each warms the plan cache and the buffer pool; the three after it are what is reported
        var variants = new (string Name, string Kind, string? Batch)[]
        {
            ("MsSqlMerge (applyBatchSize 5000, the default)", MsSqlDriverKinds.Merge, null),
            ("ChangeApply (applyBatchSize 5000, the default)", GenericDriverKinds.ChangeApply, null),
            ("MsSqlMerge (applyBatchSize 0: one statement)", MsSqlDriverKinds.Merge, "0"),
            ("ChangeApply (applyBatchSize 0: one statement)", GenericDriverKinds.ChangeApply, "0"),
        };
        foreach (var (name, kind, batch) in variants)
        {
            await RunAsync(kind, batch);
            var times = new List<double>();
            for (var i = 0; i < 3; i++) times.Add((await RunAsync(kind, batch)).ApplyMs);
            times.Sort();
            output.WriteLine($"{name}: apply of {Updates + Deletes + Inserts:N0} changed rows into {Rows:N0}: median {times[1]:N0} ms (runs {string.Join(", ", times.Select(t => t.ToString("N0")))} ms)");
            Assert.True(times[1] < 120_000, $"{name} took {times[1]:N0} ms for 100,000 changed rows");
        }
    }
}
