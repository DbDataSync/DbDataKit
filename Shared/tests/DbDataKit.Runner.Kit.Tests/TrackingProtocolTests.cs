using DbDataKit.Runner.Kit.Tests.Samples;
using DbDataKit.Runner.Protocol.V1;
using DbDataKit.State;
using DbDataKit.State.Tracking;
using DbDataKit.Tracking.Build;
using Grpc.Core;

namespace DbDataKit.Runner.Kit.Tests.Samples
{
    /// <summary>What the follower in the task's process is told: the identity of the tracking store it reads and the stream it copies.</summary>
    public sealed record TrackingCopyInput(string Identity, string Stream);

    /// <summary>Plan 28c E16: a job that is the follower of one stream: it reads a fake target (<see cref="Primary"/>) and copies into the runner's state through the protocol.</summary>
    [RunnerTask("tracking-copier")]
    public sealed class TrackingCopier : RunnerJob<TrackingCopyInput, NoProgress, NoSlots>
    {
        public static InMemoryTrackingStore Primary { get; set; } = new();

        public static TrackingRunResult? Last { get; set; }

        protected override async Task RunAsync(CancellationToken ct)
        {
            var stream = BuildStreams.Get(Input.Stream).Stream;
            var follower = new TrackingFollower(Input.Identity, stream, Primary, Tracking);
            Last = await follower.CatchUpAsync(TimeSpan.FromSeconds(20), ct);
        }
    }
}

namespace DbDataKit.Runner.Kit.Tests
{
    /// <summary>
    /// Plan 28c E16: Build's tracking copied into the runner's <c>build_tracking</c> module through the protocol by a follower that runs in the task's process: rows and checkpoint move in one transaction on the runner,
    /// every identity must end with a connection the run holds a handle for, and a copy that parts from its target is marked diverged where the runner keeps the copy.
    /// </summary>
    public sealed class TrackingProtocolTests
    {
        private const string Identity = "buildtracking:sqlserver://sql1/warehouse.dbo.tracking@warehouse";
        private const string Stream = "schema_version";

        private static readonly IReadOnlyDictionary<string, string> Handles = new Dictionary<string, string> { ["warehouse"] = "prod-sql" };

        private static TrackingRow Row(int n) => new($"obj{n}|{n:D19}", n * 1000L, $"{{\"object_name\":\"obj{n}\",\"n\":{n}}}");

        private static TrackingStream Of(string name) => BuildStreams.Get(name).Stream;

        private static (string Key, string Value)[] Parts(Checkpoint c) => [.. c.Parts.OrderBy(p => p.Key).Select(p => (p.Key, p.Value.Type == ProgressType.Int ? p.Value.AsInt.ToString(System.Globalization.CultureInfo.InvariantCulture) : p.Value.AsText))];

        [Theory]
        [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
        public async Task A_follower_in_the_task_copies_a_stream_into_the_runners_state_and_the_next_run_copies_only_what_is_new(string engine)
        {
            using var h = new Harness(engine, remote: true) { Handles = Handles };
            TrackingCopier.Primary = new InMemoryTrackingStore();
            TrackingCopier.Primary.Add(Identity, Of(Stream), Row(1), Row(2), Row(3));

            var first = await h.Run<TrackingCopier>(new TrackingCopyInput(Identity, Stream));

            Assert.Equal(TaskEndState.Succeeded, first.End.State);
            Assert.Equal(3, TrackingCopier.Last!.Copied);
            Assert.True(TrackingCopier.Last.CaughtUp);
            var store = new BuildTrackingStore(h.Database);
            Assert.Equal(["obj1", "obj2", "obj3"], store.Rows(Identity, Stream).Select(r => r.Key.Split('|')[0]));
            var checkpoint = new ProgressStore(h.Database, BuildTrackingState.ProgressKey).GetCheckpoint(TrackingFollower.CheckpointNameOf(Stream), Identity)!;
            Assert.Equal(Row(3).Key, checkpoint.Parts[TrackingFollower.KeyPartName].AsText);
            Assert.Equal(3000, checkpoint.Parts[TrackingFollower.OrderPartName].AsInt);

            TrackingCopier.Primary.Add(Identity, Of(Stream), Row(4), Row(5));
            var second = await h.Run<TrackingCopier>(new TrackingCopyInput(Identity, Stream), attempt: 1, chain: Guid.NewGuid().ToString("D"));

            Assert.Equal(TaskEndState.Succeeded, second.End.State);
            Assert.Equal(2, TrackingCopier.Last!.Copied);
            Assert.Equal(5, store.Count(Identity, Stream));
            Assert.Equal(Row(5).Key, new ProgressStore(h.Database, BuildTrackingState.ProgressKey).GetCheckpoint(TrackingFollower.CheckpointNameOf(Stream), Identity)!.Parts[TrackingFollower.KeyPartName].AsText);
        }

        [Theory]
        [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
        public async Task A_target_restored_from_a_backup_is_marked_diverged_in_the_runners_state_and_nothing_more_is_copied(string engine)
        {
            using var h = new Harness(engine, remote: true) { Handles = Handles };
            TrackingCopier.Primary = new InMemoryTrackingStore();
            TrackingCopier.Primary.Add(Identity, Of(Stream), Row(1), Row(2), Row(3));
            await h.Run<TrackingCopier>(new TrackingCopyInput(Identity, Stream));

            // the target was restored from before row 3 and its history went on differently
            TrackingCopier.Primary.Replace(Identity, Of(Stream), Row(1), Row(2));
            var result = await h.Run<TrackingCopier>(new TrackingCopyInput(Identity, Stream), chain: Guid.NewGuid().ToString("D"));

            Assert.Equal(TaskEndState.Succeeded, result.End.State);
            Assert.True(TrackingCopier.Last!.Diverged);
            var open = Assert.Single(new StateTrackingDivergences(h.Database).List(Identity));
            Assert.Equal((Stream, "diverged"), (open.Stream, open.Status));
            Assert.Equal(Row(2).Key, open.Common!.Key);
            Assert.Equal(3, new BuildTrackingStore(h.Database).Count(Identity, Stream));    // the copy keeps what it had until somebody resolves it
        }

        [Theory]
        [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
        public void Sync_entries_travel_under_their_own_identities_in_the_shared_tracking_module_and_the_two_kinds_are_not_mixed(string engine)
        {
            using var h = new Harness(engine, remote: true);
            using var run = new RawClient(h.Host.Endpoint, h.Grant("tracking-copier", handles: Handles));
            run.Start();
            const string entry = "syncposition:postgres://pg1/wh/orders@warehouse";
            var write = (string identity, string stream, bool advance, long version) => new TrackingWriteRequest { Identity = identity, Stream = stream, Advance = advance, ExpectedVersion = version, Rows = { new TrackingRowMessage { Key = "u1", Order = 1, Document = "{\"entry_seq\":1}" } } };

            Assert.True(run.Client.TrackingWrite(write(entry, SyncPositionLogCopy.Name, true, 0), run.Headers).Applied);
            var state = run.Client.TrackingState(new TrackingStreamRef { Identity = entry, Stream = SyncPositionLogCopy.Name }, run.Headers);
            Assert.Equal(1, state.Version);
            Assert.False(run.Client.TrackingWrite(write(entry, SyncPositionLogCopy.Name, true, 0), run.Headers).Applied);   // the version moved: nothing written
            Assert.Single(run.Client.TrackingTail(new TrackingTailRequest { Identity = entry, Stream = SyncPositionLogCopy.Name, Count = 5 }, run.Headers).Positions);
            Assert.Empty(run.Client.TrackingUnsettled(new TrackingUnsettledRequest { Identity = entry, Stream = SyncPositionLogCopy.Name, Limit = 5 }, run.Headers).Rows);

            // in the shared module, not Build's, and the divergence is the stream's
            Assert.Equal(1, new StateTrackingStore(h.Database).Count(entry, SyncPositionLogCopy.Name));
            var reply = run.Client.TrackingDivergence(new TrackingDivergenceRequest { Identity = entry, Stream = SyncPositionLogCopy.Name, Reason = "restored" }, run.Headers);
            Assert.Equal(SyncPositionLogCopy.Name, reply.Divergence.Stream);

            // the grant applies as for Build's, and a Build stream under a Sync identity (or the reverse) is refused
            Assert.Equal(StatusCode.PermissionDenied, Assert.Throws<RpcException>(() => run.Client.TrackingWrite(write("syncposition:postgres://pg1/wh/orders@other", SyncPositionLogCopy.Name, true, 0), run.Headers)).StatusCode);
            Assert.Equal(StatusCode.InvalidArgument, Assert.Throws<RpcException>(() => run.Client.TrackingWrite(write(entry, Stream, true, 0), run.Headers)).StatusCode);
            Assert.Equal(StatusCode.InvalidArgument, Assert.Throws<RpcException>(() => run.Client.TrackingWrite(write("buildtracking:x@warehouse", SyncPositionLogCopy.Name, true, 0), run.Headers)).StatusCode);
            Assert.Equal(StatusCode.InvalidArgument, Assert.Throws<RpcException>(() => run.Client.TrackingSnapshot(new TrackingSnapshotRequest { Identity = entry, Table = "object_info" }, run.Headers)).StatusCode);
            Assert.Equal(0, new BuildTrackingStore(h.Database).Count("buildtracking:x@warehouse", Stream));
        }

        [Theory]
        [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
        public void An_identity_must_end_with_a_connection_the_run_holds_and_a_stream_must_be_one_of_builds(string engine)
        {
            using var h = new Harness(engine, remote: true);
            using var run = new RawClient(h.Host.Endpoint, h.Grant("tracking-copier", handles: Handles));
            run.Start();
            var request = (string identity, string stream) => new TrackingWriteRequest { Identity = identity, Stream = stream, Advance = true, Rows = { new TrackingRowMessage { Key = "k", Order = 1, Document = "{}" } } };

            // the slot's name and the connection it is bound to are both the run's
            Assert.True(run.Client.TrackingWrite(request("buildtracking:x@warehouse", Stream), run.Headers).Applied);
            Assert.True(run.Client.TrackingWrite(request("buildtracking:y@prod-sql", Stream), run.Headers).Applied);

            foreach (var identity in new[] { "buildtracking:x@other", "buildtracking:x", "warehouse", "buildtracking:x@warehouse@evil", "@", "" })
            {
                var refused = Assert.Throws<RpcException>(() => run.Client.TrackingWrite(request(identity, Stream), run.Headers));
                Assert.Equal(StatusCode.PermissionDenied, refused.StatusCode);
            }

            // the same refusal on every call that names an identity
            Assert.Equal(StatusCode.PermissionDenied, Assert.Throws<RpcException>(() => run.Client.TrackingState(new TrackingStreamRef { Identity = "a@other", Stream = Stream }, run.Headers)).StatusCode);
            Assert.Equal(StatusCode.PermissionDenied, Assert.Throws<RpcException>(() => run.Client.TrackingTail(new TrackingTailRequest { Identity = "a@other", Stream = Stream, Count = 3 }, run.Headers)).StatusCode);
            Assert.Equal(StatusCode.PermissionDenied, Assert.Throws<RpcException>(() => run.Client.TrackingRead(new TrackingReadRequest { Identity = "a@other", Stream = Stream, Limit = 3 }, run.Headers)).StatusCode);
            Assert.Equal(StatusCode.PermissionDenied, Assert.Throws<RpcException>(() => run.Client.TrackingUnsettled(new TrackingUnsettledRequest { Identity = "a@other", Stream = "run_log", Limit = 3 }, run.Headers)).StatusCode);
            Assert.Equal(StatusCode.PermissionDenied, Assert.Throws<RpcException>(() => run.Client.TrackingDivergence(new TrackingDivergenceRequest { Identity = "a@other", Stream = Stream, Reason = "r" }, run.Headers)).StatusCode);
            Assert.Equal(StatusCode.PermissionDenied, Assert.Throws<RpcException>(() => run.Client.TrackingSnapshot(new TrackingSnapshotRequest { Identity = "a@other", Table = "object_info" }, run.Headers)).StatusCode);

            // a stream that is not one of Build's tables, a snapshot named as a stream, a document that is not JSON
            Assert.Equal(StatusCode.InvalidArgument, Assert.Throws<RpcException>(() => run.Client.TrackingWrite(request("a@warehouse", "users"), run.Headers)).StatusCode);
            Assert.Equal(StatusCode.InvalidArgument, Assert.Throws<RpcException>(() => run.Client.TrackingWrite(request("a@warehouse", "object_info"), run.Headers)).StatusCode);
            Assert.Equal(StatusCode.InvalidArgument, Assert.Throws<RpcException>(() => run.Client.TrackingSnapshot(new TrackingSnapshotRequest { Identity = "a@warehouse", Table = Stream }, run.Headers)).StatusCode);
            var broken = request("a@warehouse", Stream);
            broken.Rows[0].Document = "not json";
            Assert.Equal(StatusCode.InvalidArgument, Assert.Throws<RpcException>(() => run.Client.TrackingWrite(broken, run.Headers)).StatusCode);

            // nothing of the refused calls reached the module
            var store = new BuildTrackingStore(h.Database);
            Assert.Equal(1, store.Count("buildtracking:x@warehouse", Stream));
            Assert.Equal(0, store.Count("buildtracking:x@other", Stream));
            Assert.Equal(0, store.Count("a@warehouse", Stream));
        }

        [Theory]
        [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
        public void A_run_that_holds_no_connection_may_write_no_identity(string engine)
        {
            using var h = new Harness(engine, remote: true);
            using var run = new RawClient(h.Host.Endpoint, h.Grant("tracking-copier"));
            run.Start();

            var refused = Assert.Throws<RpcException>(() => run.Client.TrackingState(new TrackingStreamRef { Identity = "buildtracking:x@warehouse", Stream = Stream }, run.Headers));

            Assert.Equal(StatusCode.PermissionDenied, refused.StatusCode);
            Assert.Contains("holds none", refused.Status.Detail, StringComparison.Ordinal);
        }

        [Theory]
        [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
        public void The_rows_and_the_checkpoint_move_together_a_stale_version_writes_nothing_and_a_rewrite_leaves_the_checkpoint(string engine)
        {
            using var h = new Harness(engine, remote: true);
            using var run = new RawClient(h.Host.Endpoint, h.Grant("tracking-copier", handles: Handles));
            run.Start();
            var rows = (params int[] n) => n.Select(i => new TrackingRowMessage { Key = Row(i).Key, Order = Row(i).Order, Document = Row(i).Document });
            var write = (bool advance, long version, int[] n) =>
            {
                var request = new TrackingWriteRequest { Identity = Identity, Stream = Stream, Advance = advance, ExpectedVersion = version };
                request.Rows.AddRange(rows(n));
                return run.Client.TrackingWrite(request, run.Headers).Applied;
            };
            var state = () => run.Client.TrackingState(new TrackingStreamRef { Identity = Identity, Stream = Stream }, run.Headers);
            var store = new BuildTrackingStore(h.Database);

            Assert.Equal((false, 0L), (state().Position is not null, state().Version));
            Assert.True(write(true, 0, [1, 2]));
            Assert.Equal((Row(2).Order, Row(2).Key, 1L), (state().Position.Order, state().Position.Key, state().Version));

            // a follower that read the checkpoint before it moved: nothing is written, the checkpoint is where it was
            Assert.False(write(true, 0, [3]));
            Assert.Equal(2, store.Count(Identity, Stream));
            Assert.Equal(Row(2).Key, state().Position.Key);

            // a row seen again (a settled one) is rewritten without moving the checkpoint
            var changed = new TrackingWriteRequest { Identity = Identity, Stream = Stream, Advance = false };
            changed.Rows.Add(new TrackingRowMessage { Key = Row(1).Key, Order = Row(1).Order, Document = "{\"object_name\":\"obj1\",\"n\":99}" });
            Assert.True(run.Client.TrackingWrite(changed, run.Headers).Applied);
            Assert.Contains("99", store.Rows(Identity, Stream).First(r => r.Key == Row(1).Key).Document, StringComparison.Ordinal);
            Assert.Equal((Row(2).Key, 1L), (state().Position.Key, state().Version));

            // the copy's tail and rows as the task reads them back
            Assert.Equal([Row(1).Key, Row(2).Key], run.Client.TrackingTail(new TrackingTailRequest { Identity = Identity, Stream = Stream, Count = 10 }, run.Headers).Positions.Select(p => p.Key));
            var after = new TrackingReadRequest { Identity = Identity, Stream = Stream, Limit = 10, After = new TrackingPositionMessage { Order = Row(1).Order, Key = Row(1).Key } };
            Assert.Equal([Row(2).Key], run.Client.TrackingRead(after, run.Headers).Rows.Select(r => r.Key));
        }

        [Theory]
        [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
        public void An_updated_row_is_unsettled_while_its_step_runs_and_a_snapshot_is_replaced_whole(string engine)
        {
            using var h = new Harness(engine, remote: true);
            using var run = new RawClient(h.Host.Endpoint, h.Grant("tracking-copier", handles: Handles));
            run.Start();
            var request = new TrackingWriteRequest { Identity = Identity, Stream = "run_log", Advance = true, ExpectedVersion = 0 };
            request.Rows.Add(new TrackingRowMessage { Key = "r1|s1", Order = 5, Document = "{\"status\":\"started\"}" });
            request.Rows.Add(new TrackingRowMessage { Key = "r1|s2", Order = 6, Document = "{\"status\":\"ok\"}" });
            Assert.True(run.Client.TrackingWrite(request, run.Headers).Applied);

            var unsettled = Assert.Single(run.Client.TrackingUnsettled(new TrackingUnsettledRequest { Identity = Identity, Stream = "run_log", Limit = 10 }, run.Headers).Rows);
            Assert.Equal("r1|s1", unsettled.Key);
            Assert.Equal(BuildStreams.RowHash("{\"status\":\"started\"}"), unsettled.Hash);

            var snapshot = new TrackingSnapshotRequest { Identity = Identity, Table = "object_info" };
            snapshot.Rows.Add(new TrackingRowMessage { Key = "a", Order = 1, Document = "{\"x\":1}" });
            snapshot.Rows.Add(new TrackingRowMessage { Key = "b", Order = 1, Document = "{\"x\":2}" });
            Assert.Equal(2, run.Client.TrackingSnapshot(snapshot, run.Headers).Rows);
            snapshot.Rows.Clear();
            snapshot.Rows.Add(new TrackingRowMessage { Key = "c", Order = 2, Document = "{\"x\":3}" });
            Assert.Equal(1, run.Client.TrackingSnapshot(snapshot, run.Headers).Rows);
            Assert.Equal(["c"], new BuildTrackingStore(h.Database).Rows(Identity, "object_info").Select(r => r.Key));
        }
    }
}
