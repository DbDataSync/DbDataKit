using DbDataKit.Monitoring;

namespace DbDataKit.Runner.Kit.Testing.Tests;

// What a job reports with run.Lineage (plan 24a UC13: a runtime's writes are what the Runner tells the serving groups changed) is what a test of the job can read from the simulator.

public sealed record LoadTablesInput(string[] Written);

[RunnerTask("load-tables")]
public sealed class LoadTables : RunnerJob<LoadTablesInput, NoProgress, NoSlots>
{
    protected override Task RunAsync(CancellationToken ct)
    {
        Run.Lineage(["sqlserver://src/sales.dbo.orders"], Input.Written);
        return Task.CompletedTask;
    }
}

public class LineageTests
{
    [Fact]
    public async Task What_a_job_reports_as_its_lineage_is_the_latest_attempts_in_the_order_it_said_it()
    {
        using var sim = new RunSimulator<LoadTables>(new LoadTablesInput(["postgres://dwh/dwh.public.orders", "postgres://dwh/dwh.public.lines"]));

        var attempt = await sim.RunAsync();

        Assert.Equal(RunOutcome.Succeeded, attempt.Outcome);
        var (reads, writes) = Assert.Single(sim.Lineage);
        Assert.Equal(["sqlserver://src/sales.dbo.orders"], reads);
        Assert.Equal(["postgres://dwh/dwh.public.orders", "postgres://dwh/dwh.public.lines"], writes);
    }
}
