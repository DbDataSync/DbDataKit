using DbDataKit.Dialect;
using MySqlDialect = DbDataKit.Sync.Core.Sql.MySqlDialect;
using DbDataKit.Sync.Drivers.Abstractions;
using DbDataKit.Sync.Core.Sql;

namespace DbDataSync.Drivers.MySql.Tests;

/// <summary>Pins the type-mapping table from phase 25 §2 for MySQL/MariaDB's half: translating a native
/// type into canonical form, and rendering a canonical type another engine produced.</summary>
public sealed class MySqlDialectCanonicalTypeTests
{
    private static readonly MySqlDialect Dialect = MySqlDialect.Instance;

    /// <summary>The dialect's reading, or the unmappable type when the dialect does not know the name.</summary>
    private static CanonicalType Read(string native) => Dialect.ReadColumnType(native) is { IsMapped: true, Result: { } type } ? type : CanonicalType.Unmappable;

    /// <summary>The note of a reading that is not exact (what the reading already lost), or null.</summary>
    private static string? NoteOf(string native) => Dialect.ReadColumnType(native) is { Fidelity: not Fidelity.Exact } read ? read.Note : null;

    [Theory]
    [InlineData("smallint", CanonicalKind.Int16)]
    [InlineData("int", CanonicalKind.Int32)]
    [InlineData("bigint", CanonicalKind.Int64)]
    [InlineData("json", CanonicalKind.Json)]
    public void SimpleTypes_TranslateToTheirCanonicalKind(string nativeType, CanonicalKind expected) =>
        Assert.Equal(expected, Read(nativeType).Kind);

    [Fact]
    public void TinyintWithDisplayWidthOne_IsBoolean()
    {
        var canonical = Read("tinyint(1)");
        Assert.Equal(CanonicalKind.Boolean, canonical.Kind);
    }

    [Fact]
    public void BareTinyint_IsInt8_NotBoolean()
    {
        var canonical = Read("tinyint");
        Assert.Equal(CanonicalKind.Int8, canonical.Kind);
    }

    [Theory]
    [InlineData("enum")]
    [InlineData("set")]
    [InlineData("bit")]
    [InlineData("geometry")]
    public void EnumSetBitAndSpatialTypes_AreUnmappable(string nativeType) =>
        Assert.Equal(CanonicalKind.Unmappable, Read(nativeType).Kind);

    [Fact]
    public void Varchar_IsAlwaysUnicode()
    {
        var canonical = Read("varchar(50)");
        Assert.Equal(CanonicalKind.String, canonical.Kind);
        Assert.Equal(50, canonical.Length);
        Assert.True(canonical.Unicode);
    }

    [Fact]
    public void Text_HasNoLength_AndIsMax()
    {
        var canonical = Read("text");
        Assert.Equal(CanonicalKind.String, canonical.Kind);
        Assert.Null(canonical.Length);
    }

    [Fact]
    public void Blob_IsBinaryMax()
    {
        var canonical = Read("blob");
        Assert.Equal(CanonicalKind.Binary, canonical.Kind);
        Assert.Null(canonical.Length);
    }

    [Fact]
    public void Timestamp_IsTimestampTz_WithASourceNote()
    {
        var canonical = Read("timestamp");
        Assert.Equal(CanonicalKind.Timestamp, canonical.Kind);
        Assert.True(canonical.TimeZone);
        Assert.NotNull(NoteOf("timestamp"));
    }

    [Fact]
    public void Datetime_IsPlainTimestamp_WithNoSourceNote()
    {
        var canonical = Read("datetime");
        Assert.Equal(CanonicalKind.Timestamp, canonical.Kind);
        Assert.Null(NoteOf("datetime"));
    }

    // --- Reverse direction: rendering canonical types another engine originated. ---

    private static CanonicalType Simple(CanonicalKind kind) => CanonicalType.Of(kind);

    [Fact]
    public void RenderingBoolean_ProducesTinyint1_WithANoNativeBooleanNote()
    {
        var rendered = Dialect.WriteColumnType(Simple(CanonicalKind.Boolean));
        Assert.Equal("tinyint(1)", rendered.Sql);
        Assert.Contains("no native boolean", rendered.Fidelity);
    }

    [Fact]
    public void RenderingDecimal18_2_ProducesDecimal18_2_Faithfully()
    {
        var canonical = new CanonicalType(CanonicalKind.Decimal, Precision: 18, Scale: 2);
        var rendered = Dialect.WriteColumnType(canonical);
        Assert.Equal("decimal(18,2)", rendered.Sql);
        Assert.Null(rendered.Fidelity);
    }

    [Fact]
    public void RenderingNvarchar50_ProducesVarchar50_Faithfully()
    {
        var canonical = new CanonicalType(CanonicalKind.String, Length: 50, Unicode: true);
        var rendered = Dialect.WriteColumnType(canonical);
        Assert.Equal("varchar(50)", rendered.Sql);
        Assert.Null(rendered.Fidelity);
    }

    [Fact]
    public void RenderingNvarcharMax_ProducesLongtext_Faithfully()
    {
        var canonical = new CanonicalType(CanonicalKind.String, Unicode: true);
        var rendered = Dialect.WriteColumnType(canonical);
        Assert.Equal("longtext", rendered.Sql);
        Assert.Null(rendered.Fidelity);
    }

    [Fact]
    public void RenderingVarcharOver65535_FallsBackToLongtext_WithAFidelityNote()
    {
        var canonical = new CanonicalType(CanonicalKind.String, Length: 100_000, Unicode: true);
        var rendered = Dialect.WriteColumnType(canonical);
        Assert.Equal("longtext", rendered.Sql);
        Assert.NotNull(rendered.Fidelity);
    }

    [Fact]
    public void RenderingDatetime2Scale7_LosesOneDigitOfPrecision()
    {
        var canonical = new CanonicalType(CanonicalKind.Timestamp, Scale: 7);
        var rendered = Dialect.WriteColumnType(canonical);
        Assert.Equal("datetime(6)", rendered.Sql);
        Assert.Contains("precision", rendered.Fidelity);
    }

    [Fact]
    public void RenderingDatetimeoffset_ProducesTimestamp_WithAFidelityNote()
    {
        var rendered = Dialect.WriteColumnType(CanonicalType.Timestamp(tz: true));
        Assert.Equal("timestamp(6)", rendered.Sql);
        Assert.NotNull(rendered.Fidelity);
    }

    [Fact]
    public void RenderingUniqueidentifier_ProducesChar36_WithAFidelityNote()
    {
        var rendered = Dialect.WriteColumnType(Simple(CanonicalKind.Guid));
        Assert.Equal("char(36)", rendered.Sql);
        Assert.NotNull(rendered.Fidelity);
    }

    [Fact]
    public void RenderingVarbinaryMax_ProducesLongblob()
    {
        var canonical = new CanonicalType(CanonicalKind.Binary);
        var rendered = Dialect.WriteColumnType(canonical);
        Assert.Equal("longblob", rendered.Sql);
    }

    [Fact]
    public void RenderingUnmappable_Throws() =>
        Assert.Throws<InvalidOperationException>(() => Dialect.WriteColumnType(Simple(CanonicalKind.Unmappable)));
}
