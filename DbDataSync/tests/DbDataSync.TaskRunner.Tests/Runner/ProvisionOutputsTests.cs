using DbDataKit.Sync.Core.Config;
using DbDataSync.TaskRunner.Runner;
using Xunit;

namespace DbDataSync.TaskRunner.Tests.Runner;

/// <summary>Plan 28c E21 (Sync half, step b): on a runner an apply reports what it did to a mapping as run outputs and writes nothing into the definition.</summary>
public sealed class ProvisionOutputsTests
{
    [Fact]
    public void The_columns_read_back_after_an_apply_are_the_report_a_drain_pass_gives()
    {
        var columns = new List<ProvisionedColumnsReport>();
        var outputs = new RunOutputMappingAfterApply(columns.Add, _ => Assert.Fail("no renames were applied"));

        outputs.TargetColumnsRead("orders-sync", "orders", [new CachedColumn("Id", "int", false, true, true, 1), new CachedColumn("Name", "nvarchar(50)", true, false, false)]);

        var report = Assert.Single(columns);
        Assert.Equal(("orders-sync", "orders"), (report.Replication, report.Mapping));
        Assert.Equal(["Id", "Name"], report.Columns.Select(c => c.Name));
    }

    [Fact]
    public void The_renames_an_apply_ran_are_reported_with_their_column_and_the_definition_stays_as_it_was_published()
    {
        var done = new RenameStep("Nm", "FullName", applied: true);
        var earlier = new RenameStep("Name", "Nm", applied: true);
        var mapping = new TableMappingConfig
        {
            Name = "orders", Sources = [], Targets = [],
            ColumnMappings =
            [
                new ColumnMapping { SourceColumn = "Name", TargetColumn = "FullName", Renames = [earlier, done] },
                new ColumnMapping { SourceColumn = "Id", TargetColumn = "Id" },
            ],
        };
        var renames = new List<AppliedRenamesReport>();
        var outputs = new RunOutputMappingAfterApply(_ => Assert.Fail("no columns were read"), renames.Add);

        outputs.RenamesApplied("orders-sync", mapping, [done]);

        var report = Assert.Single(renames);
        Assert.Equal(("orders-sync", "orders"), (report.Replication, report.Mapping));
        Assert.Equal([new AppliedRename("FullName", "Nm", "FullName")], report.Renames);
    }

    [Fact]
    public void An_apply_with_no_rename_reports_none()
    {
        var outputs = new RunOutputMappingAfterApply(_ => { }, _ => Assert.Fail("nothing to report"));
        outputs.RenamesApplied("orders-sync", new TableMappingConfig { Name = "orders", Sources = [], Targets = [] }, []);
    }
}
