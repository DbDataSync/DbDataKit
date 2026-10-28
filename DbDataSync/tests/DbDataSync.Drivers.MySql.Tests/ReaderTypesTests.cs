using System.Runtime.CompilerServices;
using DbDataSync.TestSupport;
using MySqlConnector;
using Xunit;

namespace DbDataSync.Drivers.MySql.Tests;

/// <summary>What the MySQL and MariaDB source readers (<c>MySqlConnector</c>'s <c>GetValue</c>, default connection options) return per column type.</summary>
public abstract class MySqlFamilyReaderTypesTestsBase<TDatabase>(TDatabase db, string engine) : IClassFixture<TDatabase>
    where TDatabase : MySqlFamilyTestDatabase
{
    private static readonly ReaderTypeProbe.Case[] Cases =
    [
        new("tinyint", "-5"), new("tinyint(1)", "1"), new("tinyint unsigned", "200"), new("smallint", "-300"), new("smallint unsigned", "60000"),
        new("mediumint", "-70000"), new("mediumint unsigned", "70000"), new("int", "-70000"), new("int unsigned", "4000000000"),
        new("bigint", "5000000000"), new("bigint unsigned", "18446744073709551615"),
        new("decimal(18,2)", "12345.67"), new("decimal(38,10)", "123456789012345678.1234567890"), new("decimal(65,30)", "1.5"), new("float", "1.5"), new("double", "1.5"),
        new("bit(8)", "b'10101010'"), new("bit(1)", "b'1'"), new("boolean", "true"), new("year", "2024"),
        new("char(5)", "'ab'"), new("char(36)", "'6f9619ff-8b86-d011-b42d-00c04fc964ff'"), new("varchar(50)", "'text'"), new("tinytext", "'text'"), new("text", "'text'"), new("longtext", "'text'"),
        new("json", "'{\"a\":1}'"), new("enum('a','b')", "'a'"), new("set('a','b')", "'a,b'"),
        new("binary(4)", "0x01020304"), new("binary(16)", "0x6f9619ff8b86d011b42d00c04fc964ff"), new("varbinary(50)", "0x010203"), new("blob", "0x010203"), new("longblob", "0x010203"),
        new("date", "'2024-03-17'"), new("date", "'0000-00-00'"), new("time", "'09:41:22'"), new("time(6)", "'-838:59:59.123456'"), new("datetime", "'2024-03-17 09:41:22'"), new("datetime(6)", "'2024-03-17 09:41:22.123456'"),
        new("datetime", "'0000-00-00 00:00:00'"), new("timestamp(6)", "'2024-03-17 09:41:22.123456'"),
        new("geometry", "ST_GeomFromText('POINT(1 2)')"),
    ];

    [Fact]
    public async Task EveryColumnTypeReadsBackAsTheTableSays()
    {
        await using var connection = db.OpenConnection();
        await Exec(connection, "SET SESSION sql_mode = ''"); // the zero dates are inserted on purpose
        var actual = new List<string>();
        var n = 0;
        foreach (var c in Cases)
        {
            var table = $"r{n++}_{Guid.NewGuid():N}";
            var label = c.NativeType + (c.Literal.StartsWith("'0000") ? " zero" : c.NativeType == "tinyint(1)" ? " TreatTinyAsBoolean" : "");
            try
            {
                await Exec(connection, $"CREATE TABLE `{table}` (v {c.NativeType} NULL);");
                await Exec(connection, $"INSERT INTO `{table}` (v) VALUES ({c.Literal});");
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = $"SELECT v FROM `{table}`;";
                actual.Add(await ReaderTypeProbe.ReadAsync(cmd, label));
            }
            catch (Exception ex) { actual.Add($"{label}\t-\t-\tSETUP THROWS {ex.GetType().Name}"); }
        }

        var file = new StagingMatrixFile(Path.Combine(Path.GetDirectoryName(Here())!, $"ReaderTypes.{engine}.expected.tsv"));
        if (StagingMatrixFile.Update) { file.Write(actual); return; }
        var expected = file.Read();
        Assert.True(expected.SequenceEqual(actual), "reader types changed (DBDATASYNC_MATRIX_UPDATE=1 regenerates the file):" + Environment.NewLine + StagingMatrixFile.Diff(expected, actual));
    }

    private static string Here([CallerFilePath] string here = "") => here;

    private static async Task Exec(MySqlConnection c, string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}

[Trait("Category", "Integration")]
public sealed class MySqlReaderTypesTests(MySqlTestDatabase db) : MySqlFamilyReaderTypesTestsBase<MySqlTestDatabase>(db, "mysql");

[Trait("Category", "Integration")]
public sealed class MariaDbReaderTypesTests(MariaDbTestDatabase db) : MySqlFamilyReaderTypesTestsBase<MariaDbTestDatabase>(db, "mariadb");
