using DbDataKit.Dialect;
using DbDataKit.Sync.Core.Config;
using DbDataKit.Sync.Drivers.Abstractions;
using DbDataKit.Sync.Core.Sql;

namespace DbDataKit.Sync.Drivers.Generic.Tests;

public sealed class ProvisioningColumnBuilderTests
{
    private static ColumnMapping Map(string source, string target) => new() { SourceColumn = source, TargetColumn = target };

    [Fact]
    public void TranslatesEveryMappedColumnByItsSourceMetadata()
    {
        var sourceColumns = new[]
        {
            new ColumnMetadata("Id", "int", IsNullable: false, IsPrimaryKey: true, IsIdentity: false),
            new ColumnMetadata("Name", "text", IsNullable: true, IsPrimaryKey: false, IsIdentity: false),
        };
        var mappings = new[] { Map("Id", "OrderId"), Map("Name", "OrderName") };

        var (columns, warnings) = ProvisioningColumnBuilder.Build(RecordingDialect.Instance, sourceColumns, mappings);

        Assert.Equal(2, columns.Count);
        Assert.Equal("OrderId", columns[0].Name);
        Assert.True(columns[0].IsPrimaryKey);
        Assert.False(columns[0].IsNullable);
        Assert.Equal("OrderName", columns[1].Name);
        Assert.True(columns[1].IsNullable);
        Assert.Empty(warnings);
    }

    [Fact]
    public void AnIdentitySourceColumn_IsStillIncluded_ButWarnsThatItIsCreatedPlain()
    {
        var sourceColumns = new[] { new ColumnMetadata("Id", "int", IsNullable: false, IsPrimaryKey: true, IsIdentity: true) };
        var mappings = new[] { Map("Id", "Id") };

        var (columns, warnings) = ProvisioningColumnBuilder.Build(RecordingDialect.Instance, sourceColumns, mappings);

        Assert.Single(columns);
        var warning = Assert.Single(warnings);
        Assert.Contains("Id", warning);
        Assert.Contains("plain", warning);
    }

    [Fact]
    public void AMappedColumnMissingFromSourceMetadata_Throws()
    {
        var mappings = new[] { Map("Ghost", "Ghost") };

        Assert.Throws<InvalidOperationException>(
            () => ProvisioningColumnBuilder.Build(RecordingDialect.Instance, [], mappings));
    }

    /// <summary>Records exactly which native type strings it was asked to translate, so a test can
    /// assert the builder calls <c>ReadColumnType</c> with the source's own native type rather than
    /// anything else.</summary>
    private sealed class RecordingDialect : SqlDialect
    {
        public static RecordingDialect Instance { get; } = new();
        public override string QuoteIdentifier(string identifier) => identifier;
        public override string ParameterReference(string name) => name;

        public override TypeMapping<CanonicalType> ReadColumnType(string nativeType) => nativeType switch
        {
            "int" => TypeMapping<CanonicalType>.Exact(CanonicalType.Of(CanonicalKind.Int32)),
            "text" => TypeMapping<CanonicalType>.Exact(CanonicalType.String()),
            _ => TypeMapping<CanonicalType>.Unmappable("not a type this test dialect knows"),
        };

        public override RenderedColumnType WriteColumnType(CanonicalType type, string? sourceNote = null) => throw new NotSupportedException();
    }
}
