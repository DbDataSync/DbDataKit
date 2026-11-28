using DbDataKit.Dialect;
using OracleDialect = DbDataKit.Sync.Core.Sql.OracleDialect;
using DbDataKit.Sync.Drivers.Abstractions;
using DbDataKit.Sync.Core.Sql;

namespace DbDataSync.Drivers.Oracle.Tests;

/// <summary>Pins the type-mapping table from phase 25 §2 for Oracle's half: translating a native type
/// into canonical form, and rendering a canonical type another engine produced.</summary>
public sealed class OracleDialectCanonicalTypeTests
{
    private static readonly OracleDialect Dialect = OracleDialect.Instance;

    /// <summary>The dialect's reading, or the unmappable type when the dialect does not know the name.</summary>
    private static CanonicalType Read(string native) => Dialect.ReadColumnType(native) is { IsMapped: true, Result: { } type } ? type : CanonicalType.Unmappable;

    /// <summary>The note of a reading that is not exact (what the reading already lost), or null.</summary>
    private static string? NoteOf(string native) => Dialect.ReadColumnType(native) is { Fidelity: not Fidelity.Exact } read ? read.Note : null;

    [Fact]
    public void BareNumber_IsApproximatedAsDecimal38_10_WithASourceNote()
    {
        var canonical = Read("NUMBER");
        Assert.Equal(CanonicalKind.Decimal, canonical.Kind);
        Assert.Equal(38, canonical.Precision);
        Assert.Equal(10, canonical.Scale);
        Assert.NotNull(NoteOf("NUMBER"));
    }

    [Theory]
    [InlineData("NUMBER(4,0)", CanonicalKind.Int16)]
    [InlineData("NUMBER(9,0)", CanonicalKind.Int32)]
    [InlineData("NUMBER(18,0)", CanonicalKind.Int64)]
    [InlineData("NUMBER(19,0)", CanonicalKind.Decimal)]
    public void NumberWithZeroScale_ClassifiesByPrecision(string nativeType, CanonicalKind expected) =>
        Assert.Equal(expected, Read(nativeType).Kind);

    [Fact]
    public void NumberWithNonZeroScale_IsDecimal()
    {
        var canonical = Read("NUMBER(18,2)");
        Assert.Equal(CanonicalKind.Decimal, canonical.Kind);
        Assert.Equal(18, canonical.Precision);
        Assert.Equal(2, canonical.Scale);
    }

    [Fact]
    public void Date_IsTimestampAtScaleZero_NotDateOnly()
    {
        var canonical = Read("DATE");
        Assert.Equal(CanonicalKind.Timestamp, canonical.Kind);
        Assert.Equal(0, canonical.Scale);
    }

    [Fact]
    public void TimestampWithItsOwnEmbeddedPrecision_ParsesCorrectly()
    {
        // Confirmed against a live server: ALL_TAB_COLUMNS.DATA_TYPE for a TIMESTAMP column already
        // includes its precision, unlike every other type family — see OracleCatalog's own doc comment.
        var canonical = Read("TIMESTAMP(6)");
        Assert.Equal(CanonicalKind.Timestamp, canonical.Kind);
        Assert.Equal(6, canonical.Scale);
    }

    [Fact]
    public void TimestampWithTimeZone_IsTimestampTz_DespiteCanonicalTypeSpecTruncatingTheSuffix()
    {
        // CanonicalTypeSpec.Parse truncates at the first ')', which would silently drop "WITH TIME
        // ZONE" if this dialect relied on it alone — this is the test for the explicit substring check
        // OracleDialect.ReadColumnType does before falling through to the shared parser.
        var canonical = Read("TIMESTAMP(6) WITH TIME ZONE");
        Assert.Equal(CanonicalKind.Timestamp, canonical.Kind);
        Assert.True(canonical.TimeZone);
        Assert.Equal(6, canonical.Scale);
    }

    [Fact]
    public void TimestampWithLocalTimeZone_IsAlsoTimestampTz()
    {
        var canonical = Read("TIMESTAMP(6) WITH LOCAL TIME ZONE");
        Assert.Equal(CanonicalKind.Timestamp, canonical.Kind);
        Assert.True(canonical.TimeZone);
    }

    [Fact]
    public void Varchar2_IsNotUnicode_Nvarchar2Is()
    {
        Assert.Null(Read("VARCHAR2(50)").Unicode);
        Assert.True(Read("NVARCHAR2(50)").Unicode);
    }

    [Fact]
    public void Clob_HasNoLength_AndIsMax()
    {
        var canonical = Read("CLOB");
        Assert.Equal(CanonicalKind.String, canonical.Kind);
        Assert.Null(canonical.Length);
    }

    [Theory]
    [InlineData("ROWID")]
    [InlineData("UROWID")]
    [InlineData("SDO_GEOMETRY")]
    public void RowidAndSpatialTypes_AreUnmappable(string nativeType) =>
        Assert.Equal(CanonicalKind.Unmappable, Read(nativeType).Kind);

    // --- Reverse direction: rendering canonical types another engine originated. ---

    private static CanonicalType Simple(CanonicalKind kind) => CanonicalType.Of(kind);

    [Fact]
    public void RenderingBoolean_ProducesNumber1_WithANoNativeBooleanNote()
    {
        var rendered = Dialect.WriteColumnType(Simple(CanonicalKind.Boolean));
        Assert.Equal("number(1)", rendered.Sql);
        Assert.Contains("no BOOLEAN column type", rendered.Fidelity);
    }

    [Fact]
    public void RenderingDecimal18_2_ProducesNumber18_2_Faithfully()
    {
        var canonical = new CanonicalType(CanonicalKind.Decimal, Precision: 18, Scale: 2);
        var rendered = Dialect.WriteColumnType(canonical);
        Assert.Equal("number(18,2)", rendered.Sql);
        Assert.Null(rendered.Fidelity);
    }

    [Fact]
    public void RenderingNvarchar50_ProducesNvarchar250_Faithfully()
    {
        var canonical = new CanonicalType(CanonicalKind.String, Length: 50, Unicode: true);
        var rendered = Dialect.WriteColumnType(canonical);
        Assert.Equal("nvarchar2(50)", rendered.Sql);
        Assert.Null(rendered.Fidelity);
    }

    [Fact]
    public void RenderingVarchar50_ProducesVarchar250_NotNvarchar()
    {
        var canonical = new CanonicalType(CanonicalKind.String, Length: 50);
        var rendered = Dialect.WriteColumnType(canonical);
        Assert.Equal("varchar2(50)", rendered.Sql);
    }

    [Fact]
    public void RenderingNvarcharMax_ProducesNclob_Faithfully()
    {
        var canonical = new CanonicalType(CanonicalKind.String, Unicode: true);
        var rendered = Dialect.WriteColumnType(canonical);
        Assert.Equal("nclob", rendered.Sql);
        Assert.Null(rendered.Fidelity);
    }

    [Fact]
    public void RenderingRawOver2000Bytes_FallsBackToBlob_WithAFidelityNote()
    {
        var canonical = new CanonicalType(CanonicalKind.Binary, Length: 4000);
        var rendered = Dialect.WriteColumnType(canonical);
        Assert.Equal("blob", rendered.Sql);
        Assert.NotNull(rendered.Fidelity);
    }

    [Fact]
    public void RenderingTime_ProducesTimestamp_WithAFidelityNote()
    {
        var rendered = Dialect.WriteColumnType(Simple(CanonicalKind.Time));
        Assert.StartsWith("timestamp(", rendered.Sql);
        Assert.Contains("no native TIME", rendered.Fidelity);
    }

    [Fact]
    public void RenderingTimestampTz_ProducesTimestampWithTimeZone_Faithfully()
    {
        var rendered = Dialect.WriteColumnType(CanonicalType.Timestamp(tz: true));
        Assert.EndsWith("with time zone", rendered.Sql);
        Assert.Null(rendered.Fidelity);
    }

    [Fact]
    public void RenderingGuid_ProducesVarchar236_WithAFidelityNote()
    {
        var rendered = Dialect.WriteColumnType(Simple(CanonicalKind.Guid));
        Assert.Equal("varchar2(36)", rendered.Sql);
        Assert.NotNull(rendered.Fidelity);
    }

    [Fact]
    public void RenderingJson_ProducesClob_WithAVersionFloorNote()
    {
        var rendered = Dialect.WriteColumnType(Simple(CanonicalKind.Json));
        Assert.Equal("clob", rendered.Sql);
        Assert.Contains("21c", rendered.Fidelity);
    }

    [Fact]
    public void RenderingUnmappable_Throws() =>
        Assert.Throws<InvalidOperationException>(() => Dialect.WriteColumnType(Simple(CanonicalKind.Unmappable)));
}
