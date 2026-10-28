using System.Runtime.CompilerServices;
using DbDataSync.TestSupport;
using Npgsql;
using Xunit;

namespace DbDataSync.Drivers.Postgres.Tests;

/// <summary>What the PostgreSQL source readers that go through <c>NpgsqlDataReader.GetValue</c> return per column type (the logical-slot reader is covered by <c>LogicalSlotValueTypesTests</c>).</summary>
[Trait("Category", "Integration")]
public sealed class ReaderTypesTests(PostgresTestDatabase db) : IClassFixture<PostgresTestDatabase>
{
    private static readonly ReaderTypeProbe.Case[] Cases =
    [
        new("boolean", "true"), new("smallint", "-300"), new("integer", "70000"), new("bigint", "5000000000"),
        new("numeric(18,2)", "12345.67"), new("numeric", "123456789012345678901234567890.5"), new("numeric", "'NaN'"), new("money", "12.34"),
        new("real", "1.5"), new("double precision", "1.5"),
        new("text", "'text'"), new("character varying(50)", "'text'"), new("character(5)", "'ab'"), new("\"char\"", "'a'"), new("name", "'text'"),
        new("bytea", "'\\x010203'"), new("uuid", "'6f9619ff-8b86-d011-b42d-00c04fc964ff'"),
        new("date", "'2024-03-17'"), new("date", "'infinity'"), new("time", "'09:41:22.123456'"), new("timetz", "'09:41:22.123456+02'"),
        new("timestamp", "'2024-03-17 09:41:22.123456'"), new("timestamptz", "'2024-03-17 09:41:22.123456+02'"), new("timestamp", "'infinity'"),
        new("interval", "'1 day 02:03:04'"), new("interval", "'1 month'"),
        new("json", "'{\"a\":1}'"), new("jsonb", "'{\"a\":1}'"), new("xml", "'<a>1</a>'"),
        new("inet", "'192.168.0.1'"), new("cidr", "'192.168.0.0/24'"), new("macaddr", "'08:00:2b:01:02:03'"),
        new("bit(3)", "B'101'"), new("bit varying(5)", "B'101'"), new("integer[]", "'{1,2,3}'"), new("text[]", "'{a,b}'"),
        new("point", "'(1,2)'"), new("oid", "42"), new("tsvector", "'a b'::tsvector"), new("int4range", "'[1,5)'"),
    ];

    private static string ExpectedPath([CallerFilePath] string here = "") => Path.Combine(Path.GetDirectoryName(here)!, "ReaderTypes.postgres.expected.tsv");

    [Fact]
    public async Task EveryColumnTypeReadsBackAsTheTableSays()
    {
        var actual = new List<string>();
        var n = 0;
        foreach (var c in Cases)
        {
            var table = $"r{n++}_{Guid.NewGuid():N}";
            var label = c.NativeType + (c.Literal == "'NaN'" ? " NaN" : c.Literal == "'infinity'" ? " infinity" : c.NativeType == "numeric" ? " unbounded" : c.Literal == "'1 month'" ? " months" : "");
            await using var connection = db.OpenConnection();
            try
            {
                await Exec(connection, $"CREATE TABLE public.\"{table}\" (v {c.NativeType});");
                await Exec(connection, $"INSERT INTO public.\"{table}\" (v) VALUES ({c.Literal});");
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = $"SELECT v FROM public.\"{table}\";";
                actual.Add(await ReaderTypeProbe.ReadAsync(cmd, label));
            }
            catch (Exception ex) { actual.Add($"{label}\t-\t-\tSETUP THROWS {ex.GetType().Name}"); }
        }

        var file = new StagingMatrixFile(ExpectedPath());
        if (StagingMatrixFile.Update) { file.Write(actual); return; }
        var expected = file.Read();
        Assert.True(expected.SequenceEqual(actual), "reader types changed (DBDATASYNC_MATRIX_UPDATE=1 regenerates the file):" + Environment.NewLine + StagingMatrixFile.Diff(expected, actual));
    }

    private static async Task Exec(NpgsqlConnection c, string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
