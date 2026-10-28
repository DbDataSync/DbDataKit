using System.Runtime.CompilerServices;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.TestSupport;
using Npgsql;
using Xunit;

namespace DbDataSync.Drivers.Postgres.Tests;

/// <summary>
/// What binary COPY staging does with every loose value a source reader can return, into every column type the target can have: the stored value, or the
/// refusal. The expected file is the table in <c>DbDataSync/architecture/staging-value-types.md</c>; a change in it is a change in what Sync writes,
/// so it fails here until the file is regenerated (<c>DBDATASYNC_MATRIX_UPDATE=1</c>) and the difference is read in review.
/// </summary>
[Trait("Category", "Integration")]
public sealed class StagingValueMatrixTests(PostgresTestDatabase db) : IClassFixture<PostgresTestDatabase>
{
    private static readonly string[] TargetTypes =
    [
        "boolean", "smallint", "integer", "bigint", "numeric(18,2)", "real", "double precision", "text", "character varying(5)", "bytea", "uuid",
        "date", "time", "timestamp", "timestamptz", "interval", "json", "jsonb",
    ];

    private static string ExpectedPath([CallerFilePath] string here = "") => Path.Combine(Path.GetDirectoryName(here)!, "StagingMatrix.postgres.expected.tsv");

    [Fact]
    public async Task EveryValueIntoEveryColumnType_StoresOrRefusesAsTheTableSays()
    {
        var staging = new PostgresDriver().StagingProviders.Single(p => p.Kind == PostgresDriverKinds.CopyStaging);
        var actual = new List<string>();
        var n = 0;
        foreach (var type in TargetTypes)
        {
            var table = $"m{n++}_{Guid.NewGuid():N}";
            await using (var ddl = db.OpenConnection()) await Exec(ddl, $"CREATE TABLE public.\"{table}\" ({StagingMatrix.Column} {type} NULL);");
            var target = new TableRef { ConnectionName = "tgt", Database = db.DatabaseName, Schema = "public", Table = table };
            var schema = new ChangeSchema([StagingMatrix.Column]);
            foreach (var value in StagingMatrix.Values)
            {
                string outcome;
                // A refused COPY leaves the connection unusable, so every value gets its own.
                await using var connection = db.OpenConnection();
                try
                {
                    async IAsyncEnumerable<ChangeRow> One() { await Task.Yield(); yield return new ChangeRow(ChangeOperation.Insert, schema, [value.Fresh()]); }
                    var staged = await staging.StageAsync(connection, target, One(), [new ColumnMapping { SourceColumn = StagingMatrix.Column, TargetColumn = StagingMatrix.Column }],
                        "matrix", [new CachedColumn(StagingMatrix.Column, type, true, false, false)], new Dictionary<string, string>(), CancellationToken.None);
                    try
                    {
                        await using var cmd = connection.CreateCommand();
                        cmd.CommandText = $"SELECT {StagingMatrix.Column} FROM {staged.StagingLocation};";
                        outcome = "STORED " + StagingMatrix.Describe(await cmd.ExecuteScalarAsync());
                    }
                    finally { await staging.CleanupAsync(connection, staged, CancellationToken.None); }
                }
                catch (Exception ex) { outcome = StagingMatrix.Refused(ex, value.Instance.GetType().Name); }
                actual.Add($"{type}\t{value.Name}\t{outcome}");
            }
        }

        var file = new StagingMatrixFile(ExpectedPath());
        if (StagingMatrixFile.Update) { file.Write(actual); return; }
        var expected = file.Read();
        Assert.True(expected.SequenceEqual(actual), "staging outcomes changed (DBDATASYNC_MATRIX_UPDATE=1 regenerates the file):" + Environment.NewLine + StagingMatrixFile.Diff(expected, actual));
    }

    private static async Task Exec(NpgsqlConnection c, string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
