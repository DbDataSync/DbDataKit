using System.Text.Json.Nodes;
using DbDataKit.Monitoring;
using DbDataKit.Runner;
using DbDataKit.Runner.Kit;
using DbDataKit.Runner.Kit.Testing;
using DbDataKit.State;

namespace DbDataKit.Quality.Runner.Tests;

/// <summary>Plan 35 I3a: the shared <c>check</c> task over a package's source, run through the kit's simulator: results recorded under the run's own id through the kit, the block cleared when the data was fixed, the document and its refusals.</summary>
[Collection(nameof(QualityCheckTaskTests))]
public sealed class QualityCheckTaskTests : IDisposable
{
    private readonly QualityData _data = new();

    public QualityCheckTaskTests() => TestCheckJob.Source = new QualityCheckSource(_data.Source([QualityData.NotNullCustomer(), QualityData.Unique()]), ["a note of the source"], id => id == QualityData.Orders ? "orders" : null);

    public void Dispose() => _data.Dispose();

    [RunnerTask(QualityCheckTask.Name)]
    public sealed class TestCheckJob : QualityCheckJob
    {
        public static QualityCheckSource Source = null!;

        protected override Task<QualityCheckSource> SourceAsync(CancellationToken ct) => Task.FromResult(Source);
    }

    private static JsonObject Document(RunSimulator<TestCheckJob> simulator) =>
        (JsonObject)JsonNode.Parse(simulator.StoredSteps().Single(s => s.Name == QualityCheckTask.StepName).ResultJson!)!;

    [Fact]
    public async Task A_check_records_under_its_run_and_a_second_check_after_the_fix_clears_the_block()
    {
        using var first = new RunSimulator<TestCheckJob>(new QualityCheckInput());
        var failed = await first.RunAsync();
        Assert.Equal(RunOutcome.Succeeded, failed.Outcome);                    // a failing check is what the task finds, not a failure of the run
        var store = new QualityStore(first.Database);
        Assert.Equal(failed.RunId.ToString("D"), store.History(QualityData.Orders, "customer_present").Single().RunId);
        Assert.Single(store.BlocksOf(QualityData.Orders));
        var document = Document(first);
        Assert.Equal((2, 1, 1, "orders"), ((int)document["evaluated"]!, (int)document["failed"]!, (int)document["blocking"]!, (string)document["results"]![0]!["name"]!));
        Assert.Equal(["a note of the source"], document["notes"]!.AsArray().Select(n => (string)n!));

        _data.Execute("UPDATE orders SET customer_id = 99 WHERE customer_id IS NULL");
        using var second = new RunSimulator<TestCheckJob>(new QualityCheckInput("orders"), database: first.Database);
        Assert.Equal(RunOutcome.Succeeded, (await second.RunAsync()).Outcome);
        Assert.Empty(store.BlocksOf(QualityData.Orders));
        Assert.Equal("orders", (string)Document(second)["dataset"]!);
    }

    [Fact]
    public async Task An_unknown_dataset_or_a_bad_input_fails_the_run_and_records_nothing()
    {
        using var unknown = new RunSimulator<TestCheckJob>(new QualityCheckInput("customers"));
        var attempt = await unknown.RunAsync();
        Assert.Equal(RunOutcome.Failed, attempt.Outcome);
        Assert.Contains("orders", attempt.Result!.End.Failure!.Message);
        Assert.Empty(new QualityStore(unknown.Database).All());

        using var bad = new RunSimulator<TestCheckJob>(new QualityCheckInput(" orders"));
        Assert.Equal(RunOutcome.Failed, (await bad.RunAsync()).Outcome);
    }

    [Fact]
    public async Task A_dry_run_says_what_it_would_evaluate_and_records_nothing()
    {
        using var dry = new RunSimulator<TestCheckJob>(new QualityCheckInput(), dryRun: true);
        Assert.Equal(RunOutcome.Succeeded, (await dry.RunAsync()).Outcome);
        Assert.Empty(new QualityStore(dry.Database).All());
        Assert.Equal(0, _data.OpenCount);
    }

    [Fact]
    public void The_declaration_is_by_hand_with_the_packages_slots_and_a_document_too_large_keeps_its_counts()
    {
        var task = QualityCheckTask.Declare([new ConnectionSlot("warehouse", "postgres")]);
        Assert.Equal(("check", false, TriggerKind.Manual), (task.Name, task.Schedule.HasSchedule, task.Triggers.Single().Kind));
        Assert.Equal(["check-result"], task.StepList.Select(s => s.Name));

        var check = QualityData.NotNullCustomer();
        var evaluation = new CheckEvaluation(check.Id, EvaluationStatus.Fail, 1, 0, [.. Enumerable.Range(0, 10).Select(i => new string('k', 400) + i)], null, new string('m', 400), null, DateTimeOffset.UnixEpoch);
        var report = new QualityRunReport([new DatasetRunReport(QualityData.Orders, [.. Enumerable.Range(0, 100).Select(_ => new CheckRunReport(check, evaluation, true))])]);
        var document = QualityCheckTask.Document(report, null, []);
        Assert.True((bool)document["truncated"]!);
        Assert.Equal(100, (int)document["failed"]!);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(document.ToJsonString()) <= QualityCheckTask.MaxDocumentBytes);
    }
}
