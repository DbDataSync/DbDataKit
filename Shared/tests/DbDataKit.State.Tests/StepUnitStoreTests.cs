namespace DbDataKit.State.Tests;

/// <summary>Plan 28a R03s: steps and units of an attempt chain in the progress store, on every engine.</summary>
public sealed class StepUnitStoreTests : IDisposable
{
    private static readonly ProgressKey Key = new("orders-pkg", "prod", "sync-orders");
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-07T09:00:00Z");
    private const string Chain = "0197f0e0-0000-7000-8000-000000000001";

    private readonly StateEngines _engines = new();
    private readonly StateTestClock _clock = new(Start);

    public void Dispose() => _engines.Dispose();

    private StateDatabase Open(string engine) => _engines.Provision(engine).Open([ProgressModule.Module, RunsModule.Module], StateScope.Standalone, time: _clock);

    [Fact]
    public void The_module_follows_the_conventions() => Assert.Empty(StateConventions.Violations(ProgressModule.Module));

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_step_is_begun_completed_and_reused_by_the_next_attempt(string engine)
    {
        var steps = new StepStore(Open(engine), Key);

        var run = Assert.IsType<StepBegin.Run>(steps.Begin(Chain, "download", "file-1", attempt: 1));
        Assert.Equal(StepState.Started, steps.Get(Chain, "download", "file-1")!.State);
        steps.CompleteValue(Chain, "download", "file-1", 1, "{\"hash\":\"abc\",\"rows\":12}", new Dictionary<string, long> { ["rows"] = 12 });

        var done = steps.Get(Chain, "download", "file-1")!;
        Assert.Equal((StepState.Completed, run.StepId), (done.State, done.Id));
        Assert.Equal(12L, done.Counts["rows"]);
        Assert.NotNull(done.CompletedAt);

        var reuse = Assert.IsType<StepBegin.Reuse>(steps.Begin(Chain, "download", "file-1", attempt: 2));
        Assert.Equal("{\"hash\":\"abc\",\"rows\":12}", reuse.ResultJson);
        Assert.Null(reuse.ResultPayload);
        Assert.Equal(1, steps.Get(Chain, "download", "file-1")!.Attempt);          // reuse does not touch the row

        // a different key, a different name and a different chain are different steps
        Assert.IsType<StepBegin.Run>(steps.Begin(Chain, "download", "file-2", 2));
        Assert.IsType<StepBegin.Run>(steps.Begin(Chain, "parse", "file-1", 2));
        Assert.IsType<StepBegin.Run>(steps.Begin("another-chain", "download", "file-1", 1));
        Assert.IsType<StepBegin.Run>(new StepStore(Open(engine), Key with { Channel = "preview" }).Begin(Chain, "download", "file-1", 1));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void The_completed_steps_of_a_name_are_read_across_chains_newest_first_by_key_and_by_start(string engine)
    {
        var steps = new StepStore(Open(engine), Key);
        foreach (var (chain, key) in new[] { (Chain, "k1"), (Chain, "k2"), ("another-chain", "k3") })
        {
            steps.Begin(chain, "pass-record", key, 1);
            steps.CompleteValue(chain, "pass-record", key, 1, $"\"{key}\"");
            _clock.Advance(TimeSpan.FromSeconds(1));
        }

        steps.Begin(Chain, "pass-record", "unfinished", 1);          // started, never completed: not a result
        steps.Begin(Chain, "other", "k4", 1);
        steps.CompleteValue(Chain, "other", "k4", 1, "4");
        new StepStore(Open(engine), Key with { Task = "another-task" }).Begin(Chain, "pass-record", "k5", 1);

        var all = steps.Recent("pass-record");

        Assert.Equal(["k3", "k2", "k1"], all.Select(s => s.Key));
        Assert.Equal(["\"k3\""], steps.Recent("pass-record", 1).Select(s => s.ResultJson));
        Assert.Equal(["k2"], steps.Recent("pass-record", stepKey: "k2").Select(s => s.Key));
        Assert.Equal(["k2", "k1"], steps.Recent("pass-record", startedBefore: all[0].StartedAt).Select(s => s.Key));
        Assert.Empty(steps.Recent("nothing"));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_different_chain_does_not_reuse_results(string engine)
    {
        var steps = new StepStore(Open(engine), Key);
        steps.Begin(Chain, "download", "k", 1);
        steps.CompleteValue(Chain, "download", "k", 1, "1");

        Assert.IsType<StepBegin.Reuse>(steps.Begin(Chain, "download", "k", 2));
        Assert.IsType<StepBegin.Run>(steps.Begin("0197f0e0-0000-7000-8000-000000000002", "download", "k", 1));
        Assert.Empty(steps.List("0197f0e0-0000-7000-8000-000000000003"));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_started_step_without_completion_is_reported_in_doubt_by_the_next_attempt(string engine)
    {
        var steps = new StepStore(Open(engine), Key);
        steps.Begin(Chain, "charge", "order-17", 1);                       // the run dies here: no complete, no fail

        var doubt = Assert.IsType<StepBegin.InDoubt>(steps.Begin(Chain, "charge", "order-17", 2));
        Assert.Equal((StepState.Started, 1), (doubt.Step.State, doubt.Step.Attempt));
        Assert.Equal(StepState.Started, steps.Get(Chain, "charge", "order-17")!.State);   // reporting changes nothing

        // the same attempt beginning it twice is a mistake, not a doubt
        var again = Assert.Throws<InvalidOperationException>(() => steps.Begin(Chain, "charge", "order-17", 1));
        Assert.Contains("'charge'", again.Message);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_dead_runs_started_steps_are_marked_in_doubt_and_the_caller_takes_one_over(string engine)
    {
        var steps = new StepStore(Open(engine), Key);
        steps.Begin(Chain, "charge", "a", 1);
        steps.Begin(Chain, "charge", "b", 1);
        steps.Begin(Chain, "charge", "c", 1);
        steps.CompleteValue(Chain, "charge", "c", 1, "{}");

        var marked = steps.MarkInDoubt(Chain, attempt: 1);
        Assert.Equal(["a", "b"], marked.Select(s => s.Key).Order().ToArray());
        Assert.All(marked, s => Assert.Equal(StepState.InDoubt, s.State));
        Assert.Equal(StepState.Completed, steps.Get(Chain, "charge", "c")!.State);

        // in doubt even for the same attempt number (the run died; the retry may reuse the number)
        Assert.IsType<StepBegin.InDoubt>(steps.Begin(Chain, "charge", "a", 1));

        // Repeatable: take it over and run it; Check: take it over and complete it with what the check found
        var run = steps.Retake(Chain, "charge", "a", 2);
        Assert.Equal(StepState.Started, steps.Get(Chain, "charge", "a")!.State);
        Assert.Equal(2, steps.Get(Chain, "charge", "a")!.Attempt);
        steps.CompleteValue(Chain, "charge", "a", 2, "{\"found\":true}");
        Assert.IsType<StepBegin.Reuse>(steps.Begin(Chain, "charge", "a", 3));
        Assert.NotEqual(Guid.Empty, run.StepId);

        Assert.Throws<InvalidOperationException>(() => steps.Retake(Chain, "charge", "a", 3));          // completed: not in doubt
        Assert.Throws<InvalidOperationException>(() => steps.Retake(Chain, "charge", "nothing", 3));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void An_operator_resolves_an_in_doubt_step_as_happened_or_not(string engine)
    {
        var steps = new StepStore(Open(engine), Key);
        steps.Begin(Chain, "charge", "a", 1);
        steps.Begin(Chain, "charge", "b", 1);
        steps.MarkInDoubt(Chain);

        Assert.True(steps.Resolve(Chain, "charge", "a", happened: true, resultJson: "{\"ticket\":\"T-9\"}", actor: "alice"));
        var reuse = Assert.IsType<StepBegin.Reuse>(steps.Begin(Chain, "charge", "a", 2));
        Assert.Equal("{\"ticket\":\"T-9\"}", reuse.ResultJson);
        Assert.Contains("alice", steps.Get(Chain, "charge", "a")!.Error);

        Assert.True(steps.Resolve(Chain, "charge", "b", happened: false));
        Assert.Equal(StepState.Failed, steps.Get(Chain, "charge", "b")!.State);
        Assert.IsType<StepBegin.Run>(steps.Begin(Chain, "charge", "b", 2));                    // did not happen: it runs

        Assert.False(steps.Resolve(Chain, "charge", "a", happened: false));                    // settled already
        Assert.Throws<ArgumentException>(() => steps.Resolve(Chain, "charge", "b", happened: true));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_failed_step_is_run_again(string engine)
    {
        var steps = new StepStore(Open(engine), Key);
        var first = Assert.IsType<StepBegin.Run>(steps.Begin(Chain, "call-api", "x", 1));
        Assert.True(steps.Fail(Chain, "call-api", "x", 1, "503 Service Unavailable"));

        var failed = steps.Get(Chain, "call-api", "x")!;
        Assert.Equal((StepState.Failed, "503 Service Unavailable"), (failed.State, failed.Error));
        Assert.False(steps.Fail(Chain, "call-api", "x", 1, "twice"));                         // only a started step fails

        var second = Assert.IsType<StepBegin.Run>(steps.Begin(Chain, "call-api", "x", 2));
        Assert.Equal(first.StepId, second.StepId);
        var again = steps.Get(Chain, "call-api", "x")!;
        Assert.Equal((StepState.Started, 2, null), (again.State, again.Attempt, again.Error));
        steps.CompleteValue(Chain, "call-api", "x", 2, "\"ok\"");
        Assert.IsType<StepBegin.Reuse>(steps.Begin(Chain, "call-api", "x", 3));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_value_over_the_cap_is_refused_naming_the_step(string engine)
    {
        var steps = new StepStore(Open(engine), Key, new StepOptions(MaxResultBytes: 100));
        steps.Begin(Chain, "list-customers", "all", 1);

        var big = "\"" + new string('x', 200) + "\"";
        var ex = Assert.Throws<StepResultTooLargeException>(() => steps.CompleteValue(Chain, "list-customers", "all", 1, big));
        Assert.Contains("'list-customers'", ex.Message);
        Assert.Contains("streaming step", ex.Message);
        Assert.Contains("100", ex.Message);
        Assert.Equal(StepState.Started, steps.Get(Chain, "list-customers", "all")!.State);      // nothing stored

        Assert.Throws<ArgumentException>(() => steps.CompleteValue(Chain, "list-customers", "all", 1, "{not json"));
        steps.CompleteValue(Chain, "list-customers", "all", 1, "[1,2,3]");                      // within the cap

        // the default is 64 KiB
        var defaults = new StepStore(Open(engine), Key);
        defaults.Begin(Chain, "s", "k", 1);
        Assert.Throws<StepResultTooLargeException>(() => defaults.CompleteValue(Chain, "s", "k", 1, "\"" + new string('y', 64 * 1024) + "\""));
        defaults.CompleteValue(Chain, "s", "k", 1, "\"" + new string('y', 64 * 1024 - 2) + "\"");
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_streamed_result_is_complete_only_when_its_write_committed(string engine)
    {
        var database = Open(engine);
        var steps = new StepStore(database, Key);
        var payloads = new PayloadStore(database, chunkSize: 1024);
        var run = Assert.IsType<StepBegin.Run>(steps.Begin(Chain, "download", "f", 1));

        Guid interrupted;
        using (var writer = steps.OpenStream(run, "download", "text/csv"))
        {
            interrupted = writer.Id;
            writer.Write(new byte[3000]);                                  // the process dies before Complete
            var ex = Assert.Throws<InvalidOperationException>(() => steps.CompleteStream(Chain, "download", "f", 1, interrupted));
            Assert.Contains("did not complete", ex.Message);
        }

        Assert.False(payloads.Get(interrupted)!.Complete);
        Assert.Equal(StepState.Started, steps.Get(Chain, "download", "f")!.State);

        // the retry writes again (the dead attempt's step is in doubt for the next attempt)
        Assert.IsType<StepBegin.InDoubt>(steps.Begin(Chain, "download", "f", 2));
        run = steps.Retake(Chain, "download", "f", 2);
        Guid good;
        using (var writer = steps.OpenStream(run, "download", "text/csv"))
        {
            good = writer.Id;
            writer.Write(new byte[2500]);
            writer.Complete();
        }

        steps.CompleteStream(Chain, "download", "f", 2, good, new Dictionary<string, long> { ["bytes"] = 2500 });
        var reuse = Assert.IsType<StepBegin.Reuse>(steps.Begin(Chain, "download", "f", 3));
        Assert.Equal(good, reuse.ResultPayload);
        Assert.Null(reuse.ResultJson);
        Assert.Equal(2500, payloads.Get(good)!.Size);
        Assert.Equal((ProgressModule.Name, run.StepId.ToString("D")), (payloads.Get(good)!.OwnerModule, payloads.Get(good)!.OwnerRow));

        // retention: the step row, then the payloads it owned (the incomplete one included)
        _clock.Advance(TimeSpan.FromDays(40));
        var first = new RetentionJob(database).Run();
        Assert.Equal(1, first.Removed["runner_progress.step_result"]);
        _clock.Advance(TimeSpan.FromHours(2));
        new RetentionJob(database).Run();
        Assert.Null(payloads.Get(good));
        Assert.Null(payloads.Get(interrupted));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_stream_over_its_cap_throws_and_leaves_the_payload_incomplete(string engine)
    {
        var database = Open(engine);
        var steps = new StepStore(database, Key, new StepOptions(MaxStreamBytes: 2000));
        var payloads = new PayloadStore(database, chunkSize: 512);
        var run = Assert.IsType<StepBegin.Run>(steps.Begin(Chain, "download", "big", 1));

        Guid id;
        using (var writer = steps.OpenStream(run, "download"))
        {
            id = writer.Id;
            writer.Write(new byte[1500]);
            var ex = Assert.Throws<PayloadTooLargeException>(() => writer.Write(new byte[600]));
            Assert.Contains("'download'", ex.Message);
            Assert.Contains("2000", ex.Message);
            Assert.Throws<InvalidOperationException>(() => writer.Complete());                  // a refused payload cannot be completed
        }

        Assert.False(payloads.Get(id)!.Complete);
        Assert.Throws<InvalidOperationException>(() => steps.CompleteStream(Chain, "download", "big", 1, id));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_payload_writer_cap_is_exact(string engine)
    {
        var payloads = new PayloadStore(Open(engine), chunkSize: 100);
        using var writer = payloads.OpenWrite(new PayloadOptions(MaxBytes: 250));
        writer.Write(new byte[250]);                                                           // exactly the cap is fine
        writer.Complete();
        Assert.Equal(250, payloads.Get(writer.Id)!.Size);

        using var other = payloads.OpenWrite(new PayloadOptions(MaxBytes: 250));
        Assert.Throws<PayloadTooLargeException>(() => other.Write(new byte[251]));
        Assert.False(payloads.Get(other.Id)!.Complete);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_unit_completed_in_a_chain_is_skipped_by_the_next_attempt_and_a_failed_one_runs_again(string engine)
    {
        var units = new UnitStore(Open(engine), Key);

        Assert.Equal(UnitBegin.Run, units.Begin(Chain, "customer", "acme", 1));
        Assert.Equal(UnitBegin.Run, units.Begin(Chain, "customer", "globex", 1));
        Assert.Equal(UnitBegin.Run, units.Begin(Chain, "customer", "initech", 1));
        units.Complete(Chain, "customer", "acme", 1, new Dictionary<string, long> { ["orders"] = 40 });
        Assert.True(units.Fail(Chain, "customer", "globex", 1, "timeout"));
        // initech: the run died while it was started

        Assert.Equal(UnitBegin.Skip, units.Begin(Chain, "customer", "acme", 2));
        Assert.Equal(UnitBegin.Run, units.Begin(Chain, "customer", "globex", 2));
        Assert.Equal(UnitBegin.Run, units.Begin(Chain, "customer", "initech", 2));
        Assert.Equal(40L, units.Get(Chain, "customer", "acme")!.Counts["orders"]);
        Assert.Equal(1, units.Get(Chain, "customer", "acme")!.Attempt);
        Assert.Equal((UnitState.Started, 2, null), (units.Get(Chain, "customer", "globex")!.State, units.Get(Chain, "customer", "globex")!.Attempt, units.Get(Chain, "customer", "globex")!.Error));

        Assert.Throws<InvalidOperationException>(() => units.Begin(Chain, "customer", "globex", 2));          // the same unit twice in an attempt
        Assert.Throws<InvalidOperationException>(() => units.Complete(Chain, "customer", "acme", 2));         // not started in this attempt

        // another chain and another unit name are separate
        Assert.Equal(UnitBegin.Run, units.Begin("other-chain", "customer", "acme", 1));
        Assert.Equal(UnitBegin.Run, units.Begin(Chain, "region", "acme", 1));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void The_unit_summary_counts_each_state_per_chain(string engine)
    {
        var units = new UnitStore(Open(engine), Key);
        Assert.Equal(new UnitSummary(0, 0, 0, 0, 0), units.Summary(Chain));

        foreach (var k in new[] { "a", "b", "c", "d", "e" }) units.Begin(Chain, "customer", k, 1);
        units.Complete(Chain, "customer", "a", 1);
        units.Complete(Chain, "customer", "b", 1);
        units.Fail(Chain, "customer", "c", 1, "boom");
        Assert.True(units.MarkSkipped(Chain, "customer", "f", 1));
        Assert.False(units.MarkSkipped(Chain, "customer", "a", 1));                            // a unit with a row is left alone
        units.Begin("other-chain", "customer", "z", 1);

        Assert.Equal(new UnitSummary(Total: 6, Done: 2, Skipped: 1, Failed: 1, Running: 2), units.Summary(Chain));
        Assert.Equal(1, units.Summary("other-chain").Running);

        Assert.Equal(2, units.FailRunning(Chain, "the run died"));                              // d and e
        Assert.Equal(new UnitSummary(6, 2, 1, 3, 0), units.Summary(Chain));
        Assert.Equal(6, units.List(Chain).Count);

        // a skipped unit runs in the next attempt
        Assert.Equal(UnitBegin.Run, units.Begin(Chain, "customer", "f", 2));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void Retention_removes_expired_steps_and_units(string engine)
    {
        var database = Open(engine);
        var steps = new StepStore(database, Key);
        var units = new UnitStore(database, Key);
        steps.Begin(Chain, "old", "k", 1);
        steps.CompleteValue(Chain, "old", "k", 1, "1");
        units.Begin(Chain, "customer", "old", 1);
        units.Complete(Chain, "customer", "old", 1);

        _clock.Advance(TimeSpan.FromDays(29));
        steps.Begin(Chain, "new", "k", 1);
        units.Begin(Chain, "customer", "new", 1);
        Assert.Equal((0, 0), RemovedBy(new RetentionJob(database).Run()));

        _clock.Advance(TimeSpan.FromDays(2));                                                   // old ones are 31 days, the new ones 2
        Assert.Equal((1, 1), RemovedBy(new RetentionJob(database).Run()));
        Assert.Equal(["new"], steps.List(Chain).Select(s => s.Name).ToArray());
        Assert.Equal(["new"], units.List(Chain).Select(u => u.Key).ToArray());
    }

    private static (long Steps, long Units) RemovedBy(RetentionResult result) =>
        (result.Removed["runner_progress.step_result"], result.Removed["runner_progress.unit_result"]);
}
