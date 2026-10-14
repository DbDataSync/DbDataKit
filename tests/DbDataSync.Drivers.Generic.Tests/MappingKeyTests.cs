using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Abstractions;
using Xunit;

namespace DbDataSync.Drivers.Generic.Tests;

/// <summary>Phase 201M: a mapping's stated primary key replaces the source's own answer, and a key column
/// is never nullable — so a source that reports no key and every column nullable still yields a table the
/// engine will create.</summary>
public sealed class MappingKeyTests
{
    private static ColumnMetadata Column(string name, bool key = false, bool nullable = true) =>
        new(name, "int", nullable, key, IsIdentity: false);

    private static ColumnMapping Map(string name) => new() { SourceColumn = name, TargetColumn = name };

    [Fact]
    public void No_stated_key_leaves_the_columns_untouched()
    {
        var columns = new[] { Column("Id", key: true, nullable: false), Column("Name") };

        Assert.Same(columns, columns.WithKey([]));
    }

    [Fact]
    public void A_stated_key_replaces_the_reported_one_and_is_not_nullable()
    {
        var columns = new[] { Column("Id", key: true, nullable: false), Column("TenantId"), Column("Name") };

        var keyed = columns.WithKey(["tenantid"]);

        Assert.Equal(["TenantId"], keyed.Where(c => c.IsPrimaryKey).Select(c => c.Name));
        Assert.False(keyed.Single(c => c.Name == "TenantId").IsNullable);
        Assert.True(keyed.Single(c => c.Name == "Name").IsNullable);
    }

    [Fact]
    public void Cached_columns_take_the_stated_key_too()
    {
        IReadOnlyList<CachedColumn> cached =
            [new("Id", "int", true, false, false), new("Name", "text", true, false, false)];

        var keyed = cached.WithKey(["Id"]);

        Assert.True(keyed[0].IsPrimaryKey);
        Assert.False(keyed[0].IsNullable);
        Assert.False(keyed[1].IsPrimaryKey);
    }

    [Fact]
    public void A_keyless_all_nullable_source_provisions_a_primary_key_that_can_be_created()
    {
        var source = new[] { Column("Id"), Column("Name") }; // what a proxy or view reports

        var (columns, _) = ProvisioningColumnBuilder.Build(
            AnsiDialect.Instance, source.WithKey(["Id"]), [Map("Id"), Map("Name")]);

        var plan = CreateTargetTablePlanner.Plan(AnsiDialect.Instance, new TableRef { ConnectionName = "c", Database = "db", Schema = "dbo", Table = "T" }, columns);
        var sql = plan.Steps.Single().CommandText;

        Assert.Matches("\"Id\" \\w+ NOT NULL", sql);
        Assert.Contains("PRIMARY KEY (\"Id\")", sql);
        Assert.Matches("\"Name\" [^\n]* NULL", sql);
    }

    [Fact]
    public void A_stated_key_derives_the_scd2_natural_key()
    {
        var derived = NaturalKeyDerivation.Derive(
            new[] { Column("A"), Column("B") }.WithKey(["B"]), [Map("A"), Map("B")]);

        Assert.Equal(["B"], derived);
    }
}
