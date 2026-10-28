using System.Runtime.CompilerServices;
using DbDataSync.TestSupport;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DbDataSync.Drivers.MsSql.Tests;

/// <summary>What the SQL Server source readers (change tracking, CDC, watermark, trigger audit, reload all use <c>SqlDataReader.GetValue</c>) return per column type.</summary>
[Trait("Category", "Integration")]
public sealed class ReaderTypesTests(MsSqlTestDatabase db) : IClassFixture<MsSqlTestDatabase>
{
    private static readonly ReaderTypeProbe.Case[] Cases =
    [
        new("bit", "1"), new("tinyint", "200"), new("smallint", "-300"), new("int", "70000"), new("bigint", "5000000000"),
        new("decimal(18,2)", "12345.67"), new("decimal(38,10)", "123456789012345678.1234567890"), new("numeric(5,0)", "12345"), new("money", "12.3456"), new("smallmoney", "12.34"),
        new("real", "1.5"), new("float", "1.5"),
        new("char(5)", "'ab'"), new("varchar(50)", "'text'"), new("nchar(5)", "N'ab'"), new("nvarchar(50)", "N'text'"), new("nvarchar(max)", "N'text'"), new("text", "'text'"), new("ntext", "N'text'"),
        new("binary(4)", "0x01020304"), new("varbinary(50)", "0x010203"), new("varbinary(max)", "0x010203"), new("image", "0x010203"),
        new("uniqueidentifier", "'6F9619FF-8B86-D011-B42D-00C04FC964FF'"),
        new("date", "'2024-03-17'"), new("time(7)", "'09:41:22.1234567'"), new("datetime", "'2024-03-17 09:41:22.123'"), new("smalldatetime", "'2024-03-17 09:41:00'"),
        new("datetime2(7)", "'2024-03-17 09:41:22.1234567'"), new("datetimeoffset(7)", "'2024-03-17 09:41:22.1234567 +02:00'"),
        new("xml", "'<a>1</a>'"), new("rowversion", "DEFAULT"), new("sql_variant", "CAST(1 AS int)"), new("sql_variant", "CAST('x' AS varchar(5))"),
        new("hierarchyid", "'/1/'"), new("geography", "geography::Point(47.65, -122.34, 4326)"), new("geometry", "geometry::Point(1, 2, 0)"),
    ];

    private static string ExpectedPath([CallerFilePath] string here = "") => Path.Combine(Path.GetDirectoryName(here)!, "ReaderTypes.mssql.expected.tsv");

    [Fact]
    public async Task EveryColumnTypeReadsBackAsTheTableSays()
    {
        await using var connection = db.OpenConnection();
        var actual = new List<string>();
        var n = 0;
        foreach (var c in Cases)
        {
            var table = $"R{n++}_{Guid.NewGuid():N}";
            var label = c.NativeType + (c.NativeType == "sql_variant" ? " holding " + c.Literal : "");
            try
            {
                await Exec(connection, $"CREATE TABLE dbo.[{table}] (v {c.NativeType} NULL);");
                await Exec(connection, c.Literal == "DEFAULT" ? $"INSERT dbo.[{table}] DEFAULT VALUES;" : $"INSERT dbo.[{table}] (v) VALUES ({c.Literal});");
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = $"SELECT v FROM dbo.[{table}];";
                actual.Add(await ReaderTypeProbe.ReadAsync(cmd, label));
            }
            catch (Exception ex) { actual.Add($"{label}\t-\t-\tSETUP THROWS {ex.GetType().Name}"); }
        }

        var file = new StagingMatrixFile(ExpectedPath());
        if (StagingMatrixFile.Update) { file.Write(actual); return; }
        var expected = file.Read();
        Assert.True(expected.SequenceEqual(actual), "reader types changed (DBDATASYNC_MATRIX_UPDATE=1 regenerates the file):" + Environment.NewLine + StagingMatrixFile.Diff(expected, actual));
    }

    private static async Task Exec(SqlConnection c, string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
