using DbDataKit.State;
using DbDataKit.State.Tests;

namespace DbDataKit.Quality.Runner.Tests;

/// <summary>Plan 23a FQ08: the service the command and the runner task share, on every state engine, over a DuckDB data file through its read-only connection.</summary>
public sealed class QualityRunServiceTests : IDisposable
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-08T12:00:00Z");

    private readonly StateEngines _engines = new();
    private readonly QualityData _data = new();

    public static TheoryData<string> Engines => StateEngines.Engines;

    public void Dispose()
    {
        _data.Dispose();
        _engines.Dispose();
    }

    private (QualityStore Store, StateTestClock Clock) Open(string engine)
    {
        var clock = new StateTestClock(Start);
        return (new QualityStore(_engines.Provision(engine).Open([QualityModule.Module], time: clock)), clock);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_run_records_a_result_per_check_with_its_sample_keys_and_sets_the_block(string engine)
    {
        var (store, clock) = Open(engine);
        var service = new QualityRunService(_data.Source([QualityData.NotNullCustomer(), QualityData.Unique(), QualityData.NotNullCustomer("customer_warn", CheckSeverity.Warn)]), store, time: clock);

        var report = await service.RunAsync(null, runId: "run-1");

        Assert.Equal((3, 2, 0, 1), (report.Evaluated, report.Failed, report.Errors, report.Blocking));
        var statuses = store.StatusesOf(QualityData.Orders).ToDictionary(s => s.CheckId);
        Assert.Equal(CheckOutcome.Fail, statuses["customer_present"].Outcome);
        Assert.True(statuses["customer_present"].Blocking);
        Assert.Equal(CheckOutcome.Pass, statuses["id_unique"].Outcome);
        Assert.False(statuses["customer_warn"].Blocking);              // a warn check never blocks
        var failed = Assert.Single(store.History(QualityData.Orders, "customer_present"));
        Assert.Equal((2m, 0m, "run-1"), (failed.Measured, failed.Threshold, failed.RunId));
        Assert.Equal(["id=2", "id=4"], failed.SampleKeys);
        Assert.Equal([("customer_present", true)], store.BlocksOf(QualityData.Orders).Select(b => (b.CheckId, b.Blocking)));
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_block_clears_when_the_data_is_fixed_and_the_check_runs_again(string engine)
    {
        var (store, clock) = Open(engine);
        var service = new QualityRunService(_data.Source([QualityData.NotNullCustomer()]), store, time: clock);
        await service.RunAsync(null);
        Assert.Single(store.BlocksOf(QualityData.Orders));

        _data.Execute("UPDATE orders SET customer_id = 99 WHERE customer_id IS NULL");
        clock.Advance(TimeSpan.FromMinutes(5));
        var report = await service.RunAsync(QualityData.Orders);

        Assert.True(report.AllPassed);
        Assert.Empty(store.BlocksOf(QualityData.Orders));
        Assert.Equal(2, store.History(QualityData.Orders, "customer_present").Count);
        Assert.Equal(CheckOutcome.Pass, store.Status(QualityData.Orders, "customer_present")!.Outcome);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_connection_that_is_down_is_recorded_as_an_error_that_blocks_only_when_the_check_says_so(string engine)
    {
        var (store, clock) = Open(engine);
        var service = new QualityRunService(_data.Source([QualityData.NotNullCustomer("alerts"), QualityData.NotNullCustomer("blocks", onError: CheckOnError.Block)], down: true), store, time: clock);

        var report = await service.RunAsync("orders");

        Assert.Equal((0, 2), (report.Failed, report.Errors));
        Assert.StartsWith("The connection could not be opened", report.Checks.First().Evaluation.Message);
        Assert.Equal(CheckOutcome.Error, store.Status(QualityData.Orders, "alerts")!.Outcome);
        Assert.False(store.Status(QualityData.Orders, "alerts")!.Blocking);        // QF10: an error alerts, it does not block
        Assert.True(store.Status(QualityData.Orders, "blocks")!.Blocking);         // unless on_error says block
        Assert.Equal("The connection could not be opened", store.History(QualityData.Orders, "alerts").Single().Message![..34]);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_row_count_check_compares_with_the_previous_successful_evaluation_and_the_first_only_records(string engine)
    {
        var (store, clock) = Open(engine);
        var service = new QualityRunService(_data.Source([QualityData.RowCount()]), store, time: clock);

        var first = await service.RunAsync(null);
        Assert.Equal(EvaluationStatus.Pass, first.Checks.Single().Evaluation.Status);
        Assert.Null(first.Checks.Single().Evaluation.Measured);
        Assert.Equal(5m, store.Baseline(QualityData.Orders, "rows"));

        _data.Execute("INSERT INTO orders VALUES (6, 1, 'open', TIMESTAMP '2026-11-08 10:00:00'), (7, 1, 'open', TIMESTAMP '2026-11-08 10:00:00'), (8, 1, 'open', TIMESTAMP '2026-11-08 10:00:00'), (9, 1, 'open', TIMESTAMP '2026-11-08 10:00:00')");
        clock.Advance(TimeSpan.FromHours(1));
        var second = await service.RunAsync(null);
        var grew = second.Checks.Single();
        Assert.Equal((EvaluationStatus.Fail, 80m, 50m), (grew.Evaluation.Status, grew.Evaluation.Measured, grew.Evaluation.Threshold));
        Assert.Equal(9m, store.Baseline(QualityData.Orders, "rows"));

        // an evaluation that could not run is no baseline: the next one still compares with 9
        clock.Advance(TimeSpan.FromHours(1));
        await new QualityRunService(_data.Source([QualityData.RowCount()], down: true), store, time: clock).RunAsync(null);
        Assert.Equal(9m, store.Baseline(QualityData.Orders, "rows"));
        clock.Advance(TimeSpan.FromHours(1));
        var third = await service.RunAsync(null);
        Assert.Equal((EvaluationStatus.Pass, 0m), (third.Checks.Single().Evaluation.Status, third.Checks.Single().Evaluation.Measured));
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_dataset_is_found_by_identity_or_by_name_and_an_unknown_one_is_refused_naming_it(string engine)
    {
        var (store, clock) = Open(engine);
        var service = new QualityRunService(_data.Source([QualityData.Unique()]), store, time: clock);

        Assert.Single((await service.RunAsync("orders")).Datasets);
        Assert.Single((await service.RunAsync(QualityData.Orders)).Datasets);
        var ex = await Assert.ThrowsAsync<UnknownDatasetException>(() => service.RunAsync("nowhere"));
        Assert.Contains("'nowhere'", ex.Message);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_run_changes_no_data_and_leaves_the_file_free_to_write(string engine)
    {
        var (store, clock) = Open(engine);
        var before = _data.Count();

        await new QualityRunService(_data.Source([QualityData.NotNullCustomer(), QualityData.Unique(), QualityData.RowCount()]), store, time: clock).RunAsync(null);

        Assert.Equal(before, _data.Count());
        // the read-only connection is gone: a writer gets in at once
        _data.Execute("INSERT INTO orders VALUES (6, 1, 'open', TIMESTAMP '2026-11-08 10:00:00')");
        Assert.Equal(before + 1, _data.Count());
        Assert.Equal(1, _data.OpenCount);       // one connection for the three checks of the dataset
    }
}
