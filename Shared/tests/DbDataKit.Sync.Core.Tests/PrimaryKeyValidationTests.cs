using DbDataSync.Core.Config;

namespace DbDataSync.Core.Tests;

/// <summary>Phase 201M: a mapping's stated primary key names each column once and only columns it maps.</summary>
public sealed class PrimaryKeyValidationTests
{
    private static TableMappingConfig Mapping(params string[] key) => new()
    {
        Name = "orders",
        Sources = [new SourceTableSpec { ConnectionName = "s", Database = "d", Schema = "dbo", Table = "Orders" }],
        Targets = [new TableSpec { ConnectionName = "t", Database = "d", Schema = "dbo", Table = "Orders" }],
        ColumnMappings =
        [
            new ColumnMapping { SourceColumn = "Id", TargetColumn = "id" },
            new ColumnMapping { SourceColumn = "Name", TargetColumn = "name" },
        ],
        PrimaryKey = [.. key],
    };

    [Fact]
    public void NoStatedKey_IsFine() => ConfigValidation.ValidatePrimaryKey(Mapping());

    [Fact]
    public void AMappedColumn_IsFine_RegardlessOfCase() => ConfigValidation.ValidatePrimaryKey(Mapping("id"));

    [Fact]
    public void ADuplicate_IsRefused() =>
        Assert.Contains("more than once",
            Assert.Throws<ConfigValidationException>(() => ConfigValidation.ValidatePrimaryKey(Mapping("Id", "ID"))).Message);

    [Fact]
    public void AColumnTheMappingDoesNotCarry_IsRefused() =>
        Assert.Contains("'Missing'",
            Assert.Throws<ConfigValidationException>(() => ConfigValidation.ValidatePrimaryKey(Mapping("Id", "Missing"))).Message);
}
