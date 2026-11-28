using DbDataKit.Dialect;
using DbDataKit.Sync.Core.Sql;

namespace DbDataSync.Drivers.DuckDb.Tests;

/// <summary>DuckDB's type tables are the shared <c>duckdb</c> dialect's; these pin what Sync does with what the shared table says.</summary>
public class DuckDbCanonicalTypeTests
{
    private static CanonicalType Read(string native) => DuckDbDialect.Instance.ReadColumnType(native).Result!;

    [Theory]
    [InlineData("UTINYINT", CanonicalKind.Int8)]
    [InlineData("USMALLINT", CanonicalKind.Int16)]
    [InlineData("UINTEGER", CanonicalKind.Int32)]
    [InlineData("UBIGINT", CanonicalKind.Int64)]
    public void AnUnsignedInteger_IsReadAsUnsigned(string native, CanonicalKind expected)
    {
        var type = Read(native);

        Assert.Equal(expected, type.Kind);
        Assert.True(type.Unsigned);
        Assert.Equal(Fidelity.Exact, DuckDbDialect.Instance.ReadColumnType(native).Fidelity);
    }

    [Theory]
    [InlineData("UTINYINT", "utinyint")]
    [InlineData("UBIGINT", "ubigint")]
    public void AnUnsignedInteger_IsWrittenBackAsTheSameType(string native, string written) =>
        Assert.Equal(written, DuckDbDialect.Instance.WriteColumnType(Read(native)).Sql);

    [Fact]
    public void AnUnsigned64BitInteger_IsWrittenAsDecimal20_WhereTheTargetHasNoUnsignedType()
    {
        var rendered = PostgresDialect.Instance.WriteColumnType(Read("UBIGINT"));

        Assert.Equal("numeric(20,0)", rendered.Sql);
        Assert.Contains("unsigned", rendered.Fidelity);
    }

    [Fact]
    public void AVarchar_IsUnicodeAndUnbounded()
    {
        var type = Read("VARCHAR");

        Assert.Equal(CanonicalKind.String, type.Kind);
        Assert.True(type.Unicode);
        Assert.Null(type.Length);
    }

    [Fact]
    public void ATimestampWithNoStatedPrecision_RendersAtTimestamp()
    {
        var rendered = DuckDbDialect.Instance.WriteColumnType(new CanonicalType(CanonicalKind.Timestamp));

        Assert.Equal("timestamp", rendered.Sql, ignoreCase: true);
    }
}
