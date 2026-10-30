using DbDataKit.Dialect;
using DuckDB.NET.Data;

namespace DbDataKit.Connections.DuckDb.Tests;

public class SurrogateKeyTests
{
    private static long? OnDuckDb(params (string Sql, KeyPartType Type)[] parts)
    {
        using var c = new DuckDBConnection("Data Source=:memory:");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT " + SurrogateKey.Sql("duckdb", parts.Select(p => new KeyPart(p.Sql, p.Type)).ToList());
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? null : Convert.ToInt64(v);
    }

    [Fact]
    public void The_reference_is_the_first_eight_bytes_of_sha256_as_a_signed_integer()
    {
        // SHA-256("abc") = ba7816bf8f01cfea...: the top bit is set, so the value is negative
        Assert.Equal(unchecked((long)0xba7816bf8f01cfeaUL), SurrogateKey.Hash("abc"));
    }

    [Fact]
    public void Parts_are_trimmed_joined_with_the_unit_separator_and_typed()
    {
        Assert.Equal("SAP\u001f42\u001f2024-02-29", SurrogateKey.Normalize(["  SAP ", 42, new DateOnly(2024, 2, 29)]));
        Assert.Null(SurrogateKey.Normalize(["SAP", null]));
        Assert.Null(SurrogateKey.Compute("SAP", null));
        Assert.NotEqual(SurrogateKey.Compute("ab", "c"), SurrogateKey.Compute("a", "bc"));
    }

    [Fact]
    public void DuckDb_computes_the_reference_value()
    {
        Assert.Equal(SurrogateKey.Compute("abc"), OnDuckDb(("'abc'", KeyPartType.Text)));
        Assert.Equal(SurrogateKey.Compute("SAP", 42L), OnDuckDb(("' SAP'", KeyPartType.Text), ("42", KeyPartType.Integer)));
        Assert.Equal(SurrogateKey.Compute("é€", new DateOnly(2024, 2, 29), -7L),
            OnDuckDb(("'é€'", KeyPartType.Text), ("DATE '2024-02-29'", KeyPartType.Date), ("-7", KeyPartType.Integer)));
    }

    [Fact]
    public void DuckDb_gives_both_signs()
    {
        // 200 keys: the reference value is positive for some and negative for others, and DuckDB agrees with every one
        var signs = new HashSet<bool>();
        for (var i = 0; i < 200; i++)
        {
            var expected = SurrogateKey.Compute("k", (long)i);
            signs.Add(expected < 0);
            Assert.Equal(expected, OnDuckDb(("'k'", KeyPartType.Text), (i.ToString(), KeyPartType.Integer)));
        }
        Assert.Equal(2, signs.Count);
    }

    [Fact]
    public void A_null_part_makes_the_key_null()
    {
        Assert.Null(OnDuckDb(("'a'", KeyPartType.Text), ("CAST(NULL AS VARCHAR)", KeyPartType.Text)));
    }

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("fabric")]
    [InlineData("postgres")]
    public void Other_engines_get_their_own_text(string engine)
    {
        var sql = SurrogateKey.Sql(engine, [new KeyPart("c.source_system", KeyPartType.Text), new KeyPart("c.customer_id", KeyPartType.Integer)]);
        Assert.Contains("c.customer_id IS NULL", sql);
        Assert.Contains(engine == "postgres" ? "sha256(convert_to(" : "HASHBYTES('SHA2_256'", sql);
    }

    [Fact]
    public void An_engine_with_no_definition_is_refused_by_name() =>
        Assert.Contains("mysql", Assert.Throws<NotSupportedException>(() => SurrogateKey.Sql("mysql", [new KeyPart("x", KeyPartType.Text)])).Message);
}
