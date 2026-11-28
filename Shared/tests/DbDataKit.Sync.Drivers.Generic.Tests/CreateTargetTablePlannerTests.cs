using DbDataKit.Dialect;
using DbDataKit.Sync.Core.Config;
using DbDataKit.Sync.Drivers.Abstractions;
using DbDataKit.Sync.Core.Sql;

namespace DbDataKit.Sync.Drivers.Generic.Tests;

public sealed class CreateTargetTablePlannerTests
{
    private static readonly TableRef Target = new() { ConnectionName = "c", Database = "db", Schema = "dbo", Table = "Orders" };

    private static CanonicalType Simple(CanonicalKind kind) => CanonicalType.Of(kind);

    [Fact]
    public void AnyUnmappableColumn_PlansUnsupported_NamingTheColumn()
    {
        var columns = new[]
        {
            new ProvisioningColumn("Id", Simple(CanonicalKind.Int32), IsNullable: false, IsPrimaryKey: true),
            new ProvisioningColumn("Location", Simple(CanonicalKind.Unmappable), IsNullable: true, IsPrimaryKey: false),
        };

        var plan = CreateTargetTablePlanner.Plan(FakeCanonicalDialect.Instance, Target, columns);

        Assert.Equal(ProvisioningState.Unsupported, plan.State);
        Assert.Empty(plan.Steps);
        Assert.Contains(plan.Warnings, w => w.Contains("Location"));
    }

    [Fact]
    public void EveryColumnMappable_PlansMissing_WithOneCreateTableStep()
    {
        var columns = new[]
        {
            new ProvisioningColumn("Id", Simple(CanonicalKind.Int32), IsNullable: false, IsPrimaryKey: true),
            new ProvisioningColumn("Name", Simple(CanonicalKind.Boolean), IsNullable: true, IsPrimaryKey: false),
        };

        var plan = CreateTargetTablePlanner.Plan(FakeCanonicalDialect.Instance, Target, columns);

        Assert.Equal(ProvisioningState.Missing, plan.State);
        var step = Assert.Single(plan.Steps);
        Assert.Contains("CREATE TABLE", step.CommandText);
        Assert.Contains("[Id]", step.CommandText);
        Assert.Contains("PRIMARY KEY ([Id])", step.CommandText);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void AFidelityNoteFromTheRenderer_BecomesAPlanWarning_NamingTheColumn()
    {
        var columns = new[]
        {
            new ProvisioningColumn(
                "Description", new CanonicalType(CanonicalKind.String, Unicode: true),
                IsNullable: true, IsPrimaryKey: false),
        };

        var plan = CreateTargetTablePlanner.Plan(FakeCanonicalDialect.Instance, Target, columns);

        Assert.Equal(ProvisioningState.Missing, plan.State);
        Assert.Contains(plan.Warnings, w => w.Contains("Description") && w.Contains("length bound"));
    }

    [Fact]
    public void ASourceNote_SurvivesThroughToThePlansWarnings()
    {
        var columns = new[]
        {
            new ProvisioningColumn(
                "Price", new CanonicalType(CanonicalKind.Decimal, Precision: 19, Scale: 4),
                IsNullable: true, IsPrimaryKey: false, TypeNote: "carries currency semantics"),
        };

        var plan = CreateTargetTablePlanner.Plan(FakeCanonicalDialect.Instance, Target, columns);

        Assert.Contains(plan.Warnings, w => w.Contains("Price") && w.Contains("currency semantics"));
    }
}
