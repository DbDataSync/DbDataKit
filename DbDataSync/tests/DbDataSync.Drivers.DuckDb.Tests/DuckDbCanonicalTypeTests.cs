using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.DuckDb.Tests;

/// <summary>DuckDB's type tables are the shared <c>duckdb</c> dialect's; these pin what Sync does with what the shared table says.</summary>
public class DuckDbCanonicalTypeTests
{
    private static CanonicalType Read(string native) => DuckDbDialect.Instance.ToCanonicalType(native);

    [Theory]
    [InlineData("UTINYINT", CanonicalTypeKind.Int16)]
    [InlineData("USMALLINT", CanonicalTypeKind.Int32)]
    [InlineData("UINTEGER", CanonicalTypeKind.Int64)]
    public void AnUnsignedInteger_IsReadAsTheNextWiderSignedType_WithANote(string native, CanonicalTypeKind expected)
    {
        var type = Read(native);

        Assert.Equal(expected, type.Kind);
        Assert.Contains("Unsigned", type.SourceNote);
    }

    [Fact]
    public void AnUnsigned64BitInteger_IsReadAsDecimal20()
    {
        var type = Read("UBIGINT");

        Assert.Equal(CanonicalTypeKind.Decimal, type.Kind);
        Assert.Equal(20, type.Precision);
        Assert.Equal(0, type.Scale);
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
