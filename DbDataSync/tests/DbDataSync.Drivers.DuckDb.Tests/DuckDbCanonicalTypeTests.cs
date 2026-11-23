using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.DuckDb.Tests;

/// <summary>DuckDB's type tables are the shared <c>duckdb</c> dialect's; these pin what Sync does with what the shared table says.</summary>
public class DuckDbCanonicalTypeTests
{
    private static CanonicalType Read(string native) => DuckDbDialect.Instance.ToCanonicalType(native);

    [Theory]
    [InlineData("UTINYINT", CanonicalTypeKind.Int8)]
    [InlineData("USMALLINT", CanonicalTypeKind.Int16)]
    [InlineData("UINTEGER", CanonicalTypeKind.Int32)]
    [InlineData("UBIGINT", CanonicalTypeKind.Int64)]
    public void AnUnsignedInteger_IsReadAsUnsigned(string native, CanonicalTypeKind expected)
    {
        var type = Read(native);

        Assert.Equal(expected, type.Kind);
        Assert.True(type.IsUnsigned);
        Assert.Null(type.SourceNote);
    }

    [Theory]
    [InlineData("UTINYINT", "utinyint")]
    [InlineData("UBIGINT", "ubigint")]
    public void AnUnsignedInteger_IsWrittenBackAsTheSameType(string native, string written) =>
        Assert.Equal(written, DuckDbDialect.Instance.RenderColumnType(Read(native)).Sql);

    [Fact]
    public void AnUnsigned64BitInteger_IsWrittenAsDecimal20_WhereTheTargetHasNoUnsignedType()
    {
        var rendered = PostgresDialect.Instance.RenderColumnType(Read("UBIGINT"));

        Assert.Equal("numeric(20,0)", rendered.Sql);
        Assert.Contains("unsigned", rendered.Fidelity);
    }

    [Fact]
    public void AVarchar_IsUnicodeAndUnbounded()
    {
        var type = Read("VARCHAR");

        Assert.Equal(CanonicalTypeKind.String, type.Kind);
        Assert.True(type.IsUnicode);
        Assert.True(type.IsMax);
    }

    [Fact]
    public void ATimestampWithNoStatedPrecision_RendersAtTimestamp()
    {
        var rendered = DuckDbDialect.Instance.RenderColumnType(new CanonicalType(CanonicalTypeKind.Timestamp, null, null, null, false, false));

        Assert.Equal("timestamp", rendered.Sql, ignoreCase: true);
    }
}
