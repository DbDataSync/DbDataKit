using DbDataKit.Sync.Core.Config;
using DbDataKit.Sync.Core.Sql;
using DbDataKit.Sync.Drivers.Abstractions;

namespace DbDataKit.Sync.Drivers.Generic.Tests;

/// <summary>The key rules the SCD Type 2 and KeyReconcileDelete writers refuse a mapping by, which the offline check of a sync project calls too.</summary>
public sealed class WriterKeyRuleTests
{
    private static readonly TableRef Target = new() { ConnectionName = "t", Database = "db", Schema = "dbo", Table = "Orders" };

    private static readonly List<ColumnMapping> Mappings =
    [
        new() { SourceColumn = "Id", TargetColumn = "Id" },
        new() { SourceColumn = "Name", TargetColumn = "Name" },
    ];

    private static TargetShape Shape(bool keyed) =>
        TargetShape.FromCachedColumns(MsSqlDialect.Instance, "orders",
            [new("Id", "int", false, keyed, false), new("Name", "nvarchar(50)", true, false, false)], Target, Mappings);

    [Fact]
    public void The_natural_key_is_the_stated_columns()
    {
        Assert.Equal(["Id", "Region"], Scd2Writer.RequireNaturalKey(new Dictionary<string, string> { [Scd2Writer.NaturalKeyOption] = " Id , Region " }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void A_pass_with_no_natural_key_is_refused_naming_the_setting(string? stated)
    {
        var options = stated is null ? new Dictionary<string, string>() : new Dictionary<string, string> { [Scd2Writer.NaturalKeyOption] = stated };

        var ex = Assert.Throws<InvalidOperationException>(() => Scd2Writer.RequireNaturalKey(options));

        Assert.Contains("'naturalKey'", ex.Message);
        Assert.Contains("Pipeline tab", ex.Message);
    }

    [Fact]
    public void The_sweep_anti_joins_on_the_targets_primary_key()
    {
        Assert.Equal(["Id"], KeyReconcileDeleteWriter.RequireKey(Shape(keyed: true), "orders"));
    }

    [Fact]
    public void A_target_with_no_primary_key_is_refused_by_the_sweep()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => KeyReconcileDeleteWriter.RequireKey(Shape(keyed: false), "orders"));

        Assert.Contains("Table mapping 'orders' target has no cached primary key", ex.Message);
        Assert.Contains("Refresh metadata", ex.Message);
    }
}
