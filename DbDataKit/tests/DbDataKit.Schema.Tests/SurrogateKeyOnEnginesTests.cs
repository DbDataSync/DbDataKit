using System.Globalization;
using DbDataKit.Dialect;

namespace DbDataKit.Schema.Tests;

/// <summary>
/// The surrogate key of plan 25 (K01) computed by SQL Server and PostgreSQL, against the C# reference (plan 25 K05; <c>testenv/testenv.sh up mssql postgres</c>). The same fixed business keys, written as literals and read from
/// columns of the engine's own types, must give the value <see cref="SurrogateKey.Compute"/> gives; DuckDB's side is tested in <c>DbDataKit.Connections.DuckDb.Tests</c>.
/// </summary>
public sealed class SurrogateKeyOnEnginesTests
{
    public static TheoryData<string> Engines2 => Engines.Both;

    /// <summary>Fixed keys, each a list of parts: text, whole numbers (both signs, the 64-bit limits), dates, text with spaces at the ends, accents, characters outside the Basic Multilingual Plane, the empty string and a leap day.</summary>
    private static readonly object?[][] Cases =
    [
        ["abc"],
        ["  SAP ", 42L],
        ["é€", new DateOnly(2024, 2, 29), -7L],
        ["日本語", 0L],
        ["emoji \U0001F600 and ß", 1L],
        ["", 5L],
        ["k", long.MaxValue],
        ["k", long.MinValue],
        ["a", "b", "c"],
        [new DateOnly(1999, 12, 31), 1L],
        [7L, new DateTime(2024, 2, 29, 13, 45, 59)],
        [7L, new DateTime(2024, 2, 29, 13, 45, 59, 987).AddTicks(6543)],                // the fraction is cut, not rounded: 59.9876543 is :59
        [7L, new DateTime(1999, 12, 31, 23, 59, 59, 999)],                              // and :59.999 does not become the next minute
        ["with\ttab and \"quotes\" and 'apostrophes'", 3L],
    ];

    private static string Literal(string engine, object? part) => part switch
    {
        string s => (engine == Engines.SqlServer ? "N'" : "'") + s.Replace("'", "''") + "'",
        long l => l.ToString(CultureInfo.InvariantCulture),
        DateOnly d => engine == Engines.SqlServer ? $"CAST('{d:yyyy-MM-dd}' AS DATE)" : $"DATE '{d:yyyy-MM-dd}'",
        DateTime t => engine == Engines.SqlServer ? $"CAST('{t:yyyy-MM-dd HH:mm:ss.fffffff}' AS DATETIME2(7))" : $"TIMESTAMP '{t:yyyy-MM-dd HH:mm:ss.ffffff}'",
        _ => throw new NotSupportedException(),
    };

    private static KeyPartType TypeOf(object? part) => part switch { string => KeyPartType.Text, long => KeyPartType.Integer, DateTime => KeyPartType.Timestamp, _ => KeyPartType.Date };

    private static async Task<long?> ScalarAsync(Engines engines, string select)
    {
        await using var command = engines.Connection.CreateCommand();
        command.CommandText = select;
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToInt64(value);
    }

    [Theory, MemberData(nameof(Engines2))]
    public async Task Literals_give_the_reference_value(string engine)
    {
        await using var engines = await Engines.OpenAsync(engine);
        foreach (var key in Cases)
        {
            var sql = SurrogateKey.Sql(engine, key.Select(p => new KeyPart(Literal(engine, p), TypeOf(p))).ToList());
            Assert.True(SurrogateKey.Compute(key) == await ScalarAsync(engines, "SELECT " + sql), $"{engine}: {string.Join(" | ", key)}");
        }
    }

    [Theory, MemberData(nameof(Engines2))]
    public async Task Columns_of_the_engines_own_types_give_the_reference_value(string engine)
    {
        await using var engines = await Engines.OpenAsync(engine);
        var sqlServer = engine == Engines.SqlServer;
        await engines.ExecuteAsync(sqlServer
            ? "CREATE TABLE keyed (id INT PRIMARY KEY, source_system VARCHAR(40) COLLATE Latin1_General_100_CI_AS_SC_UTF8, label NVARCHAR(40), customer_id INT, born DATE)"
            : "CREATE TABLE keyed (id INT PRIMARY KEY, source_system VARCHAR(40), label VARCHAR(40), customer_id INT, born DATE)");
        await engines.ExecuteAsync(sqlServer
            ? "INSERT INTO keyed VALUES (1, N'  SAP ', N'é€', 42, '2024-02-29'), (2, N'日本語', N'emoji ß', -7, '1999-12-31'), (3, N'x', NULL, NULL, NULL)"
            : "INSERT INTO keyed VALUES (1, '  SAP ', 'é€', 42, '2024-02-29'), (2, '日本語', 'emoji ß', -7, '1999-12-31'), (3, 'x', NULL, NULL, NULL)");

        var sql = SurrogateKey.Sql(engine, [new("source_system", KeyPartType.Text), new("customer_id", KeyPartType.Integer)]);
        Assert.Equal(SurrogateKey.Compute("SAP", 42L), await ScalarAsync(engines, $"SELECT {sql} FROM keyed WHERE id = 1"));
        Assert.Equal(SurrogateKey.Compute("日本語", -7L), await ScalarAsync(engines, $"SELECT {sql} FROM keyed WHERE id = 2"));

        var wide = SurrogateKey.Sql(engine, [new("label", KeyPartType.Text), new("born", KeyPartType.Date), new("customer_id", KeyPartType.Integer)]);
        Assert.Equal(SurrogateKey.Compute("é€", new DateOnly(2024, 2, 29), 42L), await ScalarAsync(engines, $"SELECT {wide} FROM keyed WHERE id = 1"));
    }

    [Theory, MemberData(nameof(Engines2))]
    public async Task A_null_part_gives_a_null_key_and_both_signs_occur(string engine)
    {
        await using var engines = await Engines.OpenAsync(engine);
        var nullKey = SurrogateKey.Sql(engine, [new(engine == Engines.SqlServer ? "N'a'" : "'a'", KeyPartType.Text), new("CAST(NULL AS " + (engine == Engines.SqlServer ? "BIGINT" : "bigint") + ")", KeyPartType.Integer)]);
        Assert.Null(await ScalarAsync(engines, "SELECT " + nullKey));

        var signs = new HashSet<bool>();
        for (var i = 0; i < 100; i++)
        {
            var sql = SurrogateKey.Sql(engine, [new(Literal(engine, "k"), KeyPartType.Text), new(i.ToString(CultureInfo.InvariantCulture), KeyPartType.Integer)]);
            var expected = SurrogateKey.Compute("k", (long)i);
            signs.Add(expected < 0);
            Assert.Equal(expected, await ScalarAsync(engines, "SELECT " + sql));
        }
        Assert.Equal(2, signs.Count);
    }
}
