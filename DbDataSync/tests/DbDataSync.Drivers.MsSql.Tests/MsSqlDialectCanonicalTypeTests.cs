using DbDataKit.Dialect;
using DbDataKit.Sync.Drivers.Abstractions;
using DbDataKit.Sync.Core.Sql;

namespace DbDataSync.Drivers.MsSql.Tests;

/// <summary>Pins the type-mapping table from phase 25 §2 for SQL Server's half of it: translating a
/// native type into canonical form (the forward direction of every pair), and rendering a canonical
/// type SQL Server did not originate (the reverse direction — what a Postgres column becomes here).</summary>
public sealed class MsSqlDialectCanonicalTypeTests
{
    private static readonly MsSqlDialect Dialect = MsSqlDialect.Instance;

    /// <summary>The dialect's reading, or the unmappable type when the dialect does not know the name.</summary>
    private static CanonicalType Read(string native) => Dialect.ReadColumnType(native) is { IsMapped: true, Result: { } type } ? type : CanonicalType.Unmappable;

    /// <summary>The note of a reading that is not exact (what the reading already lost), or null.</summary>
    private static string? NoteOf(string native) => Dialect.ReadColumnType(native) is { Fidelity: not Fidelity.Exact } read ? read.Note : null;

    [Theory]
    [InlineData("bit", CanonicalKind.Boolean)]
    [InlineData("tinyint", CanonicalKind.Int8)]
    [InlineData("int", CanonicalKind.Int32)]
    [InlineData("bigint", CanonicalKind.Int64)]
    [InlineData("uniqueidentifier", CanonicalKind.Guid)]
    [InlineData("xml", CanonicalKind.Xml)]
    public void SimpleTypes_TranslateToTheirCanonicalKind(string nativeType, CanonicalKind expected) =>
        Assert.Equal(expected, Read(nativeType).Kind);

    [Theory]
    [InlineData("sql_variant")]
    [InlineData("hierarchyid")]
    [InlineData("geography")]
    public void TypesWithNoCanonicalEquivalent_AreUnmappable(string nativeType) =>
        Assert.Equal(CanonicalKind.Unmappable, Read(nativeType).Kind);

    [Fact]
    public void DecimalKeepsItsPrecisionAndScale()
    {
        var canonical = Read("decimal(18,2)");
        Assert.Equal(CanonicalKind.Decimal, canonical.Kind);
        Assert.Equal(18, canonical.Precision);
        Assert.Equal(2, canonical.Scale);
        Assert.Null(NoteOf("decimal(18,2)"));
    }

    [Fact]
    public void Money_CollapsesToDecimal19_4_AndCarriesASourceNoteAboutLostCurrencySemantics()
    {
        var canonical = Read("money");
        Assert.Equal(CanonicalKind.Decimal, canonical.Kind);
        Assert.Equal(19, canonical.Precision);
        Assert.Equal(4, canonical.Scale);
        Assert.NotNull(NoteOf("money"));
    }

    [Fact]
    public void NvarcharKeepsItsLengthAndIsUnicode()
    {
        var canonical = Read("nvarchar(50)");
        Assert.Equal(CanonicalKind.String, canonical.Kind);
        Assert.Equal(50, canonical.Length);
        Assert.True(canonical.Unicode);
        Assert.NotNull(canonical.Length);
    }

    [Fact]
    public void NvarcharMax_HasNoLength_AndIsMax()
    {
        var canonical = Read("nvarchar(max)");
        Assert.Equal(CanonicalKind.String, canonical.Kind);
        Assert.Null(canonical.Length);
        Assert.Null(canonical.Length);
    }

    [Fact]
    public void Datetime2_KeepsItsScale()
    {
        var canonical = Read("datetime2(7)");
        Assert.Equal(CanonicalKind.Timestamp, canonical.Kind);
        Assert.Equal(7, canonical.Scale);
    }

    [Fact]
    public void Datetime_IsTimestampWithScale3()
    {
        var canonical = Read("datetime");
        Assert.Equal(CanonicalKind.Timestamp, canonical.Kind);
        Assert.Equal(3, canonical.Scale);
    }

    [Fact]
    public void Datetimeoffset_IsTimestampTz()
    {
        var canonical = Read("datetimeoffset(7)");
        Assert.Equal(CanonicalKind.Timestamp, canonical.Kind);
        Assert.True(canonical.TimeZone);
    }

    [Fact]
    public void VarbinaryMax_IsBinaryMax()
    {
        var canonical = Read("varbinary(max)");
        Assert.Equal(CanonicalKind.Binary, canonical.Kind);
        Assert.Null(canonical.Length);
    }

    [Fact]
    public void PlainBinary_IsFixedLength()
    {
        var canonical = Read("binary(16)");
        Assert.Equal(CanonicalKind.Binary, canonical.Kind);
        Assert.Equal(16, canonical.Length);
        Assert.True(canonical.Fixed);
    }

    [Fact]
    public void PlainVarbinary_IsNotFixedLength()
    {
        var canonical = Read("varbinary(16)");
        Assert.Equal(CanonicalKind.Binary, canonical.Kind);
        Assert.Equal(16, canonical.Length);
        Assert.Null(canonical.Fixed);
    }

    [Theory]
    [InlineData("timestamp")]
    [InlineData("rowversion")]
    public void TimestampAndRowversion_MapToFixedLengthBinary8(string nativeType)
    {
        var canonical = Read(nativeType);
        Assert.Equal(CanonicalKind.Binary, canonical.Kind);
        Assert.Equal(8, canonical.Length);
        Assert.True(canonical.Fixed);
        Assert.NotNull(canonical.Length);
    }

    // --- Reverse direction: rendering a canonical type this dialect did not originate. ---

    private static CanonicalType Simple(CanonicalKind kind) => CanonicalType.Of(kind);

    [Fact]
    public void RenderingTextFromAnotherEngine_ProducesNvarcharMax_Faithfully()
    {
        var canonical = new CanonicalType(CanonicalKind.String, Unicode: true);
        var rendered = Dialect.WriteColumnType(canonical);
        Assert.Equal("nvarchar(max)", rendered.Sql);
        Assert.Null(rendered.Fidelity);
    }

    [Fact]
    public void RenderingBoolean_ProducesBit()
    {
        var rendered = Dialect.WriteColumnType(Simple(CanonicalKind.Boolean));
        Assert.Equal("bit", rendered.Sql);
        Assert.Null(rendered.Fidelity);
    }

    [Fact]
    public void RenderingGuid_ProducesUniqueidentifier()
    {
        var rendered = Dialect.WriteColumnType(Simple(CanonicalKind.Guid));
        Assert.Equal("uniqueidentifier", rendered.Sql);
        Assert.Null(rendered.Fidelity);
    }

    [Fact]
    public void RenderingJson_ProducesNvarcharMax_WithAFidelityNote()
    {
        var rendered = Dialect.WriteColumnType(Simple(CanonicalKind.Json));
        Assert.Equal("nvarchar(max)", rendered.Sql);
        Assert.NotNull(rendered.Fidelity);
    }

    [Fact]
    public void RenderingVarbinaryMax_IsFaithful()
    {
        var canonical = new CanonicalType(CanonicalKind.Binary);
        var rendered = Dialect.WriteColumnType(canonical);
        Assert.Equal("varbinary(max)", rendered.Sql);
        Assert.Null(rendered.Fidelity);
    }

    [Fact]
    public void RenderingAFixedLengthBinary_ProducesBinaryNotVarbinary()
    {
        // What ReadColumnType gives a rowversion/timestamp source column (and a genuine binary(n)
        // one) — the round trip this whole IsFixed flag exists for.
        var canonical = new CanonicalType(CanonicalKind.Binary, Length: 8, Fixed: true);
        var rendered = Dialect.WriteColumnType(canonical);
        Assert.Equal("binary(8)", rendered.Sql);
        Assert.Null(rendered.Fidelity);
    }

    [Fact]
    public void RenderingANonFixedBinary_StillProducesVarbinary()
    {
        var canonical = new CanonicalType(CanonicalKind.Binary, Length: 8);
        var rendered = Dialect.WriteColumnType(canonical);
        Assert.Equal("varbinary(8)", rendered.Sql);
    }

    [Fact]
    public void RenderingUnmappable_Throws() =>
        Assert.Throws<InvalidOperationException>(() => Dialect.WriteColumnType(Simple(CanonicalKind.Unmappable)));

    [Fact]
    public void ASourceNoteFromTheOriginatingEngine_SurvivesIntoTheRenderedFidelity()
    {
        var canonical = new CanonicalType(CanonicalKind.Decimal, Precision: 19, Scale: 4);
        var rendered = Dialect.WriteColumnType(canonical, "carries currency semantics");
        Assert.Contains("currency semantics", rendered.Fidelity);
    }
}
