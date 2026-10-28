using System.Runtime.CompilerServices;
using DbDataSync.TestSupport;
using DuckDB.NET.Data;
using Xunit;

namespace DbDataSync.Drivers.DuckDb.Tests;

/// <summary>What the DuckDB source reader (<c>DuckDBDataReader.GetValue</c>, in memory) returns per column type.</summary>
public sealed class ReaderTypesTests
{
    private static readonly ReaderTypeProbe.Case[] Cases =
    [
        new("BOOLEAN", "true"), new("TINYINT", "-5"), new("SMALLINT", "-300"), new("INTEGER", "70000"), new("BIGINT", "5000000000"),
        new("HUGEINT", "170141183460469231731687303715884105727"), new("UTINYINT", "200"), new("USMALLINT", "60000"), new("UINTEGER", "4000000000"),
        new("UBIGINT", "18446744073709551615"), new("UHUGEINT", "340282366920938463463374607431768211455"),
        new("FLOAT", "1.5"), new("DOUBLE", "1.5"), new("DECIMAL(4,1)", "123.4"), new("DECIMAL(18,2)", "12345.67"), new("DECIMAL(38,10)", "123456789012345678.1234567890"),
        new("VARCHAR", "'text'"), new("BLOB", "'\\x01\\x02\\x03'::BLOB"), new("UUID", "'6f9619ff-8b86-d011-b42d-00c04fc964ff'"), new("BIT", "'101'::BIT"),
        new("DATE", "'2024-03-17'"), new("TIME", "'09:41:22.123456'"), new("TIMETZ", "'09:41:22.123456+02'"),
        new("TIMESTAMP", "'2024-03-17 09:41:22.123456'"), new("TIMESTAMP_S", "'2024-03-17 09:41:22'"), new("TIMESTAMP_MS", "'2024-03-17 09:41:22.123'"),
        new("TIMESTAMP_NS", "'2024-03-17 09:41:22.123456789'"), new("TIMESTAMPTZ", "'2024-03-17 09:41:22.123456+02'"),
        new("INTERVAL", "INTERVAL 1 DAY + INTERVAL 2 HOUR"), new("INTERVAL", "INTERVAL 1 MONTH"),
        new("ENUM('a','b')", "'a'"), new("INTEGER[]", "[1,2,3]"), new("STRUCT(a INTEGER)", "{'a': 1}"), new("MAP(VARCHAR, INTEGER)", "MAP {'a': 1}"),
    ];

    private static string ExpectedPath([CallerFilePath] string here = "") => Path.Combine(Path.GetDirectoryName(here)!, "ReaderTypes.duckdb.expected.tsv");

    [Fact]
    public async Task EveryColumnTypeReadsBackAsTheTableSays()
    {
        using var connection = new DuckDBConnection("Data Source=:memory:");
        connection.Open();
        var actual = new List<string>();
        var n = 0;
        foreach (var c in Cases)
        {
            var table = $"r{n++}";
            var label = c.NativeType + (c.Literal.Contains("MONTH") ? " months" : "");
            try
            {
                Exec(connection, $"CREATE TABLE {table} (v {c.NativeType});");
                Exec(connection, $"INSERT INTO {table} VALUES ({c.Literal});");
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = $"SELECT v FROM {table};";
                actual.Add(await ReaderTypeProbe.ReadAsync(cmd, label));
            }
            catch (Exception ex) { actual.Add($"{label}\t-\t-\tSETUP THROWS {ex.GetType().Name}"); }
        }

        var file = new StagingMatrixFile(ExpectedPath());
        if (StagingMatrixFile.Update) { file.Write(actual); return; }
        var expected = file.Read();
        Assert.True(expected.SequenceEqual(actual), "reader types changed (DBDATASYNC_MATRIX_UPDATE=1 regenerates the file):" + Environment.NewLine + StagingMatrixFile.Diff(expected, actual));
    }

    private static void Exec(DuckDBConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
