using DbDataKit.Dialect;
using DbDataKit.Sync.Drivers.Abstractions;
using DbDataKit.Sync.Core.Sql;

namespace DbDataSync.Drivers.Postgres.Tests;

/// <summary>Pins the type-mapping table from phase 25 §2 for PostgreSQL's half: translating a native
/// type into canonical form, and rendering a canonical type another engine produced (SQL Server's
/// column types, arriving here as the reverse-direction cases the doc calls out by name).</summary>
public sealed class PostgresDialectCanonicalTypeTests
{
    private static readonly PostgresDialect Dialect = PostgresDialect.Instance;

    /// <summary>The dialect's reading, or the unmappable type when the dialect does not know the name.</summary>
    private static CanonicalType Read(string native) => Dialect.ReadColumnType(native) is { IsMapped: true, Result: { } type } ? type : CanonicalType.Unmappable;

    /// <summary>The note of a reading that is not exact (what the reading already lost), or null.</summary>
    private static string? NoteOf(string native) => Dialect.ReadColumnType(native) is { Fidelity: not Fidelity.Exact } read ? read.Note : null;

    [Theory]
    [InlineData("boolean", CanonicalKind.Boolean)]
    [InlineData("integer", CanonicalKind.Int32)]
    [InlineData("bigint", CanonicalKind.Int64)]
    [InlineData("uuid", CanonicalKind.Guid)]
    [InlineData("xml", CanonicalKind.Xml)]
    [InlineData("jsonb", CanonicalKind.Json)]
    public void SimpleTypes_TranslateToTheirCanonicalKind(string nativeType, CanonicalKind expected) =>
        Assert.Equal(expected, Read(nativeType).Kind);

    [Theory]
    [InlineData("ARRAY")]
    [InlineData("array")]
    [InlineData("USER-DEFINED")]
    public void ArrayAndUserDefinedTypes_AreUnmappable(string nativeType) =>
        Assert.Equal(CanonicalKind.Unmappable, Read(nativeType.ToLowerInvariant()).Kind);

    [Fact]
    public void CharacterVarying_IsAlwaysUnicode()
    {
        var canonical = Read("character varying(50)");
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
        Assert.True(canonical.Unicode);
    }

    [Fact]
    public void Bytea_IsBinaryMax()
    {
        var canonical = Read("bytea");
        Assert.Equal(CanonicalKind.Binary, canonical.Kind);
        Assert.Null(canonical.Length);
    }

    [Fact]
    public void TimestampWithTimeZone_IsTimestampTz()
    {
        var canonical = Read("timestamp with time zone");
        Assert.Equal(CanonicalKind.Timestamp, canonical.Kind);
        Assert.True(canonical.TimeZone);
    }

    // --- Reverse direction: rendering canonical types SQL Server originated. ---

    private static CanonicalType Simple(CanonicalKind kind) => CanonicalType.Of(kind);

    [Fact]
    public void RenderingBoolean_ProducesBoolean_Faithfully()
    {
        var rendered = Dialect.WriteColumnType(Simple(CanonicalKind.Boolean));
        Assert.Equal("boolean", rendered.Sql);
        Assert.Null(rendered.Fidelity);
    }

    [Fact]
    public void RenderingTinyintDerivedInt8_WidensToSmallint_WithAFidelityNote()
    {
        var rendered = Dialect.WriteColumnType(Simple(CanonicalKind.Int8));
        Assert.Equal("smallint", rendered.Sql);
        Assert.NotNull(rendered.Fidelity);
    }

    [Fact]
    public void RenderingDecimal18_2_ProducesNumeric18_2_Faithfully()
    {
        var canonical = new CanonicalType(CanonicalKind.Decimal, Precision: 18, Scale: 2);
        var rendered = Dialect.WriteColumnType(canonical);
        Assert.Equal("numeric(18,2)", rendered.Sql);
        Assert.Null(rendered.Fidelity);
    }

    [Fact]
    public void RenderingMoneyDerivedDecimal19_4_ProducesNumeric19_4_WithTheSourceNote()
    {
        var canonical = new CanonicalType(CanonicalKind.Decimal, Precision: 19, Scale: 4);
        var rendered = Dialect.WriteColumnType(canonical, "carries currency semantics");
        Assert.Equal("numeric(19,4)", rendered.Sql);
        Assert.Contains("currency semantics", rendered.Fidelity);
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
    public void RenderingNvarcharMax_ProducesText_Faithfully()
    {
        var canonical = new CanonicalType(CanonicalKind.String, Unicode: true);
        var rendered = Dialect.WriteColumnType(canonical);
        Assert.Equal("text", rendered.Sql);
        Assert.Null(rendered.Fidelity);
    }

    [Fact]
    public void RenderingDatetime2Scale7_LosesOneDigitOfPrecision()
    {
        var canonical = new CanonicalType(CanonicalKind.Timestamp, Scale: 7);
        var rendered = Dialect.WriteColumnType(canonical);
        Assert.Equal("timestamp(6)", rendered.Sql);
        Assert.Contains("precision", rendered.Fidelity);
    }

    [Fact]
    public void RenderingDatetimeScale3_IsFaithful()
    {
        var canonical = new CanonicalType(CanonicalKind.Timestamp, Scale: 3);
        var rendered = Dialect.WriteColumnType(canonical);
        Assert.Equal("timestamp(3)", rendered.Sql);
        Assert.Null(rendered.Fidelity);
    }

    [Fact]
    public void RenderingDatetimeoffset_ProducesTimestamptz_Faithfully()
    {
        var rendered = Dialect.WriteColumnType(CanonicalType.Timestamp(tz: true));
        Assert.Equal("timestamp(6) with time zone", rendered.Sql);
        Assert.Null(rendered.Fidelity);
    }

    [Fact]
    public void RenderingUniqueidentifier_ProducesUuid_Faithfully()
    {
        var rendered = Dialect.WriteColumnType(Simple(CanonicalKind.Guid));
        Assert.Equal("uuid", rendered.Sql);
        Assert.Null(rendered.Fidelity);
    }

    [Fact]
    public void RenderingVarbinaryMax_ProducesBytea_Faithfully()
    {
        var canonical = new CanonicalType(CanonicalKind.Binary);
        var rendered = Dialect.WriteColumnType(canonical);
        Assert.Equal("bytea", rendered.Sql);
        Assert.Null(rendered.Fidelity);
    }

    [Fact]
    public void RenderingUnmappable_Throws() =>
        Assert.Throws<InvalidOperationException>(() => Dialect.WriteColumnType(Simple(CanonicalKind.Unmappable)));
}
