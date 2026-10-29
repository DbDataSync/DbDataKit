using System.Runtime.CompilerServices;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.TestSupport;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DbDataSync.Drivers.MsSql.Tests;

/// <summary>
/// What SQL Server staging does with every loose value a source reader can return, into every column type the target can have: the stored value, or the
/// refusal. The expected file is the table in <c>DbDataSync/architecture/staging-value-types.md</c>; a change in it is a change in what Sync writes,
/// so it fails here until the file is regenerated (<c>DBDATASYNC_MATRIX_UPDATE=1</c>) and the difference is read in review.
/// </summary>
[Trait("Category", "Integration")]
public sealed class StagingValueMatrixTests(MsSqlTestDatabase db) : IClassFixture<MsSqlTestDatabase>
{
    private static readonly string[] TargetTypes =
    [
        "bit", "tinyint", "smallint", "int", "bigint", "decimal(18,2)", "real", "float", "money", "nvarchar(50)", "nvarchar(5)", "varchar(50)",
        "varbinary(50)", "uniqueidentifier", "date", "time(7)", "datetime", "datetime2(7)", "datetimeoffset(7)", "xml",
    ];

    // A time-only string stored in a date-and-time column takes the server's current date, so the file names it instead of the day it was written.
    private static readonly string Today = DateTime.UtcNow.ToString("yyyy-MM-dd");

    private static string ExpectedPath([CallerFilePath] string here = "") => Path.Combine(Path.GetDirectoryName(here)!, "StagingMatrix.mssql.expected.tsv");

    [Fact]
    public async Task EveryValueIntoEveryColumnType_StoresOrRefusesAsTheTableSays()
    {
        var staging = new MsSqlStagingTableProvider();
        await using var connection = db.OpenConnection();
        var actual = new List<string>();
        var n = 0;
        foreach (var type in TargetTypes)
        {
            var table = $"M{n++}_{Guid.NewGuid():N}";
            await Exec(connection, $"CREATE TABLE dbo.[{table}] ({StagingMatrix.Column} {type} NULL);");
            var target = new TableRef { ConnectionName = "tgt", Database = db.DatabaseName, Schema = "dbo", Table = table };
            var schema = new ChangeSchema([StagingMatrix.Column]);
            foreach (var value in StagingMatrix.Values)
            {
                string outcome;
                try
                {
                    async IAsyncEnumerable<ChangeRow> One() { await Task.Yield(); yield return new ChangeRow(ChangeOperation.Insert, schema, [value.Fresh()]); }
                    var staged = await staging.StageAsync(connection, target, One(), [new ColumnMapping { SourceColumn = StagingMatrix.Column, TargetColumn = StagingMatrix.Column }],
                        "matrix", [], new Dictionary<string, string>(), CancellationToken.None);
                    try
                    {
                        await using var cmd = connection.CreateCommand();
                        cmd.CommandText = $"SELECT {StagingMatrix.Column} FROM {staged.StagingLocation};";
                        outcome = "STORED " + StagingMatrix.Describe(await cmd.ExecuteScalarAsync());
                    }
                    finally { await staging.CleanupAsync(connection, staged, CancellationToken.None); }
                }
                catch (Exception ex) { outcome = StagingMatrix.Refused(ex, value.Instance.GetType().Name); }
                actual.Add($"{type}\t{value.Name}\t{outcome.Replace(Today, "<today>")}");
            }
        }

        var file = new StagingMatrixFile(ExpectedPath());
        if (StagingMatrixFile.Update) { file.Write(actual); return; }
        var expected = file.Read();
        Assert.True(expected.SequenceEqual(actual), "staging outcomes changed (DBDATASYNC_MATRIX_UPDATE=1 regenerates the file):" + Environment.NewLine + StagingMatrixFile.Diff(expected, actual));
    }

    private static async Task Exec(SqlConnection c, string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
