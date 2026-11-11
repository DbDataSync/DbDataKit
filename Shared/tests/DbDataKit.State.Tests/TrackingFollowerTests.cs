using System.Data.Common;
using DbDataKit.Monitoring;
using DbDataKit.State.Tracking;

namespace DbDataKit.State.Tests;

/// <summary>Plan 29a Q21: the tracking follower, its divergence handling, identities and settings, on every engine for everything that touches state.</summary>
public sealed class TrackingFollowerTests : IDisposable
{
    // the blocking sinks below hold pool threads on purpose: with a small runner (two cores) the pool injects threads slowly and a timeout would only fire late
    static TrackingFollowerTests() => ThreadPool.SetMinThreads(32, 32);

    private const string Production = "mssql://prod-sql01/Warehouse";
    private const string Development = "mssql://dev-sql07/Warehouse";

    private static readonly TrackingStream Log = new("migration_log", ["id", "at", "object", "outcome"], orderingKey: "id", rowKey: "id");
    private static readonly ProgressKey Key = new("build", "main", "tracking");

    private readonly StateEngines _engines = new();

    public void Dispose() => _engines.Dispose();

    private static TrackingRow Row(long id, string text = "x") => new($"r{id:D4}", id, $"{{\"id\":{id},\"v\":\"{text}\"}}");

    /// <summary>Rows a restored target writes after the restore: the same ordering values as the rows it lost, but they are other rows, with keys of their own.</summary>
    private static TrackingRow[] After(int from, int to) => [.. Enumerable.Range(from, to - from + 1).Select(i => new TrackingRow($"n{i:D4}", i, $"{{\"id\":{i},\"v\":\"after-restore\"}}"))];

    private static TrackingRow[] Rows(int from, int to, string text = "x") => [.. Enumerable.Range(from, to - from + 1).Select(i => Row(i, text))];

    private static StateModule[] Modules => [ProgressModule.Module, TrackingModule.Module, AuditModule.Module];

    private StateDatabase Open(string engine, TimeProvider? time = null) => _engines.Provision(engine).Open(Modules, StateScope.Standalone, time: time);

    private static TrackingFollower Follower(StateDatabase database, ITrackingSource primary, ITrackingSink copy, string identity = Production, TrackingFollowerOptions? options = null, ITrackingDivergenceStore? divergences = null, AuditLog? audit = null) =>
        new(identity, Log, primary, copy, new ProgressStore(database, Key), divergences ?? new StateTrackingDivergences(database), options, audit);

    // ---------- module ----------

    [Fact]
    public void The_module_follows_the_conventions() => Assert.Empty(StateConventions.Violations(TrackingModule.Module));

    [Fact]
    public void Tracking_rows_have_no_default_retention_and_divergences_have_no_rule_at_all()
    {
        var rule = Assert.Single(TrackingModule.Module.Retention);
        Assert.Equal("tracking_row", rule.Table);
        Assert.Null(rule.DefaultDays);
        Assert.DoesNotContain(TrackingModule.Module.Retention, r => r.Table == "tracking_divergence");
        Assert.Equal(StateScopeKind.Group, TrackingModule.Module.Scope);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task Retention_removes_nothing_until_days_are_set_and_never_a_divergence(string engine)
    {
        var clock = new StateTestClock(DateTimeOffset.Parse("2026-11-07T00:00:00Z"));
        var database = Open(engine, clock);
        var store = new StateTrackingStore(database);
        await store.WriteAsync(Production, Log, Rows(1, 3), default);
        new StateTrackingDivergences(database).Mark(Production, Log.Name, "test", null, null, null);
        clock.Advance(TimeSpan.FromDays(400));

        new RetentionJob(database).Run();

        Assert.Equal(3, (await store.ReadAfterAsync(Production, Log, null, 10, default)).Count);
        Assert.Single(new StateTrackingDivergences(database).List());

        new RetentionJob(database, new RetentionSettings { Days = 30 }).Run();      // a general figure does not reach a rule that has no default age

        Assert.Equal(3, (await store.ReadAfterAsync(Production, Log, null, 10, default)).Count);

        new RetentionJob(database, new RetentionSettings { Tables = new Dictionary<string, double> { ["tracking.tracking_row"] = 30 } }).Run();

        Assert.Empty((await store.ReadAfterAsync(Production, Log, null, 10, default)));
        Assert.Single(new StateTrackingDivergences(database).List());
    }

    // ---------- the state source and sink ----------

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task The_state_store_reads_rows_after_a_position_in_order_including_rows_that_share_an_ordering_value(string engine)
    {
        var store = new StateTrackingStore(Open(engine));
        await store.WriteAsync(Production, Log, [new TrackingRow("b", 10, "{}"), new TrackingRow("a", 10, "{}"), new TrackingRow("c", 10, "{}"), new TrackingRow("d", 11, "{}")], default);

        var all = await store.ReadAfterAsync(Production, Log, null, 10, default);
        var after = await store.ReadAfterAsync(Production, Log, new TrackingPosition(10, "a"), 10, default);

        Assert.Equal(["a", "b", "c", "d"], all.Select(r => r.Key));
        Assert.Equal(["b", "c", "d"], after.Select(r => r.Key));
        Assert.Equal(new TrackingPosition(11, "d"), await store.HighWaterMarkAsync(Production, Log, default));
        Assert.Equal([new TrackingPosition(10, "c"), new TrackingPosition(11, "d")], await store.TailAsync(Production, Log, 2, default));
        Assert.Null(await store.HighWaterMarkAsync(Development, Log, default));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task Writing_a_row_twice_keeps_one_row_and_the_document_is_text_exactly_as_given(string engine)
    {
        var store = new StateTrackingStore(Open(engine));
        const string document = "{ \"id\" : 1,   \"note\": \"plaï\\n\" }";   // spacing and escapes must survive: it is text, not a JSON document the engine rewrites

        await store.WriteAsync(Production, Log, [new TrackingRow("r1", 1, document)], default);
        await store.WriteAsync(Production, Log, [new TrackingRow("r1", 1, document)], default);

        var row = Assert.Single(await store.ReadAfterAsync(Production, Log, null, 10, default));
        Assert.Equal(document, row.Document);
    }

    // ---------- the follower ----------

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task The_follower_copies_what_the_primary_added_and_running_twice_copies_nothing_new(string engine)
    {
        var database = Open(engine);
        var primary = new InMemoryTrackingStore();
        var copy = new StateTrackingStore(database);
        primary.Add(Production, Log, Rows(1, 7));
        var follower = Follower(database, primary, copy);

        var first = await follower.RunOnceAsync();
        var second = await follower.RunOnceAsync();
        primary.Add(Production, Log, Rows(8, 9));
        var third = await follower.RunOnceAsync();

        Assert.Equal((7, true, null), (first.Copied, first.CaughtUp, first.Error));
        Assert.Equal((0, true, null), (second.Copied, second.CaughtUp, second.Error));
        Assert.Equal(2, third.Copied);
        Assert.Equal(primary.Rows(Production, Log).Select(r => r.Key), (await copy.ReadAfterAsync(Production, Log, null, 100, default)).Select(r => r.Key));
        Assert.Equal(new TrackingPosition(9, "r0009"), follower.Position);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task A_copy_made_in_bounded_batches_catches_up_across_runs(string engine)
    {
        var database = Open(engine);
        var primary = new CountingStore(new InMemoryTrackingStore());
        primary.Inner.Add(Production, Log, Rows(1, 25));
        var copy = new StateTrackingStore(database);
        var follower = Follower(database, primary, copy, options: new TrackingFollowerOptions { BatchSize = 10, MaxBatchesPerRun = 2 });

        var first = await follower.RunOnceAsync();
        var second = await follower.RunOnceAsync();

        Assert.Equal((20, false), (first.Copied, first.CaughtUp));    // two batches of ten, then the run stops: bounded
        Assert.Equal((5, true), (second.Copied, second.CaughtUp));
        Assert.All(primary.BatchSizes, size => Assert.True(size <= 10));
        Assert.Equal(25, (await copy.ReadAfterAsync(Production, Log, null, 100, default)).Count);

        var again = Follower(database, primary, copy, options: new TrackingFollowerOptions { BatchSize = 10, MaxBatchesPerRun = 2 });
        primary.Inner.Add(Production, Log, Rows(26, 40));
        var catchUp = await again.CatchUpAsync(TimeSpan.FromSeconds(30));
        Assert.True(catchUp.CaughtUp);
        Assert.Equal(15, catchUp.Copied);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task The_checkpoint_moves_in_the_same_transaction_as_the_rows_of_a_state_copy(string engine)
    {
        var database = Open(engine);
        var primary = new InMemoryTrackingStore();
        primary.Add(Production, Log, Rows(1, 5));
        var copy = new FailingAfterWriteSink(new StateTrackingStore(database));
        var follower = Follower(database, primary, copy);

        // the rows are written inside Advance's transaction; making the checkpoint's own write fail afterwards (a failure the sink raises after the rows) must leave neither
        var failed = await follower.RunOnceAsync();

        Assert.NotNull(failed.Error);
        Assert.Equal(0, failed.Copied);
        Assert.Null(follower.Position);
        Assert.Empty(await new StateTrackingStore(database).ReadAfterAsync(Production, Log, null, 100, default));

        copy.Fail = false;
        var ok = await follower.RunOnceAsync();
        Assert.Equal((5, true), (ok.Copied, ok.CaughtUp));
        Assert.Equal(new TrackingPosition(5, "r0005"), follower.Position);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task A_crash_between_the_write_and_the_checkpoint_of_another_kind_of_copy_only_repeats_work(string engine)
    {
        var database = Open(engine);
        var primary = new InMemoryTrackingStore();
        primary.Add(Production, Log, Rows(1, 6));
        var copy = new InMemoryTrackingStore();      // not a state sink: the rows are written first, then the checkpoint moves
        var follower = Follower(database, primary, copy);
        // simulate the crash: the copy already has the rows (the write was durable) but the checkpoint never moved
        await copy.WriteAsync(Production, Log, Rows(1, 6), default);
        Assert.Null(follower.Position);

        var result = await follower.RunOnceAsync();

        Assert.True(result.CaughtUp);
        Assert.Equal(6, copy.Rows(Production, Log).Count);       // no duplicates
        Assert.Equal(new TrackingPosition(6, "r0006"), follower.Position);
    }

    // ---------- a down or slow side ----------

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task A_copy_that_throws_is_an_error_in_the_result_and_the_next_run_catches_up(string engine)
    {
        var database = Open(engine);
        var primary = new InMemoryTrackingStore();
        primary.Add(Production, Log, Rows(1, 4));
        var copy = new ChaosSink(new InMemoryTrackingStore()) { Mode = Chaos.Throw };
        var follower = Follower(database, primary, copy);

        var down = await follower.RunOnceAsync();

        Assert.False(down.CaughtUp);
        Assert.Contains("copy is down", down.Error);
        Assert.Null(follower.Position);          // the checkpoint did not move

        copy.Mode = Chaos.None;
        var up = await follower.RunOnceAsync();
        Assert.Equal((4, true), (up.Copied, up.CaughtUp));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task A_copy_that_blocks_never_holds_up_the_caller_beyond_the_budget(string engine)
    {
        var database = Open(engine);
        var primary = new InMemoryTrackingStore();
        primary.Add(Production, Log, Rows(1, 4));
        using var release = new ManualResetEventSlim();
        var copy = new ChaosSink(new InMemoryTrackingStore()) { Mode = Chaos.Block, Release = release };
        var follower = Follower(database, primary, copy, options: new TrackingFollowerOptions { RunTimeLimit = TimeSpan.FromMilliseconds(150) });

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var run = await follower.RunOnceAsync();
        var runTime = clock.Elapsed;
        clock.Restart();
        var catchUp = await follower.CatchUpAsync(TimeSpan.FromMilliseconds(100));
        var catchUpTime = clock.Elapsed;
        clock.Restart();
        var noWait = await follower.CatchUpAsync(TimeSpan.Zero);
        var noWaitTime = clock.Elapsed;

        Assert.False(run.CaughtUp);
        Assert.NotNull(run.Error);
        Assert.True(runTime < TimeSpan.FromSeconds(5), $"RunOnce took {runTime}");
        Assert.False(catchUp.CaughtUp);
        Assert.True(catchUpTime < TimeSpan.FromSeconds(5), $"CatchUp took {catchUpTime}");
        Assert.False(noWait.CaughtUp);
        Assert.True(noWaitTime < TimeSpan.FromMilliseconds(100));
        Assert.Null(follower.Position);

        release.Set();      // the blocked side wakes: the abandoned run ends and a later run copies
        CaughtUpEventually(follower);
    }

    private static void CaughtUpEventually(TrackingFollower follower)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        TrackingRunResult? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = follower.RunOnceAsync().GetAwaiter().GetResult();
            if (last.CaughtUp && follower.Position is not null) return;
            Thread.Sleep(50);
        }

        Assert.Fail($"the follower never caught up after the side recovered: {last?.Error}");
    }

    [Fact]
    public async Task A_primary_that_is_down_is_an_error_not_an_exception()
    {
        var copy = new InMemoryTrackingStore();
        var follower = new TrackingFollower(Production, Log, new ChaosSink(new InMemoryTrackingStore()) { Mode = Chaos.Throw }, copy, ProgressNeverUsed(), new InMemoryTrackingDivergences());

        var result = await follower.RunOnceAsync();

        Assert.False(result.CaughtUp);
        Assert.Contains("copy is down", result.Error);
    }

    private static ProgressStore ProgressNeverUsed()
    {
        // a progress store on a scratch SQLite file, for the tests whose side fails before the checkpoint is touched
        var dir = Directory.CreateTempSubdirectory("dbdatakit-tracking-");
        return new ProgressStore(StateDatabase.OpenModules(Path.Combine(dir.FullName, "state.db"), [ProgressModule.Module, TrackingModule.Module], StateScope.Standalone), Key);
    }

    // ---------- divergence ----------

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task A_restored_primary_is_marked_diverged_at_the_point_where_the_sides_part_and_copying_stops(string engine)
    {
        var database = Open(engine);
        var primary = new InMemoryTrackingStore();
        var copy = new StateTrackingStore(database);
        primary.Add(Production, Log, Rows(1, 10));
        var follower = Follower(database, primary, copy);
        Assert.True((await follower.RunOnceAsync()).CaughtUp);

        primary.Replace(Production, Log, Rows(1, 6));                                 // a backup from before rows 7 to 10 was restored
        primary.Add(Production, Log, After(7, 8));                                    // and the target went on with other rows
        var result = await follower.RunOnceAsync();

        Assert.True(result.Diverged);
        Assert.False(result.CaughtUp);
        var divergence = Assert.IsType<TrackingDivergence>(follower.Divergence);
        Assert.Equal(TrackingDivergence.Diverged, divergence.Status);
        Assert.Equal(new TrackingPosition(6, "r0006"), divergence.Common);
        Assert.Equal(new TrackingPosition(10, "r0010"), divergence.CopyHigh);
        Assert.Equal(Production, divergence.Identity);
        Assert.Equal(Log.Name, divergence.Stream);

        primary.Add(Production, Log, Rows(9, 12, "later"));
        var stopped = await follower.RunOnceAsync();      // nothing is copied while diverged
        Assert.True(stopped.Diverged);
        Assert.Equal(0, stopped.Copied);
        Assert.Equal(10, (await copy.ReadAfterAsync(Production, Log, null, 100, default)).Count);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task A_primary_whose_newest_row_is_behind_the_checkpoint_is_diverged_even_when_it_is_empty(string engine)
    {
        var database = Open(engine);
        var primary = new InMemoryTrackingStore();
        var copy = new StateTrackingStore(database);
        primary.Add(Production, Log, Rows(1, 4));
        var follower = Follower(database, primary, copy);
        await follower.RunOnceAsync();

        primary.Replace(Production, Log);     // restored to an empty target
        var result = await follower.RunOnceAsync();

        Assert.True(result.Diverged);
        Assert.Contains("restored", result.Divergence!.Reason);
        Assert.Null(result.Divergence.Common);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task A_copy_that_holds_rows_the_primary_does_not_is_diverged_by_the_digest_of_its_newest_keys(string engine)
    {
        var database = Open(engine);
        var primary = new InMemoryTrackingStore();
        var copy = new StateTrackingStore(database);
        primary.Add(Production, Log, Rows(1, 5));
        var follower = Follower(database, primary, copy, options: new TrackingFollowerOptions { TailSize = 4 });
        await follower.RunOnceAsync();
        await copy.WriteAsync(Production, Log, [Row(6, "only-in-the-copy")], default);       // someone wrote to the copy behind the follower's back

        primary.Add(Production, Log, [new TrackingRow("r0099", 6, "{}")]);                  // the primary's own row 6 is a different row
        var result = await follower.RunOnceAsync();

        Assert.True(result.Diverged);
        Assert.Contains("digest", result.Divergence!.Reason);
        Assert.Equal(new TrackingPosition(5, "r0005"), result.Divergence.Common);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task A_copy_that_is_merely_behind_is_not_diverged(string engine)
    {
        var database = Open(engine);
        var primary = new InMemoryTrackingStore();
        var copy = new StateTrackingStore(database);
        primary.Add(Production, Log, Rows(1, 30));
        var follower = Follower(database, primary, copy, options: new TrackingFollowerOptions { BatchSize = 10, MaxBatchesPerRun = 1, TailSize = 5 });

        for (var i = 0; i < 3; i++)
        {
            var result = await follower.RunOnceAsync();
            Assert.False(result.Diverged);
        }

        primary.Add(Production, Log, Rows(31, 100));          // the copy falls far behind: older than its tail window
        for (var i = 0; i < 4; i++) Assert.False((await follower.RunOnceAsync()).Diverged);
        Assert.Null(follower.Divergence);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task Resolving_by_archive_keeps_the_copys_later_rows_aside_and_copies_on_from_the_common_point(string engine)
    {
        var database = Open(engine);
        var primary = new InMemoryTrackingStore();
        var copy = new StateTrackingStore(database);
        var audit = new AuditLog(database, "build");
        primary.Add(Production, Log, Rows(1, 10));
        var follower = Follower(database, primary, copy, audit: audit);
        await follower.RunOnceAsync();
        primary.Replace(Production, Log, Rows(1, 6));
        primary.Add(Production, Log, After(7, 8));
        Assert.True((await follower.RunOnceAsync()).Diverged);
        var divergenceId = follower.Divergence!.Id;

        Assert.True(await follower.ResolveAsync(TrackingResolution.Archive, "ada"));

        Assert.Null(follower.Divergence);
        Assert.Equal(new TrackingPosition(6, "r0006"), follower.Position);
        Assert.Equal(4, copy.ArchivedCount(Production, Log, $"divergence-{divergenceId}"));
        Assert.Equal(6, (await copy.ReadAfterAsync(Production, Log, null, 100, default)).Count);       // the live stream is back to the common rows
        var resumed = await follower.RunOnceAsync();
        Assert.Equal((2, true, false), (resumed.Copied, resumed.CaughtUp, resumed.Diverged));
        Assert.Equal(primary.Rows(Production, Log).Select(r => r.Document), (await copy.ReadAfterAsync(Production, Log, null, 100, default)).Select(r => r.Document));

        var record = Assert.Single(audit.Read(new AuditQuery(Verb: "tracking.resolve")));
        Assert.Equal(("ada", "ok"), (record.Entry.ActorId, record.Entry.Outcome));
        Assert.Contains("Archive", record.Entry.Detail);
        var kept = Assert.Single(new StateTrackingDivergences(database).List(Production));      // the divergence stays as a record
        Assert.Equal((TrackingDivergence.Archived, "ada"), (kept.Status, kept.ResolvedBy));
        Assert.False(await follower.ResolveAsync(TrackingResolution.Archive, "ada"));         // nothing left to resolve
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task Resolving_by_reset_deletes_the_copys_later_rows_and_restarts_from_the_common_point(string engine)
    {
        var database = Open(engine);
        var primary = new InMemoryTrackingStore();
        var copy = new StateTrackingStore(database);
        var audit = new AuditLog(database, "build");
        primary.Add(Production, Log, Rows(1, 10));
        var follower = Follower(database, primary, copy, audit: audit);
        await follower.RunOnceAsync();
        primary.Replace(Production, Log, Rows(1, 3));
        Assert.True((await follower.RunOnceAsync()).Diverged);
        var divergenceId = follower.Divergence!.Id;

        Assert.True(await follower.ResolveAsync(TrackingResolution.ResetCopy, "grace"));

        Assert.Equal(3, (await copy.ReadAfterAsync(Production, Log, null, 100, default)).Count);
        Assert.Equal(0, copy.ArchivedCount(Production, Log, $"divergence-{divergenceId}"));
        primary.Add(Production, Log, Rows(4, 5, "new"));
        var resumed = await follower.RunOnceAsync();
        Assert.Equal((2, true), (resumed.Copied, resumed.CaughtUp));
        Assert.Contains("ResetCopy", Assert.Single(audit.Read(new AuditQuery(Verb: "tracking.resolve"))).Entry.Detail);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task A_divergence_with_nothing_in_common_resets_to_the_start(string engine)
    {
        var database = Open(engine);
        var primary = new InMemoryTrackingStore();
        var copy = new StateTrackingStore(database);
        primary.Add(Production, Log, Rows(1, 4));
        var follower = Follower(database, primary, copy);
        await follower.RunOnceAsync();
        primary.Replace(Production, Log);

        Assert.True((await follower.RunOnceAsync()).Diverged);
        Assert.True(await follower.ResolveAsync(TrackingResolution.ResetCopy, "ada"));

        Assert.Empty(await copy.ReadAfterAsync(Production, Log, null, 100, default));
        Assert.Null(follower.Position);
        primary.Add(Production, Log, Rows(1, 2, "fresh"));
        Assert.Equal(2, (await follower.RunOnceAsync()).Copied);
    }

    // ---------- identity ----------

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task Two_identities_with_the_same_name_stay_separate_in_one_copy_and_one_follower_never_reads_or_marks_the_other(string engine)
    {
        var database = Open(engine);
        var copy = new StateTrackingStore(database);
        var divergences = new StateTrackingDivergences(database);
        var production = new InMemoryTrackingStore();
        var development = new InMemoryTrackingStore();      // a development copy: production's rows, restored, under another server's identity
        production.Add(Production, Log, Rows(1, 8));
        development.Add(Development, Log, Rows(1, 5));
        var prod = Follower(database, production, copy, Production, divergences: divergences);
        var dev = Follower(database, development, copy, Development, divergences: divergences);

        await prod.RunOnceAsync();
        await dev.RunOnceAsync();

        Assert.Equal(8, (await copy.ReadAfterAsync(Production, Log, null, 100, default)).Count);
        Assert.Equal(5, (await copy.ReadAfterAsync(Development, Log, null, 100, default)).Count);

        // the development copy's target is rebuilt (a new restore): only its identity diverges
        development.Replace(Development, Log, Rows(1, 2));
        Assert.True((await dev.RunOnceAsync()).Diverged);
        Assert.False((await prod.RunOnceAsync()).Diverged);
        Assert.Null(prod.Divergence);
        Assert.Equal(Development, Assert.Single(divergences.List()).Identity);

        // a follower for B whose primary holds only A's rows copies nothing and touches nothing of A's
        var strayPrimary = new InMemoryTrackingStore();
        strayPrimary.Add(Production, Log, Rows(1, 20));
        var stray = Follower(database, strayPrimary, copy, "mssql://other/Other", divergences: divergences);
        var result = await stray.RunOnceAsync();
        Assert.Equal((0, true), (result.Copied, result.CaughtUp));
        Assert.Equal(8, (await copy.ReadAfterAsync(Production, Log, null, 100, default)).Count);
        Assert.Empty(await copy.ReadAfterAsync("mssql://other/Other", Log, null, 100, default));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task Each_identity_has_its_own_checkpoint(string engine)
    {
        var database = Open(engine);
        var copy = new StateTrackingStore(database);
        var primary = new InMemoryTrackingStore();
        primary.Add(Production, Log, Rows(1, 3));
        primary.Add(Development, Log, Rows(1, 9));

        await Follower(database, primary, copy, Production).RunOnceAsync();
        var dev = Follower(database, primary, copy, Development);

        Assert.Null(dev.Position);
        await dev.RunOnceAsync();
        Assert.Equal(new TrackingPosition(9, "r0009"), dev.Position);
        Assert.Equal(new TrackingPosition(3, "r0003"), Follower(database, primary, copy, Production).Position);
    }

    // ---------- settings and switching ----------

    [Fact]
    public void Settings_are_validated_both_needs_a_primary_and_the_single_sides_forbid_one()
    {
        Assert.Equal((TrackingMode.Both, TrackingSide.Target), (TrackingSettings.Default.Mode, TrackingSettings.Default.PrimarySide));
        Assert.Equal(TrackingSide.State, TrackingSettings.Both(TrackingSide.State).Primary);
        Assert.Equal(TrackingSide.Target, TrackingSettings.TargetOnly.PrimarySide);
        Assert.Equal(TrackingSide.State, TrackingSettings.StateOnly.PrimarySide);
        Assert.Null(TrackingSettings.TargetOnly.Primary);

        Assert.Contains("must say which", Assert.Throws<ArgumentException>(() => TrackingSettings.Create(TrackingMode.Both, null)).Message);
        Assert.Contains("takes no primary", Assert.Throws<ArgumentException>(() => TrackingSettings.Create(TrackingMode.Target, TrackingSide.State)).Message);
        Assert.Contains("takes no primary", Assert.Throws<ArgumentException>(() => TrackingSettings.Create(TrackingMode.State, TrackingSide.State)).Message);
        Assert.Contains("not a tracking mode", Assert.Throws<ArgumentException>(() => TrackingSettings.Create((TrackingMode)9, null)).Message);

        Assert.Equal(TrackingSettings.Both(TrackingSide.State), TrackingSettings.Parse("Both", "state"));
        Assert.Equal(TrackingSettings.TargetOnly, TrackingSettings.Parse("target", null));
        Assert.Contains("'sideways'", Assert.Throws<ArgumentException>(() => TrackingSettings.Parse("both", "sideways")).Message);
        Assert.Contains("'7'", Assert.Throws<ArgumentException>(() => TrackingSettings.Parse("7", null)).Message);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task Switching_the_primary_first_brings_the_copy_up_to_date_then_returns_the_new_setting_and_records_it(string engine)
    {
        var database = Open(engine);
        var primary = new InMemoryTrackingStore();
        var copy = new StateTrackingStore(database);
        primary.Add(Production, Log, Rows(1, 12));
        var audit = new AuditLog(database, "build");
        var follower = Follower(database, primary, copy);

        var result = await TrackingSettings.Default.SwitchPrimaryAsync(TrackingSide.State, [follower], TimeSpan.FromSeconds(30), "ada", Production, audit);

        Assert.True(result.Switched);
        Assert.Equal(TrackingSettings.Both(TrackingSide.State), result.Settings);
        Assert.Equal(12, (await copy.ReadAfterAsync(Production, Log, null, 100, default)).Count);       // the new primary holds everything before it becomes primary
        var record = Assert.Single(audit.Read(new AuditQuery(Verb: "tracking.switch_primary")));
        Assert.Equal(("ada", "ok", Production), (record.Entry.ActorId, record.Entry.Outcome, record.Entry.TargetId));
        Assert.Matches("\"to\":\\s*\"state\"", record.Entry.Detail);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task Switching_is_refused_with_a_message_when_the_copy_cannot_catch_up_and_nothing_changes(string engine)
    {
        var database = Open(engine);
        var primary = new InMemoryTrackingStore();
        primary.Add(Production, Log, Rows(1, 5));
        var audit = new AuditLog(database, "build");
        var down = Follower(database, primary, new ChaosSink(new InMemoryTrackingStore()) { Mode = Chaos.Throw }, audit: audit);

        var result = await TrackingSettings.Default.SwitchPrimaryAsync(TrackingSide.State, [down], TimeSpan.FromSeconds(5), "ada", Production, audit);

        Assert.False(result.Switched);
        Assert.Equal(TrackingSettings.Default, result.Settings);
        Assert.Contains("could not be brought up to date", result.Message);
        Assert.Equal("refused", Assert.Single(audit.Read(new AuditQuery(Verb: "tracking.switch_primary"))).Entry.Outcome);

        var slow = Follower(database, primary, new ChaosSink(new InMemoryTrackingStore()) { Mode = Chaos.Block, Release = new ManualResetEventSlim() });
        var late = await TrackingSettings.Default.SwitchPrimaryAsync(TrackingSide.State, [slow], TimeSpan.FromMilliseconds(200), "ada", Production);
        Assert.False(late.Switched);
        Assert.NotNull(late.Message);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task Switching_is_refused_while_a_stream_is_diverged_and_for_a_mode_that_has_one_side(string engine)
    {
        var database = Open(engine);
        var primary = new InMemoryTrackingStore();
        var copy = new StateTrackingStore(database);
        primary.Add(Production, Log, Rows(1, 5));
        var follower = Follower(database, primary, copy);
        await follower.RunOnceAsync();
        primary.Replace(Production, Log, Rows(1, 2));

        var diverged = await TrackingSettings.Default.SwitchPrimaryAsync(TrackingSide.State, [follower], TimeSpan.FromSeconds(5), "ada", Production);
        var single = await TrackingSettings.TargetOnly.SwitchPrimaryAsync(TrackingSide.State, [follower], TimeSpan.FromSeconds(5), "ada", Production);
        var same = await TrackingSettings.Default.SwitchPrimaryAsync(TrackingSide.Target, [follower], TimeSpan.FromSeconds(5), "ada", Production);
        var other = await TrackingSettings.Default.SwitchPrimaryAsync(TrackingSide.State, [follower], TimeSpan.FromSeconds(5), "ada", Development);

        Assert.Contains("diverged", diverged.Message);
        Assert.Contains("keeps both sides", single.Message);
        Assert.Contains("already the primary", same.Message);
        Assert.Contains("another connection", other.Message);
        Assert.All(new[] { diverged, single, same, other }, r => Assert.False(r.Switched));
    }

    [Fact]
    public void A_stream_names_its_ordering_and_row_keys_among_its_columns()
    {
        Assert.Contains("ordering key", Assert.Throws<ArgumentException>(() => new TrackingStream("s", ["a"], "b", "a")).Message);
        Assert.Contains("row key", Assert.Throws<ArgumentException>(() => new TrackingStream("s", ["a"], "a", "b")).Message);
    }

    // ---------- test sides ----------

    private enum Chaos { None, Throw, Block }

    /// <summary>A side that is down (throws) or stuck (blocks without looking at the cancellation token) until released.</summary>
    private sealed class ChaosSink(ITrackingSink inner) : ITrackingSink
    {
        public Chaos Mode { get; set; }

        public ManualResetEventSlim? Release { get; set; }

        private void Misbehave()
        {
            if (Mode == Chaos.Throw) throw new IOException("the copy is down");
            if (Mode == Chaos.Block) Release!.Wait(TimeSpan.FromMinutes(2));      // stuck, but never for ever: a thread left blocked by a test would starve the pool of the tests that run beside it
        }

        public Task<IReadOnlyList<TrackingRow>> ReadAfterAsync(string identity, TrackingStream stream, TrackingPosition? after, int limit, CancellationToken ct) { Misbehave(); return inner.ReadAfterAsync(identity, stream, after, limit, ct); }

        public Task<TrackingPosition?> HighWaterMarkAsync(string identity, TrackingStream stream, CancellationToken ct) { Misbehave(); return inner.HighWaterMarkAsync(identity, stream, ct); }

        public Task<IReadOnlyList<TrackingPosition>> TailAsync(string identity, TrackingStream stream, int count, CancellationToken ct) { Misbehave(); return inner.TailAsync(identity, stream, count, ct); }

        public Task WriteAsync(string identity, TrackingStream stream, IReadOnlyList<TrackingRow> rows, CancellationToken ct) { Misbehave(); return inner.WriteAsync(identity, stream, rows, ct); }

        public Task<int> ArchiveAfterAsync(string identity, TrackingStream stream, TrackingPosition? common, string marker, CancellationToken ct) { Misbehave(); return inner.ArchiveAfterAsync(identity, stream, common, marker, ct); }

        public Task<int> DeleteAfterAsync(string identity, TrackingStream stream, TrackingPosition? common, CancellationToken ct) { Misbehave(); return inner.DeleteAfterAsync(identity, stream, common, ct); }
    }

    /// <summary>A primary that records how many rows each read was asked for and returned.</summary>
    private sealed class CountingStore(InMemoryTrackingStore inner) : ITrackingSource
    {
        public InMemoryTrackingStore Inner { get; } = inner;

        public List<int> BatchSizes { get; } = [];

        public async Task<IReadOnlyList<TrackingRow>> ReadAfterAsync(string identity, TrackingStream stream, TrackingPosition? after, int limit, CancellationToken ct)
        {
            var rows = await Inner.ReadAfterAsync(identity, stream, after, limit, ct);
            BatchSizes.Add(rows.Count);
            return rows;
        }

        public Task<TrackingPosition?> HighWaterMarkAsync(string identity, TrackingStream stream, CancellationToken ct) => Inner.HighWaterMarkAsync(identity, stream, ct);

        public Task<IReadOnlyList<TrackingPosition>> TailAsync(string identity, TrackingStream stream, int count, CancellationToken ct) => Inner.TailAsync(identity, stream, count, ct);
    }

    /// <summary>A state sink that writes its rows inside the follower's transaction and then fails, as a crash right after the rows would: the checkpoint must not have moved and the rows must not be there.</summary>
    private sealed class FailingAfterWriteSink(StateTrackingStore inner) : IStateTrackingSink
    {
        public bool Fail { get; set; } = true;

        public StateDatabase Database => inner.Database;

        public void Write(DbConnection connection, DbTransaction transaction, string identity, TrackingStream stream, IReadOnlyList<TrackingRow> rows)
        {
            inner.Write(connection, transaction, identity, stream, rows);
            if (Fail) throw new InvalidOperationException("crash after the rows were written");
        }

        public Task<IReadOnlyList<TrackingRow>> ReadAfterAsync(string identity, TrackingStream stream, TrackingPosition? after, int limit, CancellationToken ct) => inner.ReadAfterAsync(identity, stream, after, limit, ct);

        public Task<TrackingPosition?> HighWaterMarkAsync(string identity, TrackingStream stream, CancellationToken ct) => inner.HighWaterMarkAsync(identity, stream, ct);

        public Task<IReadOnlyList<TrackingPosition>> TailAsync(string identity, TrackingStream stream, int count, CancellationToken ct) => inner.TailAsync(identity, stream, count, ct);

        public Task WriteAsync(string identity, TrackingStream stream, IReadOnlyList<TrackingRow> rows, CancellationToken ct) => inner.WriteAsync(identity, stream, rows, ct);

        public Task<int> ArchiveAfterAsync(string identity, TrackingStream stream, TrackingPosition? common, string marker, CancellationToken ct) => inner.ArchiveAfterAsync(identity, stream, common, marker, ct);

        public Task<int> DeleteAfterAsync(string identity, TrackingStream stream, TrackingPosition? common, CancellationToken ct) => inner.DeleteAfterAsync(identity, stream, common, ct);
    }
}
