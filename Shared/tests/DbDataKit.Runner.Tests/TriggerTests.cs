using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using DbDataKit.Monitoring;
using DbDataKit.Runner.Execution;
using DbDataKit.Runner.Scheduler;
using DbDataKit.Runner.Triggers;
using DbDataKit.Scheduling;
using DbDataKit.State;
using DbDataKit.State.Tests;
using static DbDataKit.Runner.Tests.SchedulerHarness;

namespace DbDataKit.Runner.Tests;

/// <summary>One state database on an engine with the modules triggers touch (queue, runs, projection, progress, payloads), a clock the tests move, and a way to make schedulers over it.</summary>
internal sealed class TriggerHarness : IDisposable
{
    private readonly StateEngines _engines = new();
    private readonly StateEngines.Location _location;
    private readonly string _directory = Directory.CreateTempSubdirectory("dbdatakit-trig-").FullName;

    public TriggerHarness(string engine)
    {
        _location = _engines.Provision(engine);
        Database = OpenDatabase();
        Cache = new ArtifactCache(Path.Combine(_directory, "cache"), new EmptySource());
    }

    public string Root => _directory;

    public TestClock Clock { get; } = new(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));

    public StateDatabase Database { get; }

    public InMemoryVersionSource Versions { get; } = new();

    public ArtifactCache Cache { get; }

    public ModuleWorkQueueStore Queue => new(Database, ServiceName);

    public RunStore Runs => new(Database, ServiceName);

    public const string ServiceName = "runner";

    public StateDatabase OpenDatabase() => _location.Open([SchedulingModule.Module, RunnerModule.Module, RunsModule.Module, ProgressModule.Module, PayloadStore.Module, DefinitionsModule.Module, QualityModule.Module], StateScope.Standalone, time: Clock);

    public static PackageTask Def(string name, IReadOnlyList<TaskTrigger>? triggers = null, TaskLimits? limits = null, bool allowOverlap = false) =>
        new(name, new TaskEntry("T"), new Schedule(), triggers ?? [], [], [], ProgressDeclaration.Empty, limits ?? TaskLimits.None, [], [], allowOverlap);

    public void Activate(PackageManifest manifest, string version = "1", string channel = Channel)
    {
        Versions.Add(manifest.Name, version, manifest);
        new TaskProjections(Database).Replace(manifest.Name, channel, ProjectionDocuments.RowsOf(manifest, version));
    }

    public RunnerScheduler NewScheduler(ITaskRunner runner, StateDatabase? database = null, TriggerOptions? triggers = null, string[]? channels = null) =>
        new(database ?? Database, runner, Cache, Versions, new RunnerSchedulerOptions
        {
            Channels = channels ?? [Channel],
            WorkerId = "loop-" + Guid.NewGuid().ToString("N"),
            Tick = TimeSpan.FromSeconds(5),
            Jitter = () => 0.5,
            Triggers = triggers ?? new TriggerOptions(),
        });

    /// <summary>A tick that waits for what it started, and moves the clock a second first (finished runs have distinct end times, as they do in life).</summary>
    public async Task<TickReport> Tick(RunnerScheduler scheduler)
    {
        Clock.Advance(TimeSpan.FromSeconds(1));
        var report = await scheduler.TickAsync();
        await scheduler.WhenIdleAsync();
        return report;
    }

    public static TaskEnd Failed() => new(TaskEndState.Failed, Failure: new TaskFailure(TaskFailureKind.Permanent, "boom"));

    public static readonly RunOutcome[] AnyOutcome = [RunOutcome.Succeeded, RunOutcome.CompletedWithFailures, RunOutcome.Failed, RunOutcome.Stopped, RunOutcome.Interrupted];

    public int RunsOf(string task) => Runs.Read(new RunQuery("runner", $"pkg/prod/{task}", Limit: 500)).Count;

    public void Dispose()
    {
        _engines.Dispose();
        try { Directory.Delete(_directory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* temp */ }
    }
}

/// <summary>Plan 28a R13: triggers write work items through the scheduler's own path, on each engine, with a fake clock and a fake executor.</summary>
public sealed class TriggerTests
{
    public static TheoryData<string> Engines => StateEngines.Engines;

    private static readonly TaskRef A = new("pkg", "prod", "a");
    private static readonly TaskRef B = new("pkg", "prod", "b");

    private static PackageManifest Manifest(params PackageTask[] tasks) => SchedulerHarness.Manifest("pkg", tasks);

    private static PackageTask Def(string name, params TaskTrigger[] triggers) => TriggerHarness.Def(name, triggers);

    // ---- after a run

    [Theory, MemberData(nameof(Engines))]
    public async Task B_runs_after_A_succeeds_and_not_after_A_fails_when_the_filter_says_succeeded(string engine)
    {
        using var h = new TriggerHarness(engine);
        h.Activate(Manifest(Def("a"), Def("b", TaskTrigger.AfterRun("a"))));
        var failA = false;
        var runner = new FakeRunner(h.Database) { Behaviour = (r, _) => Task.FromResult(r.Task.Task == "a" && failA ? TriggerHarness.Failed() : new TaskEnd(TaskEndState.Succeeded)) };
        var scheduler = h.NewScheduler(runner);

        await h.Tick(scheduler);                       // the trigger starts following from here
        scheduler.RunNow(A);
        await h.Tick(scheduler);                       // A runs
        Assert.Equal(0, h.RunsOf("b"));
        await h.Tick(scheduler);                       // the end of A is seen: B is queued and runs
        Assert.Equal((1, 1), (h.RunsOf("a"), h.RunsOf("b")));
        await h.Tick(scheduler);
        Assert.Equal(1, h.RunsOf("b"));                // each finished run triggers once

        failA = true;
        scheduler.RunNow(A);
        await h.Tick(scheduler);
        await h.Tick(scheduler);
        await h.Tick(scheduler);
        Assert.Equal((2, 1), (h.RunsOf("a"), h.RunsOf("b")));      // the failed A did not start B
        Assert.Contains(scheduler.Triggers.Statuses(), s => s.Task == B && s.Kind == "after" && s.Id == "after:pkg/a");
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_dry_run_of_A_is_told_so_and_does_not_start_B_while_a_real_run_does(string engine)
    {
        using var h = new TriggerHarness(engine);
        h.Activate(Manifest(Def("a"), Def("b", TaskTrigger.AfterRun("a"))));
        var inputsOfA = new List<IReadOnlyDictionary<string, string>>();
        var runner = new FakeRunner(h.Database) { Behaviour = (r, _) => { if (r.Task.Task == "a") inputsOfA.Add(r.Inputs); return Task.FromResult(new TaskEnd(TaskEndState.Succeeded)); } };
        var scheduler = h.NewScheduler(runner);

        await h.Tick(scheduler);
        scheduler.RunNow(A, null, "test", dryRun: true);
        await h.Tick(scheduler);                       // the dry run of A runs
        await h.Tick(scheduler);                       // its end is seen and set aside
        await h.Tick(scheduler);
        Assert.Equal((1, 0), (h.RunsOf("a"), h.RunsOf("b")));
        Assert.Equal("true", Assert.Single(inputsOfA)[RunnerProtocol.DryRunInput]);

        scheduler.RunNow(A);
        await h.Tick(scheduler);
        await h.Tick(scheduler);
        await h.Tick(scheduler);
        Assert.Equal((2, 1), (h.RunsOf("a"), h.RunsOf("b")));
        Assert.False(inputsOfA[1].ContainsKey(RunnerProtocol.DryRunInput));
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task With_any_B_runs_after_a_failure_too_and_a_filter_names_the_outcomes_it_wants(string engine)
    {
        using var h = new TriggerHarness(engine);
        h.Activate(Manifest(Def("a"), Def("b", TaskTrigger.AfterRun("a", TriggerHarness.AnyOutcome)), Def("c", TaskTrigger.AfterRun("a", RunOutcome.Failed))));
        var failA = true;
        var runner = new FakeRunner(h.Database) { Behaviour = (r, _) => Task.FromResult(r.Task.Task == "a" && failA ? TriggerHarness.Failed() : new TaskEnd(TaskEndState.Succeeded)) };
        var scheduler = h.NewScheduler(runner);

        await h.Tick(scheduler);
        scheduler.RunNow(A);
        await h.Tick(scheduler);
        await h.Tick(scheduler);
        Assert.Equal((1, 1), (h.RunsOf("b"), h.RunsOf("c")));          // failed: both follow

        failA = false;
        scheduler.RunNow(A);
        await h.Tick(scheduler);
        await h.Tick(scheduler);
        await h.Tick(scheduler);
        Assert.Equal((2, 1), (h.RunsOf("b"), h.RunsOf("c")));          // succeeded: only the any
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task Two_scheduler_instances_on_one_database_run_B_once_per_A_run(string engine)
    {
        using var h = new TriggerHarness(engine);
        h.Activate(Manifest(Def("a"), Def("b", TaskTrigger.AfterRun("a"))));
        var second = h.OpenDatabase();
        var one = h.NewScheduler(new FakeRunner(h.Database));
        var two = h.NewScheduler(new FakeRunner(second), second);
        await one.TickAsync();
        await two.TickAsync();

        for (var i = 0; i < 6; i++)
        {
            one.RunNow(A);
            await h.Tick(one);                                                // A runs
            h.Clock.Advance(TimeSpan.FromSeconds(1));
            await Task.WhenAll(h.Tick(one), h.Tick(two), h.Tick(one), h.Tick(two));    // both see the end of A at once
        }

        await h.Tick(one);
        await h.Tick(two);
        var runs = h.Runs.Read(new RunQuery("runner", "pkg/prod/b", Limit: 500));
        Assert.Equal((6, 6), (h.RunsOf("a"), runs.Count));
        Assert.Equal(6, runs.Select(r => r.Id).Distinct().Count());
        Assert.Empty(h.Queue.ListInFlight("pkg/prod"));
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_chain_runs_across_packages_on_one_runner(string engine)
    {
        using var h = new TriggerHarness(engine);
        h.Activate(SchedulerHarness.Manifest("pkg", Def("a")));
        h.Activate(SchedulerHarness.Manifest("other", TriggerHarness.Def("b", [new TaskTrigger(TriggerKind.AfterRun, Task: "a", Package: "pkg")])));
        h.Activate(SchedulerHarness.Manifest("third", TriggerHarness.Def("c", [new TaskTrigger(TriggerKind.AfterRun, Task: "b", Package: "other")])));
        var runner = new FakeRunner(h.Database);
        var scheduler = h.NewScheduler(runner);

        await h.Tick(scheduler);
        scheduler.RunNow(A);
        for (var i = 0; i < 4; i++) await h.Tick(scheduler);

        Assert.Equal(["a", "b", "c"], runner.Requests.Select(r => r.Task.Task).ToArray());
        Assert.Equal(["pkg", "other", "third"], runner.Requests.Select(r => r.Task.Package).ToArray());
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_failed_run_that_will_be_retried_does_not_trigger_until_its_last_attempt(string engine)
    {
        using var h = new TriggerHarness(engine);
        h.Activate(Manifest(
            TriggerHarness.Def("a", limits: new TaskLimits(Attempts: 2, Backoff: TimeSpan.FromSeconds(30), MaxBackoff: TimeSpan.FromSeconds(30))),
            Def("b", TaskTrigger.AfterRun("a", RunOutcome.Failed))));
        var runner = new FakeRunner(h.Database)
        {
            Behaviour = (r, _) => Task.FromResult(r.Task.Task == "a" ? new TaskEnd(TaskEndState.Failed, Failure: new TaskFailure(TaskFailureKind.Temporary, "later")) : new TaskEnd(TaskEndState.Succeeded)),
        };
        var scheduler = h.NewScheduler(runner);

        await h.Tick(scheduler);
        scheduler.RunNow(A);
        await h.Tick(scheduler);                       // attempt 1 fails, a retry is queued for 30 s later
        await h.Tick(scheduler);
        Assert.Equal((1, 0), (h.RunsOf("a"), h.RunsOf("b")));          // not the end yet

        h.Clock.Advance(TimeSpan.FromSeconds(40));
        await h.Tick(scheduler);                       // attempt 2 runs and fails for good
        await h.Tick(scheduler);
        await h.Tick(scheduler);
        Assert.Equal((2, 1), (h.RunsOf("a"), h.RunsOf("b")));          // B once, for the chain
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_paused_task_is_not_started_by_the_end_of_another_run(string engine)
    {
        using var h = new TriggerHarness(engine);
        h.Activate(Manifest(Def("a"), Def("b", TaskTrigger.AfterRun("a"))));
        var scheduler = h.NewScheduler(new FakeRunner(h.Database));
        scheduler.Pause(B, "me");

        await h.Tick(scheduler);
        scheduler.RunNow(A);
        await h.Tick(scheduler);
        await h.Tick(scheduler);
        scheduler.Resume(B);
        await h.Tick(scheduler);
        await h.Tick(scheduler);

        Assert.Equal((1, 0), (h.RunsOf("a"), h.RunsOf("b")));          // dropped, as a schedule's times are
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_trigger_cursor_moves_only_from_where_it_was_read(string engine)
    {
        using var h = new TriggerHarness(engine);
        var states = new TriggerStates(h.Database);
        var other = new TriggerStates(h.OpenDatabase());

        Assert.True(states.TryAdvance("pkg", "prod", "b", "after:pkg/a", null, "1|x", null));
        Assert.False(other.TryAdvance("pkg", "prod", "b", "after:pkg/a", null, "1|y", null));      // no cursor was the claim: taken
        Assert.False(other.TryAdvance("pkg", "prod", "b", "after:pkg/a", "0|z", "2|z", null));      // not where it was read
        Assert.True(other.TryAdvance("pkg", "prod", "b", "after:pkg/a", "1|x", "2|z", h.Clock.GetUtcNow()));
        Assert.Equal("2|z", states.Get("pkg", "prod", "b", "after:pkg/a")!.Cursor);

        var results = new ConcurrentBag<bool>();
        Parallel.For(0, 8, i => results.Add(new TriggerStates(h.OpenDatabase()).TryAdvance("pkg", "prod", "b", "after:pkg/a", "2|z", $"3|{i}", null)));
        Assert.Single(results, r => r);                                                            // of eight racing, one moved it
    }

    // ---- webhooks


    [Theory, MemberData(nameof(Engines))]
    public async Task A_webhook_body_becomes_an_inbox_item_and_a_task_takes_it_exactly_once_also_with_two_runners(string engine)
    {
        using var h = new TriggerHarness(engine);
        h.Activate(Manifest(Def("hook", TaskTrigger.OnWebhook("shop"))));
        var taken = new ConcurrentBag<Guid>();
        var bodies = new ConcurrentBag<string>();
        FakeRunner Taker(StateDatabase db)
        {
            var fake = new FakeRunner(db)
            {
                Behaviour = (request, _) =>
                {
                    var progress = new ProgressStore(db, new ProgressKey("pkg", "prod", "hook"));
                    while (progress.TakeNext(request.RunId!.Value) is { } item)
                    {
                        taken.Add(item.Id);
                        using var reader = new PayloadStore(db).OpenRead(item.BodyPayload!.Value);
                        using var text = new StreamReader(reader);
                        bodies.Add(text.ReadToEnd());
                    }

                    return Task.FromResult(new TaskEnd(TaskEndState.Succeeded));
                },
            };
            return fake;
        }

        var second = h.OpenDatabase();
        var firstRunner = Taker(h.Database);
        var secondRunner = Taker(second);
        var one = h.NewScheduler(firstRunner);
        var two = h.NewScheduler(secondRunner, second);
        var receiver = new WebhookReceiver(h.Database, one.Triggers);

        var receipts = new List<WebhookReceipt>();
        for (var i = 0; i < 6; i++)
        {
            h.Clock.Advance(TimeSpan.FromSeconds(1));
            receipts.Add(await receiver.ReceiveAsync("pkg", "prod", "shop", true, Encoding.UTF8.GetBytes($$"""{"order":{{i}}}""")));
            if (i % 2 == 1) await Task.WhenAll(h.Tick(one), h.Tick(two));      // two runners go at the inbox together
        }

        await Task.WhenAll(h.Tick(one), h.Tick(two));
        await Task.WhenAll(h.Tick(one), h.Tick(two));
        Assert.All(receipts, r => Assert.Single(r.Deliveries));
        Assert.Equal(6, taken.Count);
        Assert.Equal(6, taken.Distinct().Count());                              // each webhook went to one run
        Assert.Equal(Enumerable.Range(0, 6).Select(i => $$"""{"order":{{i}}}""").Order(), bodies.Order());
        Assert.Equal(0, new ProgressStore(h.Database, new ProgressKey("pkg", "prod", "hook")).InboxWaiting());
        Assert.True(firstRunner.Requests.Count + secondRunner.Requests.Count >= 1);
        Assert.Empty(h.Queue.ListInFlight("pkg/prod"));
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_webhook_with_a_bad_signature_is_stored_not_triggered_and_no_run_can_take_it(string engine)
    {
        using var h = new TriggerHarness(engine);
        h.Activate(Manifest(Def("hook", TaskTrigger.OnWebhook("shop", "shop_secret"))));
        var runner = new FakeRunner(h.Database);
        var scheduler = h.NewScheduler(runner);
        var receiver = new WebhookReceiver(h.Database, scheduler.Triggers);

        var receipt = await receiver.ReceiveAsync("pkg", "prod", "shop", false, "tampered"u8.ToArray());
        await h.Tick(scheduler);
        await h.Tick(scheduler);

        var delivery = Assert.Single(receipt.Deliveries);
        Assert.False(delivery.Queued);
        Assert.Empty(runner.Requests);
        var progress = new ProgressStore(h.Database, new ProgressKey("pkg", "prod", "hook"));
        var record = Assert.Single(progress.InboxRecords());
        Assert.Equal((false, ProgressStore.RefusedBySignature), (record.SignatureOk, record.TakenBy));
        Assert.NotNull(record.BodyPayload);                                      // the body is kept
        Assert.Null(progress.TakeNext(Guid.NewGuid()));
        Assert.Equal(0, progress.InboxWaiting());
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_webhook_nobody_listens_for_is_not_stored_and_one_for_a_busy_task_waits_for_it(string engine)
    {
        using var h = new TriggerHarness(engine);
        h.Activate(Manifest(Def("hook", TaskTrigger.OnWebhook("shop"))));
        var release = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var first = true;
        var runner = new FakeRunner(h.Database)
        {
            Behaviour = async (request, _) =>
            {
                if (first)
                {
                    first = false;
                    started.SetResult();
                    await release.Task;
                }

                return new TaskEnd(TaskEndState.Succeeded);
            },
        };
        var scheduler = h.NewScheduler(runner);
        var receiver = new WebhookReceiver(h.Database, scheduler.Triggers);

        Assert.False((await receiver.ReceiveAsync("pkg", "prod", "other-source", true, "x"u8.ToArray())).Subscribed);
        Assert.Empty(new ProgressStore(h.Database, new ProgressKey("pkg", "prod", "hook")).InboxRecords());

        h.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True((await receiver.ReceiveAsync("pkg", "prod", "shop", true, "1"u8.ToArray())).Deliveries[0].Queued);
        await scheduler.TickAsync();
        await started.Task;                                   // the run is going and takes nothing yet
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False((await receiver.ReceiveAsync("pkg", "prod", "shop", true, "2"u8.ToArray())).Deliveries[0].Queued);    // it is running: not queued twice
        release.SetResult();
        await scheduler.WhenIdleAsync();

        await h.Tick(scheduler);                              // the item that arrived meanwhile is not lost: another run is queued for it
        Assert.Equal(2, h.RunsOf("hook"));
    }

    // ---- files

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    [Theory, MemberData(nameof(Engines))]
    public async Task A_file_seen_twice_runs_once_and_a_changed_file_runs_again(string engine)
    {
        using var h = new TriggerHarness(engine);
        var folder = Path.Combine(h.Root, "inbox");
        Directory.CreateDirectory(Path.Combine(folder, "in"));
        var task = new PackageTask("load", new TaskEntry("T"), new Schedule(), [TaskTrigger.OnFile("inbox", "in/*.csv", TimeSpan.FromSeconds(10))], [], [new ConnectionSlot("inbox", "file")],
            ProgressDeclaration.Empty, TaskLimits.None, [], []);
        h.Activate(Manifest(task));
        var runner = new FakeRunner(h.Database);
        var scheduler = h.NewScheduler(runner, triggers: new TriggerOptions { FileSources = new LocalFileSources(new Dictionary<string, string> { ["inbox"] = folder }), FileSettle = TimeSpan.Zero });
        var load = new TaskRef("pkg", "prod", "load");
        var ledger = new ProgressStore(h.Database, new ProgressKey("pkg", "prod", "load"));
        var csv = (string name) => Path.Combine(folder, "in", name);

        await h.Tick(scheduler);
        Assert.Empty(runner.Requests);                         // no file yet
        await File.WriteAllTextAsync(csv("a.csv"), "id,1\n");
        await File.WriteAllTextAsync(csv("skip.txt"), "not matched");
        h.Clock.Advance(TimeSpan.FromSeconds(11));
        await h.Tick(scheduler);
        Assert.Single(runner.Requests);
        Assert.NotNull(ledger.FindFile("inbox", Hash("id,1\n")));         // noted on first sight; processing is the task's

        h.Clock.Advance(TimeSpan.FromSeconds(11));
        await h.Tick(scheduler);
        Assert.Single(runner.Requests);                        // the same file again: no second run

        await File.WriteAllTextAsync(csv("copy-of-a.csv"), "id,1\n");     // the same content under another name
        h.Clock.Advance(TimeSpan.FromSeconds(11));
        await h.Tick(scheduler);
        Assert.Single(runner.Requests);

        await File.WriteAllTextAsync(csv("a.csv"), "id,1\nid,2\n");        // changed: a new content
        h.Clock.Advance(TimeSpan.FromSeconds(11));
        await h.Tick(scheduler);
        Assert.Equal(2, runner.Requests.Count);
        Assert.NotNull(ledger.FindFile("inbox", Hash("id,1\nid,2\n")));

        await File.WriteAllTextAsync(csv("b.csv"), "id,3\n");
        h.Clock.Advance(TimeSpan.FromSeconds(3));
        await h.Tick(scheduler);
        Assert.Equal(2, runner.Requests.Count);                // within the poll interval the folder is not looked at
        h.Clock.Advance(TimeSpan.FromSeconds(10));
        await h.Tick(scheduler);
        Assert.Equal(3, runner.Requests.Count);
        Assert.All(runner.Requests, r => Assert.Equal(load, r.Task));
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task Two_runners_polling_one_folder_queue_each_file_once(string engine)
    {
        using var h = new TriggerHarness(engine);
        var folder = Path.Combine(h.Root, "inbox");
        Directory.CreateDirectory(folder);
        for (var i = 0; i < 5; i++) await File.WriteAllTextAsync(Path.Combine(folder, $"f{i}.csv"), $"content {i}");
        h.Activate(Manifest(new PackageTask("load", new TaskEntry("T"), new Schedule(), [TaskTrigger.OnFile("inbox", "*.csv", TimeSpan.FromSeconds(1))], [], [new ConnectionSlot("inbox", "file")],
            ProgressDeclaration.Empty, TaskLimits.None, [], [])));
        var options = new TriggerOptions { FileSources = new LocalFileSources(new Dictionary<string, string> { ["inbox"] = folder }), FileSettle = TimeSpan.Zero };
        var second = h.OpenDatabase();
        var one = h.NewScheduler(new FakeRunner(h.Database), triggers: options);
        var two = h.NewScheduler(new FakeRunner(second), second, options);

        // the five runs of one task take turns (the run lock), so it takes several ticks with the clock past the wait of a turned-away item; polling goes on meanwhile and finds nothing new
        for (var i = 0; i < 14; i++)
        {
            h.Clock.Advance(TimeSpan.FromSeconds(6));
            await Task.WhenAll(h.Tick(one), h.Tick(two));
        }

        Assert.Equal(5, h.RunsOf("load"));
    }

    /// <summary>A file of 200 MiB that exists only as a stream of zeros.</summary>
    private sealed class HugeFileSource : IFileSource, IFileSourceProvider
    {
        public const long Size = 200L * 1024 * 1024;
        public int LargestRead;

        public IFileSource? Open(TaskRef task, string connectionSlot) => this;

        public async IAsyncEnumerable<FileEntry> ListAsync(string directory, string pattern, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield return new FileEntry("big.bin", Size, DateTimeOffset.UnixEpoch);
        }

        public Task<Stream> OpenReadAsync(string path, CancellationToken ct) => Task.FromResult<Stream>(new Zeros(this));

        private sealed class Zeros(HugeFileSource owner) : Stream
        {
            private long _left = Size;
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => Size;
            public override long Position { get => Size - _left; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

            public override int Read(Span<byte> buffer)
            {
                owner.LargestRead = Math.Max(owner.LargestRead, buffer.Length);
                var n = (int)Math.Min(buffer.Length, _left);
                buffer[..n].Clear();
                _left -= n;
                return n;
            }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromResult(Read(buffer.Span));
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_big_file_is_hashed_with_a_small_buffer(string engine)
    {
        using var h = new TriggerHarness(engine);
        h.Activate(Manifest(new PackageTask("load", new TaskEntry("T"), new Schedule(), [TaskTrigger.OnFile("inbox", "*.bin")], [], [new ConnectionSlot("inbox", "file")],
            ProgressDeclaration.Empty, TaskLimits.None, [], [])));
        var huge = new HugeFileSource();
        var runner = new FakeRunner(h.Database);
        var scheduler = h.NewScheduler(runner, triggers: new TriggerOptions { FileSources = huge, FileSettle = TimeSpan.Zero });

        await h.Tick(scheduler);

        Assert.Single(runner.Requests);
        Assert.InRange(huge.LargestRead, 1, 128 * 1024);       // 200 MiB went through in reads of at most 128 KiB
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_file_trigger_without_a_folder_for_its_slot_says_so_and_queues_nothing(string engine)
    {
        using var h = new TriggerHarness(engine);
        h.Activate(Manifest(new PackageTask("load", new TaskEntry("T"), new Schedule(), [TaskTrigger.OnFile("inbox", "*.csv")], [], [new ConnectionSlot("inbox", "file")],
            ProgressDeclaration.Empty, TaskLimits.None, [], [])));
        var runner = new FakeRunner(h.Database);
        var scheduler = h.NewScheduler(runner);

        await h.Tick(scheduler);

        Assert.Empty(runner.Requests);
        Assert.Contains(scheduler.Status().Problems, p => p.Subject == "pkg/prod/load" && p.Message.Contains("no folder for it", StringComparison.Ordinal));
        Assert.Contains("no folder", scheduler.Triggers.Statuses().Single(s => s.Kind == "file").Problem, StringComparison.Ordinal);
    }

    // ---- store change

    [Theory, MemberData(nameof(Engines))]
    public async Task A_store_change_trigger_is_accepted_reported_as_not_built_and_never_fires(string engine)
    {
        using var h = new TriggerHarness(engine);
        h.Activate(Manifest(Def("sync", TaskTrigger.OnStoreChange("orders", "order"))));
        var runner = new FakeRunner(h.Database);
        var scheduler = h.NewScheduler(runner);

        for (var i = 0; i < 3; i++) await h.Tick(scheduler);

        Assert.Empty(runner.Requests);
        Assert.Empty(h.Queue.ListInFlight("pkg/prod"));
        var problem = Assert.Single(scheduler.Status().Problems);
        Assert.Contains("waiting for a change source (not built)", problem.Message, StringComparison.Ordinal);
        var status = Assert.Single(scheduler.Triggers.Statuses());
        Assert.Equal(("store_change", "waiting for a change source (not built)"), (status.Kind, status.State));
    }

    // ---- cycles

    [Theory, MemberData(nameof(Engines))]
    public void A_trigger_loop_across_packages_is_refused_before_the_projection_is_replaced_and_names_the_loop(string engine)
    {
        using var h = new TriggerHarness(engine);
        var projections = new TaskProjections(h.Database);
        // pkg2's b follows pkg1's a; publishing pkg1 with an a that follows pkg2's b closes the loop
        projections.Replace("pkg2", "prod", ProjectionDocuments.RowsOf(SchedulerHarness.Manifest("pkg2", TriggerHarness.Def("b", [new TaskTrigger(TriggerKind.AfterRun, Task: "a", Package: "pkg1")])), "1"));
        var looping = ProjectionDocuments.RowsOf(SchedulerHarness.Manifest("pkg1", TriggerHarness.Def("a", [new TaskTrigger(TriggerKind.AfterRun, Task: "b", Package: "pkg2")])), "1");

        var refused = Assert.Throws<TriggerCycleException>(() => ProjectionDocuments.RequireNoCycle(projections.ListByChannel("prod"), "pkg1", "prod", looping));

        Assert.Contains("pkg1/a", refused.Message, StringComparison.Ordinal);
        Assert.Contains("pkg2/b", refused.Message, StringComparison.Ordinal);
        Assert.Contains("prod", refused.Message, StringComparison.Ordinal);
        Assert.Equal("prod", refused.Cycle.Channel);
        Assert.Equal(refused.Cycle.Tasks[0], refused.Cycle.Tasks[^1]);

        var fine = ProjectionDocuments.RowsOf(SchedulerHarness.Manifest("pkg1", TriggerHarness.Def("a")), "1");
        ProjectionDocuments.RequireNoCycle(projections.ListByChannel("prod"), "pkg1", "prod", fine);            // no loop: accepted
        ProjectionDocuments.RequireNoCycle(projections.ListByChannel("dev"), "pkg1", "dev", looping);           // channels do not trigger each other
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task The_runtime_depth_guard_stops_a_loop_that_got_in_anyway(string engine)
    {
        using var h = new TriggerHarness(engine);
        // projections built by hand, past the publish check: a follows b and b follows a
        h.Activate(Manifest(Def("a", TaskTrigger.AfterRun("b")), Def("b", TaskTrigger.AfterRun("a"))));
        var runner = new FakeRunner(h.Database);
        var scheduler = h.NewScheduler(runner);
        Assert.Equal(16, new TriggerOptions().MaxChainDepth);

        await h.Tick(scheduler);
        scheduler.RunNow(A);
        for (var i = 0; i < 30; i++) await h.Tick(scheduler);

        Assert.Equal(17, runner.Requests.Count);               // the manual run, then 16 triggered runs in one chain
        Assert.Contains(scheduler.Status().Problems, p => p.Message.Contains("deeper than the limit of 16", StringComparison.Ordinal));
        Assert.Contains(scheduler.Status().Problems, p => p.Message.Contains("The triggers form a loop", StringComparison.Ordinal));
        for (var i = 0; i < 3; i++) await h.Tick(scheduler);
        Assert.Equal(17, runner.Requests.Count);               // and it stays stopped
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_chain_deeper_than_a_configured_depth_stops_there(string engine)
    {
        using var h = new TriggerHarness(engine);
        h.Activate(Manifest(Def("a", TaskTrigger.AfterRun("b")), Def("b", TaskTrigger.AfterRun("a"))));
        var runner = new FakeRunner(h.Database);
        var scheduler = h.NewScheduler(runner, triggers: new TriggerOptions { MaxChainDepth = 3 });

        await h.Tick(scheduler);
        scheduler.RunNow(A);
        for (var i = 0; i < 12; i++) await h.Tick(scheduler);

        Assert.Equal(4, runner.Requests.Count);
    }

    // ---- the triggers column

    [Fact]
    public void Triggers_round_trip_through_their_text_and_a_bad_text_names_the_key()
    {
        TaskTrigger[] triggers =
        [
            TaskTrigger.AfterRun("extract"), new(TriggerKind.AfterRun, Task: "load", Package: "other", On: [RunOutcome.Failed, RunOutcome.CompletedWithFailures]),
            new(TriggerKind.AfterRun, Task: "any", On: TriggerHarness.AnyOutcome), TaskTrigger.OnWebhook("shop", "shop_hook"), TaskTrigger.OnFile("inbox", "in/*.csv", TimeSpan.FromMinutes(1)),
            TaskTrigger.OnStoreChange("orders", "order"), TaskTrigger.ByHand(),
        ];

        var text = ProjectionDocuments.TriggersText(triggers);
        var back = ProjectionDocuments.ParseTriggers(text);

        Assert.Equal(triggers.Select(t => (t.Kind, t.Task, t.Package, t.Source, t.Secret, t.Connection, t.Path, t.Poll, t.Model, t.Entity)),
            back.Select(t => (t.Kind, t.Task, t.Package, t.Source, t.Secret, t.Connection, t.Path, t.Poll, t.Model, t.Entity)));
        Assert.Equal([RunOutcome.Failed, RunOutcome.CompletedWithFailures], back[1].Outcomes);
        Assert.Equal(TriggerHarness.AnyOutcome.Order(), back[2].Outcomes.Order());
        Assert.Contains("\"on\":[\"any\"]", text, StringComparison.Ordinal);
        Assert.Null(ProjectionDocuments.TriggersText([]));
        Assert.Empty(ProjectionDocuments.ParseTriggers(null));
        Assert.Throws<FormatException>(() => ProjectionDocuments.ParseTriggers("{}"));
        Assert.Contains("triggers[0].after.task is missing", Assert.Throws<FormatException>(() => ProjectionDocuments.ParseTriggers("""[{"after":{}}]""")).Message, StringComparison.Ordinal);
        Assert.Contains("triggers[0].cron is not a trigger", Assert.Throws<FormatException>(() => ProjectionDocuments.ParseTriggers("""[{"cron":{}}]""")).Message, StringComparison.Ordinal);
        Assert.Contains("'later'", Assert.Throws<FormatException>(() => ProjectionDocuments.ParseTriggers("""[{"after":{"task":"a","on":["later"]}}]""")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_cross_package_graph_finds_loops_the_single_manifest_check_cannot()
    {
        var one = new ProjectedTriggers("p1", "prod", "a", [new TaskTrigger(TriggerKind.AfterRun, Task: "z", Package: "p2")]);
        var two = new ProjectedTriggers("p2", "prod", "z", [new TaskTrigger(TriggerKind.AfterRun, Task: "a", Package: "p1")]);
        var cycle = TriggerGraph.FindCycle([one, two])!;
        Assert.Equal("prod", cycle.Channel);
        Assert.Equal(3, cycle.Tasks.Count);
        Assert.Null(TriggerGraph.FindCycle([one, two with { Channel = "dev" }]));
        Assert.Null(TriggerGraph.FindCycle([one, two with { Triggers = [] }]));
        var selfLoop = TriggerGraph.FindCycle([new ProjectedTriggers("p", "prod", "t", [TaskTrigger.AfterRun("t")])])!;
        Assert.Equal(["p/t", "p/t"], selfLoop.Tasks);
    }
}
