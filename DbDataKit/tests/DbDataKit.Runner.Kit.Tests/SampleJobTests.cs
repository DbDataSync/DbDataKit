using System.Security.Cryptography;
using System.Text;
using DbDataKit.Monitoring;
using DbDataKit.Runner.Kit.Tests.Samples;
using DbDataKit.State;

namespace DbDataKit.Runner.Kit.Tests;

/// <summary>Plan 28a R03a: the four sample jobs of plan 28b section 2 as real jobs, on every engine, and what they show of steps, units, checkpoints and streams.</summary>
public sealed class SampleJobTests
{
    private static readonly DateTimeOffset T1 = DateTimeOffset.Parse("2026-11-01T00:00:00Z");
    private static readonly DateTimeOffset T2 = DateTimeOffset.Parse("2026-11-02T00:00:00Z");
    private static readonly DateTimeOffset T3 = DateTimeOffset.Parse("2026-11-03T00:00:00Z");

    private static string NewChain() => Guid.NewGuid().ToString("D");

    // ---- 2.1 incremental pull

    [Theory]
    [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
    public async Task The_incremental_pull_moves_its_checkpoint_with_the_run_and_the_next_run_starts_from_it(string engine)
    {
        using var h = new Harness(engine);
        h.World.OrderPages.Add(new Page([new Row("1", 1), new Row("2", 2)], T1));
        h.World.OrderPages.Add(new Page([new Row("3", 3)], T2));

        var first = await h.Run<Orders>();

        Assert.Equal(RunOutcome.Succeeded, first.Outcome);
        Assert.Equal(3, first.Counters["rows"]);
        Assert.Equal(3, h.World.Warehouse.Count);
        var stored = h.StoredRun(first.RunId).Run;
        Assert.Equal((RunOutcome.Succeeded, 3L), (stored.Outcome, stored.Rows));
        var checkpoint = h.ProgressOf("orders").GetCheckpoint("orders_modified")!;
        Assert.Equal(T2, checkpoint.Parts["value"].AsTime);
        Assert.Equal(1, checkpoint.Version);
        Assert.Equal("orders up to 2026-11-02 00:00:00Z", first.LastStatus);

        h.World.OrderPages.Add(new Page([new Row("4", 4)], T3));
        var second = await h.Run<Orders>(chain: NewChain());

        Assert.Equal(1, second.Counters["rows"]);                                       // only what is newer than the checkpoint
        Assert.Contains(T2, h.World.OrdersAskedSince);
        Assert.Equal(T3, h.ProgressOf("orders").GetCheckpoint("orders_modified")!.Parts["value"].AsTime);
        Assert.Equal(2, h.ProgressOf("orders").History("orders_modified").Count);
    }

    [Theory]
    [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
    public async Task A_failed_run_leaves_the_checkpoint_where_it_was(string engine)
    {
        using var h = new Harness(engine);
        h.World.OrderPages.Add(new Page([new Row("1", 1)], T1));
        h.World.OrderPages.Add(new Page([new Row("2", 2)], T2));
        h.World.OrderPagesFailAfter = 1;                                                // the second page never arrives

        var failed = await h.Run<Orders>();

        Assert.Equal(RunOutcome.Failed, failed.Outcome);
        Assert.Equal(TaskFailureKind.Temporary, failed.End.Failure!.Kind);              // an I/O failure is worth a retry
        Assert.True(failed.End.Failure.Retryable);
        Assert.Null(h.ProgressOf("orders").GetCheckpoint("orders_modified"));
        var stored = h.StoredRun(failed.RunId).Run;
        Assert.Equal(RunOutcome.Failed, stored.Outcome);
        Assert.StartsWith("Temporary", stored.Failure!.Kind, StringComparison.Ordinal);

        h.World.OrderPagesFailAfter = int.MaxValue;
        var retry = await h.Run<Orders>(attempt: 2);
        Assert.Equal(RunOutcome.Succeeded, retry.Outcome);
        Assert.Equal(T2, h.ProgressOf("orders").GetCheckpoint("orders_modified")!.Parts["value"].AsTime);
    }

    [Theory]
    [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
    public async Task A_member_set_but_not_saved_is_not_stored_when_the_run_fails_and_a_commit_point_is_kept(string engine)
    {
        using var h = new Harness(engine);
        h.World.FailOnDay = 3;                                                          // days 1 and 2 are committed with SaveProgressAsync, day 3 is set and then the run throws

        var failed = await h.Run<Backfill>();

        Assert.Equal(RunOutcome.Failed, failed.Outcome);
        var store = h.ProgressOf("backfill");
        Assert.Equal(2, store.GetCheckpoint("last_day")!.Parts["value"].AsInt);          // a crash does not lose the days before the failure
        Assert.Equal("token-2", store.GetState("token")!.AsText);
        Assert.Equal(2, store.GetCheckpoint("last_day")!.Version);

        h.World.FailOnDay = -1;
        var retry = await h.Run<Backfill>(attempt: 2);
        Assert.Equal(RunOutcome.Succeeded, retry.Outcome);
        Assert.Equal(3, store.GetCheckpoint("last_day")!.Parts["value"].AsInt);
        Assert.Equal("token-3", store.GetState("token")!.AsText);
    }

    [Theory]
    [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
    public async Task A_checkpoint_moved_by_another_run_stops_this_one_at_the_commit_point(string engine)
    {
        using var h = new Harness(engine);
        h.World.AfterDay = day =>
        {
            if (day == 1)                                                               // another run (or an administrator) moves the checkpoint
            {
                var store = h.ProgressOf("backfill");
                var current = store.GetCheckpoint("last_day")!;
                Assert.True(store.Advance("last_day", new Dictionary<string, ProgressValue> { ["value"] = ProgressValue.OfInt(1) }, current.Version, actor: "admin"));
            }

            return Task.CompletedTask;
        };

        var stopped = await h.Run<Backfill>();

        Assert.Equal(RunOutcome.Stopped, stopped.Outcome);                              // ProgressConflictException: the runner marks the run Stopped
        Assert.Contains("moved", stopped.End.Failure!.Message, StringComparison.Ordinal);
        Assert.Equal(RunOutcome.Stopped, h.StoredRun(stopped.RunId).Run.Outcome);
        Assert.Equal(2, h.ProgressOf("backfill").GetCheckpoint("last_day")!.Version);    // the run's day 1 and the administrator's write; day 2 was refused
    }

    [Theory]
    [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
    public async Task A_conflict_at_the_final_commit_stops_the_run_and_writes_nothing(string engine)
    {
        using var h = new Harness(engine);
        h.World.OrderPages.Add(new Page([new Row("1", 1)], T1));
        h.World.OnOrderPage = () =>
        {
            var store = h.ProgressOf("orders");
            Assert.True(store.Advance("orders_modified", new Dictionary<string, ProgressValue> { ["value"] = ProgressValue.OfTime(T3) }, 0, actor: "admin"));
            return Task.CompletedTask;
        };

        var result = await h.Run<Orders>();

        Assert.Equal(RunOutcome.Stopped, result.Outcome);
        Assert.Equal(T3, h.ProgressOf("orders").GetCheckpoint("orders_modified")!.Parts["value"].AsTime);   // the administrator's value stands
        Assert.Equal(RunOutcome.Stopped, h.StoredRun(result.RunId).Run.Outcome);
    }

    // ---- 2.2 daily drop: a streamed download and a recovery check

    private static byte[] Csv => Encoding.UTF8.GetBytes("a,1\nb,2\nc,3\n");

    private static readonly DateOnly Day = new(2026, 11, 7);

    [Theory]
    [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
    public async Task A_step_body_runs_once_across_a_crash_and_its_retry_the_check_finds_the_result(string engine)
    {
        using var h = new Harness(engine);
        h.World.Sftp["/drop/2026-11-07.csv"] = Csv;
        var input = new DailyDropInput(Day);

        // attempt 1: the archive step's body ran, and the process died before its result was stored
        await Assert.ThrowsAsync<SimulatedCrashException>(() => h.Run<DailyDrop>(input, hooks: Harness.CrashAfterStep("archive")));
        Assert.Equal(1, h.World.ArchiveBodies);
        Assert.Equal(StepState.Started, h.StepsOf("daily-drop").Get(Harness.Chain, "archive", "2026-11-07")!.State);
        Assert.Equal(StepState.Completed, h.StepsOf("daily-drop").Get(Harness.Chain, "download", "2026-11-07")!.State);
        Assert.Equal(RunOutcome.Running, Assert.Single(h.Runs.Read(new RunQuery("runner"))).Run.Outcome);     // a dead process records nothing

        // attempt 2: the download is reused (the file is gone from the server), the archive is in doubt and its check finds the receipt
        var retry = await h.Run<DailyDrop>(input, attempt: 2);

        Assert.Equal(RunOutcome.Succeeded, retry.Outcome);
        Assert.Equal(1, h.World.ArchiveBodies);                                         // the body ran once
        Assert.Equal(1, h.World.Downloads);
        Assert.Equal((StepStatus.Reused, StepStatus.Recovered), (Status(retry, "download"), Status(retry, "archive")));
        var archive = h.StepsOf("daily-drop").Get(Harness.Chain, "archive", "2026-11-07")!;
        Assert.Equal(StepState.Completed, archive.State);
        Assert.Equal(new ArchiveReceipt("archive://2026-11-07", 12), Harness.Json<ArchiveReceipt>(archive.ResultJson));
        Assert.Equal(3, retry.Counters["rows"]);                                        // the code between the steps ran again from the stored bytes
        Assert.Equal("archived archive://2026-11-07", retry.LastStatus);
    }

    [Theory]
    [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
    public async Task A_check_that_finds_nothing_runs_the_body_again(string engine)
    {
        using var h = new Harness(engine);
        h.World.Sftp["/drop/2026-11-07.csv"] = Csv;
        var input = new DailyDropInput(Day);
        await Assert.ThrowsAsync<SimulatedCrashException>(() => h.Run<DailyDrop>(input, hooks: Harness.CrashAfterStep("archive")));
        h.World.Archived.Clear();                                                       // the archive did not keep it after all

        var retry = await h.Run<DailyDrop>(input, attempt: 2);

        Assert.Equal(RunOutcome.Succeeded, retry.Outcome);
        Assert.Equal(2, h.World.ArchiveBodies);
        Assert.Equal(StepStatus.Ran, Status(retry, "archive"));
    }

    [Theory]
    [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
    public async Task A_completed_chain_answers_every_step_from_memory(string engine)
    {
        using var h = new Harness(engine);
        h.World.Sftp["/drop/2026-11-07.csv"] = Csv;
        var input = new DailyDropInput(Day);
        var first = await h.Run<DailyDrop>(input);
        Assert.Equal((StepStatus.Ran, StepStatus.Ran), (Status(first, "download"), Status(first, "archive")));

        var again = await h.Run<DailyDrop>(input, attempt: 2);

        Assert.Equal((StepStatus.Reused, StepStatus.Reused), (Status(again, "download"), Status(again, "archive")));
        Assert.Equal((1, 1), (h.World.Downloads, h.World.ArchiveBodies));
    }

    private static StepStatus Status(RunResult result, string step) => result.Steps.Single(s => s.Name == step).Status;

    // ---- 2.3 fan-out over customers

    private static void AddCustomers(World world, int n)
    {
        for (var i = 1; i <= n; i++) world.Customers.Add(new Customer($"c{i}", $"Customer {i}"));
    }

    [Theory]
    [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
    public async Task A_unit_finished_in_one_attempt_is_skipped_in_the_next_and_per_key_progress_is_typed(string engine)
    {
        using var h = new Harness(engine);
        AddCustomers(h.World, 6);
        h.World.FailingCustomers["c3"] = 1;                                             // fails in the first attempt only

        var first = await h.Run<Customers>();

        Assert.Equal(RunOutcome.CompletedWithFailures, first.Outcome);
        Assert.Equal((1L, "c3"), (first.End.FailedItems, first.End.FirstFailedItem));
        var summary = h.UnitsOf("customers").Summary(Harness.Chain);
        Assert.Equal((6, 5, 1), (summary.Total, summary.Done, summary.Failed));
        Assert.Equal(10, first.Counters["rows"]);                                       // five customers, two rows each
        Assert.Equal(10, first.Units.Where(u => u.State == UnitState.Completed).Sum(u => u.Counts["rows"]));   // counted against each unit as well as the run
        var progress = h.ProgressOf("customers");
        Assert.Equal(["c1", "c2", "c4", "c5", "c6"], progress.ItemKeys("customer"));    // the failed unit has no checkpoint
        Assert.Equal(h.World.CustomerNewest, progress.GetCheckpoint("customer", "c1")!.Parts["modified"].AsTime);
        Assert.Equal("after-c1-2", progress.GetState("customer_cursor", "c1")!.AsText);
        Assert.Equal(RunOutcome.CompletedWithFailures, h.StoredRun(first.RunId).Run.Outcome);

        var retry = await h.Run<Customers>(attempt: 2);

        Assert.Equal(RunOutcome.Succeeded, retry.Outcome);
        Assert.Equal(1, retry.Units.Count(u => u.State == UnitState.Completed));
        Assert.Equal(5, retry.Units.Count(u => u.State == "reused"));
        Assert.All(["c1", "c2", "c4", "c5", "c6"], c => Assert.Equal(1, h.World.CustomerRuns[c]));      // finished in attempt 1: not run again
        Assert.Equal(2, h.World.CustomerRuns["c3"]);
        Assert.Equal(6, progress.ItemKeys("customer").Count);

        // the next scheduled run is a new chain: every unit runs, from its own checkpoint
        var next = await h.Run<Customers>(chain: NewChain());
        Assert.Equal(RunOutcome.Succeeded, next.Outcome);
        Assert.Equal(h.World.CustomerNewest, h.World.CustomerSince["c2"]);
    }

    [Theory]
    [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
    public async Task Units_run_in_parallel_but_not_beyond_the_limit_of_the_environment(string engine)
    {
        using var h = new Harness(engine);
        var running = 0;
        var most = 0;
        var env = h.Environment(maxParallelism: 2);
        var result = await JobRunner.RunAsync<ParallelProbe>(env, Harness.Chain, 1, new ProbeInput(() =>
        {
            var now = Interlocked.Increment(ref running);
            lock (h) most = Math.Max(most, now);
            return () => Interlocked.Decrement(ref running);
        }), CancellationToken.None);

        Assert.Equal(RunOutcome.Succeeded, result.Outcome);
        Assert.InRange(most, 1, 2);
    }

    // ---- 2.4 invoices: inbox, dead letters, work that is not rows

    [Theory]
    [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
    public async Task The_inbox_job_sends_letters_once_and_makes_dead_letters_of_what_it_refuses(string engine)
    {
        using var h = new Harness(engine);
        h.Receive("invoices", "billing", new Invoice("inv1", 100));
        h.Receive("invoices", "billing", new Invoice("inv2", -5));                      // invalid: the job rejects it
        h.Receive("invoices", "billing", Encoding.UTF8.GetBytes("this is not json"));  // not an invoice at all: a dead letter, not a crash
        h.Receive("invoices", "billing", new Invoice("inv3", 30));

        var result = await h.Run<Invoices>();

        Assert.Equal(RunOutcome.CompletedWithFailures, result.Outcome);                 // a record was rejected
        Assert.Equal(2, result.End.FailedItems);
        Assert.Equal(2, result.Counters["letters"]);
        Assert.Equal(2, h.World.LettersSent);
        Assert.Equal(["inv1", "inv3"], h.StepsOf("invoices").List(Harness.Chain).Select(s => s.Key).Order().ToArray());
        Assert.Equal(0, h.ProgressOf("invoices").InboxWaiting());
        var letters = h.ProgressOf("invoices").DeadLetters();
        Assert.Equal(2, letters.Count);
        Assert.Contains(letters, l => l.Error.Contains("amount of -5", StringComparison.Ordinal));
        Assert.Contains(letters, l => l.Error.Contains("not a Invoice", StringComparison.Ordinal));
        Assert.All(letters, l => Assert.NotNull(l.BodyPayload));
        var body = await new PayloadStore(h.Database).OpenRead(letters.First(l => l.Error.Contains("-5", StringComparison.Ordinal)).BodyPayload!.Value).ReadAllAsync();
        Assert.Contains("inv2", Encoding.UTF8.GetString(body), StringComparison.Ordinal);

        var again = await h.Run<Invoices>(attempt: 2);                                  // nothing is waiting; nothing is sent twice
        Assert.Equal(RunOutcome.Succeeded, again.Outcome);
        Assert.Equal(2, h.World.LettersSent);
    }

    // ---- streams

    [Theory]
    [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
    public async Task A_result_of_twenty_mebibytes_is_streamed_in_and_out_with_its_hash_and_no_buffer_that_size(string engine)
    {
        using var h = new Harness(engine);
        const long size = 20L * 1024 * 1024;

        var result = await h.Run<Big>(new BigInput(size));

        Assert.Equal(RunOutcome.Succeeded, result.Outcome);
        Assert.Equal(size, result.Counters["bytes"]);
        var step = h.StepsOf("big").Get(Harness.Chain, "make", "big")!;
        Assert.Equal(StepState.Completed, step.State);
        Assert.Null(step.ResultJson);
        var info = new PayloadStore(h.Database).Get(step.ResultPayload!.Value)!;
        Assert.Equal((size, true, "application/octet-stream"), (info.Size, info.Complete, info.MediaType));

        // the bytes that were written, hashed as they were written
        using var expected = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (long written = 0; written < size; written += Big.Block.Length) expected.AppendData(Big.Block, 0, (int)Math.Min(Big.Block.Length, size - written));
        Assert.Equal(Convert.ToHexString(expected.GetHashAndReset()), info.Sha256!.ToUpperInvariant());

        // read back through the ref the job got: a stream that returns at most one chunk per read, however large the caller's buffer
        var file = h.World.LastPayload!;
        Assert.Equal((size, info.Sha256), (file.Size, file.Sha256));
        using var seen = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[4 * 1024 * 1024];
        long total = 0;
        var biggestRead = 0;
        var reads = 0;
        await using (var stream = file.OpenRead())
        {
            int n;
            while ((n = await stream.ReadAsync(buffer)) > 0)
            {
                seen.AppendData(buffer, 0, n);
                total += n;
                biggestRead = Math.Max(biggestRead, n);
                reads++;
            }
        }

        Assert.Equal(size, total);
        Assert.True(biggestRead <= PayloadStore.DefaultChunkSize, $"a read returned {biggestRead} bytes; one chunk is {PayloadStore.DefaultChunkSize}");
        Assert.True(reads >= size / PayloadStore.DefaultChunkSize);                     // 20 chunks or more: never the whole result at once
        Assert.Equal(info.Sha256!.ToUpperInvariant(), Convert.ToHexString(seen.GetHashAndReset()));

        // and refused as a whole over the cap
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => file.ReadAllBytesAsync());
        Assert.Contains("read it as a stream", ex.Message, StringComparison.Ordinal);
    }

    // ---- failures, recovery, the rest of the surface

    private static TroubleInput Mode(string mode) => new(mode);

    [Theory]
    [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
    public async Task Repeatable_runs_the_body_again_after_a_crash(string engine)
    {
        using var h = new Harness(engine);
        await Assert.ThrowsAsync<SimulatedCrashException>(() => h.Run<Trouble>(Mode("repeatable"), hooks: Harness.CrashAfterStep("charge")));
        Assert.Equal(1, h.World.Bodies);

        var retry = await h.Run<Trouble>(Mode("repeatable"), attempt: 2);

        Assert.Equal(RunOutcome.Succeeded, retry.Outcome);
        Assert.Equal(2, h.World.Bodies);
        Assert.Equal(StepStatus.Ran, Status(retry, "charge"));
    }

    [Theory]
    [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
    public async Task Ask_stops_the_run_naming_the_step_until_an_operator_answers(string engine)
    {
        using var h = new Harness(engine);
        await Assert.ThrowsAsync<SimulatedCrashException>(() => h.Run<Trouble>(Mode("ask"), hooks: Harness.CrashAfterStep("charge")));

        var stopped = await h.Run<Trouble>(Mode("ask"), attempt: 2);

        Assert.Equal(RunOutcome.Failed, stopped.Outcome);
        Assert.Equal(TaskFailureKind.Permanent, stopped.End.Failure!.Kind);              // retrying alone would ask again
        Assert.Contains("'charge'", stopped.End.Failure.Message, StringComparison.Ordinal);
        Assert.Contains("'a'", stopped.End.Failure.Message, StringComparison.Ordinal);
        Assert.Equal("a", stopped.End.Failure.Item);
        Assert.Equal(1, h.World.Bodies);

        Assert.True(h.StepsOf("trouble").Resolve(Harness.Chain, "charge", "a", happened: true, resultJson: "\"done\"", actor: "alice"));
        var resolved = await h.Run<Trouble>(Mode("ask"), attempt: 3);

        Assert.Equal(RunOutcome.Succeeded, resolved.Outcome);
        Assert.Equal(StepStatus.Reused, Status(resolved, "charge"));
        Assert.Equal(1, h.World.Bodies);
    }

    [Theory]
    [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
    public async Task A_step_that_throws_is_failed_and_runs_again_on_the_retry(string engine)
    {
        using var h = new Harness(engine);
        h.World.Throw = new TimeoutException("the API timed out");

        var failed = await h.Run<Trouble>(Mode("throws"));

        Assert.Equal(RunOutcome.Failed, failed.Outcome);
        Assert.True(TaskEndMapping.ShouldRetry(failed.End, 1, 3));
        Assert.Equal(StepState.Failed, h.StepsOf("trouble").Get(Harness.Chain, "charge", "a")!.State);
        Assert.Equal(StepStatus.Failed, Status(failed, "charge"));

        h.World.Throw = null;
        var retry = await h.Run<Trouble>(Mode("throws"), attempt: 2);

        Assert.Equal(RunOutcome.Succeeded, retry.Outcome);
        Assert.Equal(2, h.World.Bodies);                                                // a failed step is not in doubt: it ran and said so
    }

    [Theory]
    [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
    public async Task Failures_are_classified_through_the_run_model(string engine)
    {
        using var h = new Harness(engine);

        h.World.Throw = new TaskFailureException(TaskFailureKind.Permanent, "The file is not a CSV.", item: "in/a.csv");
        var permanent = await h.Run<Trouble>(Mode("throws"), chain: NewChain());
        Assert.Equal((TaskFailureKind.Permanent, "in/a.csv"), (permanent.End.Failure!.Kind, permanent.End.Failure.Item));
        Assert.False(TaskEndMapping.ShouldRetry(permanent.End, 1, 3));
        Assert.Equal("in/a.csv", h.StoredRun(permanent.RunId).Run.FailedItem);

        h.World.Throw = new DbDataKit.Connections.ConnectionFailureException(new DbDataKit.Connections.ConnectionFailure(DbDataKit.Connections.ConnectionFailureKind.Network, "08001", null));
        var network = await h.Run<Trouble>(Mode("throws"), chain: NewChain());
        Assert.Equal((TaskFailureKind.Temporary, "Network"), (network.End.Failure!.Kind, network.End.Failure.Code));
        Assert.Equal("Temporary:Network", h.StoredRun(network.RunId).Run.Failure!.Kind);

        h.World.Throw = new DbDataKit.Connections.ConnectionFailureException(new DbDataKit.Connections.ConnectionFailure(DbDataKit.Connections.ConnectionFailureKind.Login, "18456", null));
        var login = await h.Run<Trouble>(Mode("throws"), chain: NewChain());
        Assert.Equal(TaskFailureKind.Permanent, login.End.Failure!.Kind);

        h.World.Throw = new InvalidOperationException("a bug");
        var bug = await h.Run<Trouble>(Mode("throws"), chain: NewChain());
        Assert.Equal(TaskFailureKind.Permanent, bug.End.Failure!.Kind);
        Assert.Empty(bug.End.Problems());
    }

    [Theory]
    [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
    public async Task A_cancelled_run_is_stopped_and_its_step_stays_in_doubt(string engine)
    {
        using var h = new Harness(engine);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var stopped = await h.Run<Trouble>(Mode("cancel"), ct: cts.Token);

        Assert.Equal(RunOutcome.Stopped, stopped.Outcome);                              // a cancel is a stop, never a failure to retry
        Assert.Equal(TaskFailureKind.Cancelled, stopped.End.Failure!.Kind);
        Assert.Equal(RunOutcome.Stopped, h.StoredRun(stopped.RunId).Run.Outcome);
        Assert.Equal(StepState.Started, h.StepsOf("trouble").Get(Harness.Chain, "charge", "a")!.State);     // the body stopped part way: nothing says what it had done

        var retry = await h.Run<Trouble>(Mode("cancel"), attempt: 2);                    // Repeatable: in doubt means run it again

        Assert.Equal(RunOutcome.Succeeded, retry.Outcome);
        Assert.Equal(2, h.World.Bodies);
    }

    [Theory]
    [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
    public async Task A_step_inside_a_step_is_refused_naming_both(string engine)
    {
        using var h = new Harness(engine);

        var result = await h.Run<Trouble>(Mode("nested"));

        Assert.Equal(RunOutcome.Failed, result.Outcome);
        Assert.Contains("'inner'", result.End.Failure!.Message, StringComparison.Ordinal);
        Assert.Contains("'charge'", result.End.Failure.Message, StringComparison.Ordinal);
        Assert.Equal(["charge"], h.StepsOf("trouble").List(Harness.Chain).Select(s => s.Name).ToArray());     // the inner step never began
    }

    [Theory]
    [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
    public async Task A_value_over_the_cap_fails_the_step_and_says_to_declare_a_streaming_step(string engine)
    {
        using var h = new Harness(engine);

        var result = await h.Run<Trouble>(Mode("too-big"));

        Assert.Equal(RunOutcome.Failed, result.Outcome);
        Assert.Contains("streaming step", result.End.Failure!.Message, StringComparison.Ordinal);
        Assert.Contains("'big-value'", result.End.Failure.Message, StringComparison.Ordinal);
        Assert.Equal(StepState.Failed, h.StepsOf("trouble").Get(Harness.Chain, "big-value", "a")!.State);
    }

    [Theory]
    [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
    public async Task An_interrupted_stream_leaves_nothing_stored_and_the_retry_writes_it_again(string engine)
    {
        using var h = new Harness(engine);
        await Assert.ThrowsAsync<SimulatedCrashException>(() => h.Run<Trouble>(Mode("stream-repeatable"), hooks: Harness.CrashAfterStep("make")));
        var first = h.StepsOf("trouble").Get(Harness.Chain, "make", "s")!;
        Assert.Equal(StepState.Started, first.State);
        Assert.Null(first.ResultPayload);

        var retry = await h.Run<Trouble>(Mode("stream-repeatable"), attempt: 2);

        Assert.Equal(RunOutcome.Succeeded, retry.Outcome);
        Assert.Equal(2, h.World.Bodies);
        var step = h.StepsOf("trouble").Get(Harness.Chain, "make", "s")!;
        Assert.Equal(StepState.Completed, step.State);
        Assert.True(new PayloadStore(h.Database).Get(step.ResultPayload!.Value)!.Complete);
    }

    [Theory]
    [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
    public async Task A_file_that_arrives_twice_is_claimed_once(string engine)
    {
        using var h = new Harness(engine);

        var first = await h.Run<Trouble>(Mode("files"));
        Assert.Equal((1, 0), (first.Counters["claimed"], first.Counters["claimed-again"]));      // the renamed copy has the same hash: processed already

        var next = await h.Run<Trouble>(Mode("files"), chain: NewChain());
        Assert.Equal(0, next.Counters["claimed"]);                                       // and a later run does not see it again either
        Assert.True(h.ProgressOf("trouble").FindFile("inbox", "hash-1")!.Processed);
    }

    [Theory]
    [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
    public async Task A_rejected_record_is_a_dead_letter_and_the_run_completes_with_failures(string engine)
    {
        using var h = new Harness(engine);

        var result = await h.Run<Trouble>(Mode("reject"));

        Assert.Equal(RunOutcome.CompletedWithFailures, result.Outcome);
        var letter = Assert.Single(h.ProgressOf("trouble").DeadLetters());
        Assert.Equal("The amount is negative.", letter.Error);
        Assert.Equal("record:Invoice", letter.Source);
        var body = await new PayloadStore(h.Database).OpenRead(letter.BodyPayload!.Value).ReadAllAsync();
        Assert.Equal(new Invoice("bad", -1), System.Text.Json.JsonSerializer.Deserialize<Invoice>(body, System.Text.Json.JsonSerializerOptions.Web));
    }

    [Theory]
    [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
    public async Task An_inbox_item_neither_completed_nor_rejected_is_released_when_the_run_ends(string engine)
    {
        using var h = new Harness(engine);
        h.Receive("trouble", "billing", new Invoice("inv1", 5));

        var result = await h.Run<Trouble>(Mode("take-one"));

        Assert.Equal(RunOutcome.Succeeded, result.Outcome);
        Assert.Equal(1, h.ProgressOf("trouble").InboxWaiting());                        // taken again by the next run
    }

    [Theory]
    [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
    public async Task The_run_log_gets_the_jobs_lines_and_a_summary(string engine)
    {
        using var h = new Harness(engine);

        var result = await h.Run<Trouble>(Mode("log"));

        var lines = h.Runs.Logs(result.RunId).Select(l => l.Message).ToArray();
        Assert.Contains("hello from the run", lines);
        Assert.Contains(lines, l => l.StartsWith("Run ended Succeeded", StringComparison.Ordinal) && l.Contains("last status: working", StringComparison.Ordinal));
        Assert.Equal("working", result.LastStatus);
    }

    [Theory]
    [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
    public async Task A_unit_that_throws_does_not_stop_the_others_unless_the_options_say_so(string engine)
    {
        using var h = new Harness(engine);

        var carry = await h.Run<Stoppable>(new StopInput(false));
        Assert.Equal(RunOutcome.CompletedWithFailures, carry.Outcome);
        Assert.Equal([1, 2, 3, 4, 5, 6], h.World.Ran.Order().ToArray());
        Assert.Equal("2", carry.End.FirstFailedItem);

        var chain = NewChain();
        var stop = await h.Run<Stoppable>(new StopInput(true), chain: chain);
        Assert.Equal(RunOutcome.Failed, stop.Outcome);
        Assert.Equal("2", stop.End.Failure!.Item);
        Assert.Contains("number 2 is bad", stop.End.Failure.Message, StringComparison.Ordinal);
        Assert.Equal(["1", "2", "3", "4", "5", "6"], h.UnitsOf("stoppable").List(chain).Select(u => u.Key).Order().ToArray());
        Assert.Equal([UnitState.Completed, UnitState.Failed, UnitState.Skipped, UnitState.Skipped, UnitState.Skipped, UnitState.Skipped],
            h.UnitsOf("stoppable").List(chain).OrderBy(u => u.Key, StringComparer.Ordinal).Select(u => u.State).ToArray());
        Assert.Equal(8, h.World.Ran.Count);                                             // six, then 1 and 2 again: 3 to 6 did not run
    }
}

public sealed record ProbeInput(Func<Action> Enter);

public static class ProbeUnits
{
    public static readonly Unit<int> Item = Unit.Of<int>("item", n => n.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

[RunnerTask("probe")]
public sealed class ParallelProbe : RunnerJob<ProbeInput, NoProgress, NoSlots>
{
    protected override Task RunAsync(CancellationToken ct) =>
        ForEachAsync(ProbeUnits.Item, Enumerable.Range(1, 12), parallelism: 8, async (n, ct) =>
        {
            var leave = Input.Enter();
            await Task.Delay(30, ct);
            leave();
        }, ct);
}

internal static class StreamExtensions
{
    public static async Task<byte[]> ReadAllAsync(this Stream stream)
    {
        await using (stream)
        {
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            return memory.ToArray();
        }
    }
}
