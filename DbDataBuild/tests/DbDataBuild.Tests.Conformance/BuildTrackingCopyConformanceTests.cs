using System.Security.Cryptography;
using System.Text;
using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Execution;
using DbDataBuild.State;
using DbDataKit.State;
using DbDataKit.State.Tracking;
using Xunit;
using AuditLog = DbDataBuild.Execution.AuditLog;

namespace DbDataBuild.Tests.Conformance;

/// <summary>
/// Plan 28c E16 on real engines, with rows written by Build's own writers: the module follows the conventions on the state's engine, the copy of a project's twelve tracking tables equals the target for every stream, an updated row reaches the copy with its final
/// status, a restored target is marked diverged, a development copy's rows stay under their own identity, and a layout 9 store is upgraded to layout 10 (and refused until then). The state database is the engine's own scratch database, beside the tracking tables.
/// </summary>
public class BuildTrackingCopyConformanceTests
{
    public static TheoryData<string> Engines => new() { "sqlserver", "postgres" };

    private const string SchemaName = "dbdatabuild";
    private static readonly string Hash = new('a', 64);

    private static LoginSettings Login(Engine e, Login which) => LoginSettings.FromEnvironment(e.Name, which, _ => e.ConnectionString).Settings!;

    private static async Task<MutationGate> GateAsync(Engine e) =>
        await MutationGate.OpenAsync(Login(e, DbDataBuild.Execution.Login.Write), "test", StatementKind.Tracking, new MemoryStatementLog(), Guid.NewGuid());

    private static StateDatabase OpenState(Engine e) =>
        StateOpener.Open(new StateSettings(e.Name == "sqlserver" ? StateEngineIds.MsSql : StateEngineIds.Postgres, e.ConnectionString), "unused.db", BuildTrackingState.Modules, BuildTrackingState.Scope);

    private const string ModelDocument = "{\"kind\":\"view\",\"title\":\"V one\",\"description\":\"The view\",\"upstream\":[{\"name\":\"src.t\",\"category\":\"model\",\"kind\":\"mapped\"}],\"columns\":[{\"name\":\"id\",\"title\":\"Identifier\"}]}";
    private const string MappedDocument = "{\"kind\":\"mapped\",\"title\":\"A table\",\"columns\":[{\"name\":\"id\"}]}";
    private const string ProjectDocument = "{\"title\":\"Sales\",\"config\":{\"connections\":{}}}";

    /// <summary>Every kind of row Build writes, through the writers it uses.</summary>
    private static async Task SeedAsync(MutationGate gate, TrackingScope scope, string tag = "")
    {
        await TrackingStore.RecordSchemaVersionAsync(gate, scope, "s1", "marts.v1" + tag, Hash, null, "tool", "p1", null);
        await TrackingStore.RecordSchemaVersionAsync(gate, scope, "s2", "marts.v1" + tag, new string('b', 64), Hash, "out_of_band", null, null);
        await AuditLog.MigrationAsync(gate, scope, "m1", "p1" + tag, Hash, "plan text", "abc123", "me", "started", Hash, null, "refresh", Hash);
        await AuditLog.PlanMemberAsync(gate, scope, "x1", "p1" + tag, 1, "member", "marts.v1", "load", Hash, true);
        await AuditLog.PlanMemberAsync(gate, scope, "x2", "p1" + tag, 2, "member", "marts.v2", "load", Hash, false);
        await AuditLog.PlanMemberAsync(gate, scope, "x10", "p1" + tag, 10, "member", "marts.v3", "load", Hash, true);
        var ddl = Guid.NewGuid();
        await AuditLog.BeginDdlAsync(gate, scope, "d1", ddl, "marts.v1" + tag, "CREATE VIEW", Hash, null, "me", "p1" + tag, null);
        await AuditLog.FinishDdlAsync(gate, scope, "d1", ddl, "ok", Hash);
        var run = Guid.NewGuid();
        await AuditLog.BeginRunAsync(gate, scope, "r1", run, "marts.v1" + tag, "load", "p1" + tag, null, Hash, null, "load", null, null, null, null);
        await AuditLog.FinishRunAsync(gate, scope, "r1", run, "ok", 5, Hash, 12);
        await AuditLog.IntervalAsync(gate, scope, "i1", "marts.v1" + tag, run, "2026-01-01", "2026-01-02", Hash, "load");
        await AuditLog.AcknowledgeAsync(gate, scope, "a1", "marts.v1" + tag, "DDB-300", "detail", "me", "reason");
        await AuditLog.StorePlanAsync(gate, scope, "sp1", "p2" + tag, Hash, "stored plan", "me");
        await AuditLog.MigrationAsync(gate, scope, "m2", "p1" + tag, Hash, "plan text", "abc123", "me", "completed", null, Hash, "refresh", Hash);
        foreach (var (kind, subject, json) in new[] { ("project", "project", ProjectDocument), ("model", "src.t", MappedDocument), ("model", "marts.v1" + tag, ModelDocument) })
        {
            await MetadataStore.StoreAsync(gate, scope, "md-" + subject, kind, subject, json, Hash, "1", "p1", null);
            await MetadataStore.ReplaceInfoAsync(gate, scope, "info-" + subject, kind, subject, MetadataInfoRows.From(kind, json));
        }

        var dependencies = MetadataDependencies.FromDocuments([("model", "marts.v1" + tag, ModelDocument)]);
        await MetadataStore.ReplaceDependenciesAsync(gate, scope, "dep", [.. dependencies.Select(d => d.Subject).Distinct()], dependencies);
    }

    private static BuildTrackingCopier CopierFor(ReadSession read, Engine engine, TrackingScope scope, StateDatabase state, TimeSpan? lag = null)
    {
        var login = Login(engine, DbDataBuild.Execution.Login.Read);
        var identity = BuildTrackingIdentity.For(login, scope);
        return new BuildTrackingCopier(identity, new BuildTrackingSource(read, scope, identity, lag ?? TimeSpan.Zero), new BuildTrackingStore(state), (kind, json) => MetadataInfoRows.HasRows(kind) ? MetadataInfoRows.From(kind, json) : null);
    }

    private static string KeysHash(IEnumerable<string> keys) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', keys)))).ToLowerInvariant();

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task The_state_module_follows_the_conventions_and_builds_on_the_engine(string name)
    {
        await using var engine = EngineEnv.Require(name);
        await engine.StartAsync();
        Assert.Empty(StateConventions.Violations(BuildTrackingModule.Module));
        var state = OpenState(engine);
        Assert.Equal(0, new BuildTrackingStore(state).Count("id", "run_log"));
        // opened again, nothing is applied twice
        Assert.Equal(0, new BuildTrackingStore(OpenState(engine)).Count("id", "run_log"));
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task The_copy_of_a_project_with_all_twelve_tables_equals_the_target_for_every_stream(string name)
    {
        await using var engine = EngineEnv.Require(name);
        await engine.StartAsync();
        var scope = new TrackingScope(name, SchemaName, "sales");
        await using (var gate = await GateAsync(engine)) { await TrackingStore.InitAsync(gate, name, SchemaName); await SeedAsync(gate, scope); }
        var state = OpenState(engine);
        await using var read = await ReadSession.OpenAsync(Login(engine, DbDataBuild.Execution.Login.Read));
        var copier = CopierFor(read, engine, scope, state);

        var result = await copier.RunAsync(TimeSpan.FromSeconds(60));

        Assert.True(result.Ok, string.Join("; ", result.Problems));
        Assert.True(result.CaughtUp);
        Assert.True(result.SnapshotsRebuilt);
        var store = new BuildTrackingStore(state);
        var q = engine.QuoteIdent;
        foreach (var table in BuildStreams.Tables)
        {
            var onTarget = int.Parse((await engine.RowsAsync($"SELECT CAST(COUNT(*) AS VARCHAR(20)) FROM {q(SchemaName)}.{q(table)} {(table == "tracking_version" ? "" : $"WHERE {q("connection")} = 'sales'")}")).Single());     // the layout record is the store's, not a connection's
            Assert.True(onTarget > 0, $"the seed wrote no row of {table}");
            Assert.Equal(onTarget, store.Count(copier.Identity, table));
        }

        // the keys of every stream, in order, are the same on both sides (a hash of them)
        var source = new BuildTrackingSource(read, scope, copier.Identity, TimeSpan.Zero);
        foreach (var stream in BuildStreams.All)
        {
            var target = await source.ReadAfterAsync(copier.Identity, stream.Stream, null, 100000, default);
            Assert.Equal(KeysHash(target.Select(r => r.Key)), KeysHash(store.Rows(copier.Identity, stream.Name).Select(r => r.Key)));
        }

        // the plan members come in the order of their positions: 1, 2, 10
        Assert.Equal(["p1|0000000001", "p1|0000000002", "p1|0000000010"], store.Rows(copier.Identity, "refresh_plan_member").Select(r => r.Key));

        // the snapshots hold what the target's rows say
        Assert.Equal(await engine.RowsAsync($"SELECT {q("subject_kind")} + '|' + {q("subject")} + '|' + {q("element")} + '|' + {q("item")} + '|' + {q("item_key")} FROM {q(SchemaName)}.{q("object_info")} WHERE {q("connection")} = 'sales' ORDER BY 1".Replace(" + ", name == "postgres" ? " || " : " + ")),
            store.Rows(copier.Identity, "object_info").Select(r => r.Key).Order(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);

        // a second run copies nothing and changes nothing
        var again = await copier.RunAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(0, again.Copied);
        Assert.False(again.SnapshotsRebuilt);
    }

    /// <summary>The state the runner's protocol host keeps, reached in process: the same statements as the runner's calls (plan 28c E16), so the remote copier can be run against real tracking tables without a runner.</summary>
    private sealed class InProcessRunnerCopy(StateDatabase state) : IRemoteTrackingCopy
    {
        private readonly BuildTrackingStore _store = new(state);
        private readonly ProgressStore _progress = new(state, BuildTrackingState.ProgressKey);
        private readonly StateTrackingDivergences _divergences = new(state);

        public Task<TrackingCopyState> StateAsync(string identity, string stream, CancellationToken ct)
        {
            var c = _progress.GetCheckpoint(TrackingFollower.CheckpointNameOf(stream), identity);
            TrackingPosition? position = c is not null ? new TrackingPosition(c.Parts[TrackingFollower.OrderPartName].AsInt, c.Parts[TrackingFollower.KeyPartName].AsText) : null;
            return Task.FromResult(new TrackingCopyState(position, c?.Version ?? 0, _divergences.Open(identity, stream)));
        }

        public Task<bool> WriteAdvanceAsync(string identity, TrackingStream stream, IReadOnlyList<TrackingRow> rows, long expectedVersion, CancellationToken ct)
        {
            var last = rows[^1];
            var parts = new Dictionary<string, ProgressValue> { [TrackingFollower.OrderPartName] = ProgressValue.OfInt(last.Order), [TrackingFollower.KeyPartName] = ProgressValue.OfText(last.Key) };
            return Task.FromResult(_progress.Advance(TrackingFollower.CheckpointNameOf(stream.Name), parts, expectedVersion, itemKey: identity, alsoWrite: (c, t) => _store.Write(c, t, identity, stream, rows)));
        }

        public Task<IReadOnlyList<TrackingPosition>> TailAsync(string identity, TrackingStream stream, int count, CancellationToken ct) => _store.TailAsync(identity, stream, count, ct);

        public Task<TrackingDivergence> MarkDivergenceAsync(string identity, string stream, string reason, TrackingPosition? common, TrackingPosition? primaryHigh, TrackingPosition? copyHigh, CancellationToken ct) =>
            Task.FromResult(_divergences.Mark(identity, stream, reason, common, primaryHigh, copyHigh));

        public Task RewriteAsync(string identity, string stream, IReadOnlyList<TrackingRow> rows, CancellationToken ct) => _store.WriteAsync(identity, BuildStreams.Get(stream).Stream, rows, ct);

        public Task<IReadOnlyList<TrackingRow>> ReadAsync(string identity, string stream, TrackingPosition? after, int limit, CancellationToken ct) => _store.ReadAfterAsync(identity, BuildStreams.Get(stream).Stream, after, limit, ct);

        public Task<IReadOnlyList<(string Key, string Hash)>> UnsettledAsync(string identity, string stream, int limit, CancellationToken ct) => Task.FromResult(_store.Unsettled(identity, stream, limit));

        public Task<int> ReplaceSnapshotAsync(string identity, string table, IReadOnlyList<TrackingRow> rows, CancellationToken ct)
        {
            _store.ReplaceSnapshot(identity, table, [.. rows.Select(r => (r.Key, r.Order, r.Document))]);
            return Task.FromResult(rows.Count);
        }
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task The_remote_copier_of_the_runners_task_makes_the_same_copy_as_the_local_one(string name)
    {
        await using var engine = EngineEnv.Require(name);
        await engine.StartAsync();
        var scope = new TrackingScope(name, SchemaName, "sales");
        await using (var gate = await GateAsync(engine)) { await TrackingStore.InitAsync(gate, name, SchemaName); await SeedAsync(gate, scope); }
        var localState = OpenState(engine);
        await using var read = await ReadSession.OpenAsync(Login(engine, DbDataBuild.Execution.Login.Read));
        var local = CopierFor(read, engine, scope, localState);
        Assert.True((await local.RunAsync(TimeSpan.FromSeconds(60))).Ok);

        // the same target copied the runner's way: into a state that is only reached through IRemoteTrackingCopy
        var runnerState = StateOpener.Open(new StateSettings(name == "sqlserver" ? StateEngineIds.MsSql : StateEngineIds.Postgres, engine.ConnectionString), "unused.db", BuildTrackingState.Modules, new StateScope("runner-main", "dbdatarunner"));
        var identity = local.Identity;
        var remote = new RemoteBuildTrackingCopier(identity, new BuildTrackingSource(read, scope, identity, TimeSpan.Zero), new InProcessRunnerCopy(runnerState), (kind, json) => MetadataInfoRows.HasRows(kind) ? MetadataInfoRows.From(kind, json) : null);
        var result = await remote.RunAsync(TimeSpan.FromSeconds(60));

        Assert.True(result.Ok, string.Join("; ", result.Problems));
        Assert.True(result.CaughtUp);
        Assert.True(result.SnapshotsRebuilt);
        var a = new BuildTrackingStore(localState);
        var b = new BuildTrackingStore(runnerState);
        foreach (var table in BuildStreams.Tables)
        {
            Assert.True(a.Count(identity, table) > 0);
            Assert.Equal(a.Count(identity, table), b.Count(identity, table));
            Assert.Equal(KeysHash(a.Rows(identity, table).Select(r => r.Key)), KeysHash(b.Rows(identity, table).Select(r => r.Key)));
        }

        Assert.Equal(0, (await remote.RunAsync(TimeSpan.FromSeconds(60))).Copied);
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task An_updated_row_reaches_the_copy_with_its_final_status(string name)
    {
        await using var engine = EngineEnv.Require(name);
        await engine.StartAsync();
        var scope = new TrackingScope(name, SchemaName, "sales");
        await using var gate = await GateAsync(engine);
        await TrackingStore.InitAsync(gate, name, SchemaName);
        var run = Guid.NewGuid();
        var ddl = Guid.NewGuid();
        await AuditLog.BeginRunAsync(gate, scope, "r1", run, "marts.v1", "load", "p1", null, Hash, null, "load", null, null, null, null);
        await AuditLog.BeginDdlAsync(gate, scope, "d1", ddl, "marts.v1", "CREATE", Hash, null, "me", "p1", null);
        var state = OpenState(engine);
        await using var read = await ReadSession.OpenAsync(Login(engine, DbDataBuild.Execution.Login.Read));
        var copier = CopierFor(read, engine, scope, state);
        var store = new BuildTrackingStore(state);

        await copier.RunAsync(TimeSpan.FromSeconds(60));
        Assert.Contains("\"status\":\"started\"", store.Rows(copier.Identity, "run_log").Single().Document);
        Assert.Equal(2, store.Unsettled(copier.Identity, "run_log", 10).Count + store.Unsettled(copier.Identity, "ddl_log", 10).Count);

        await AuditLog.FinishRunAsync(gate, scope, "r1", run, "ok", 7, Hash, 33);
        await AuditLog.FinishDdlAsync(gate, scope, "d1", ddl, "failed", null);
        var second = await copier.RunAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(2, second.Reconciled);
        var copied = store.Rows(copier.Identity, "run_log").Single().Document;
        Assert.Contains("\"status\":\"ok\"", copied);
        Assert.Contains("\"rows_affected\":7", copied);
        Assert.Contains("\"duration_ms\":33", copied);
        Assert.Contains("\"status\":\"failed\"", store.Rows(copier.Identity, "ddl_log").Single().Document);
        Assert.Empty(store.Unsettled(copier.Identity, "run_log", 10));
        Assert.Empty(store.Unsettled(copier.Identity, "ddl_log", 10));
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task A_restored_target_is_marked_diverged_and_a_development_copy_keeps_its_own_identity(string name)
    {
        await using var engine = EngineEnv.Require(name);
        await engine.StartAsync();
        var production = new TrackingScope(name, SchemaName, "sales");
        var development = new TrackingScope(name, "dbdatabuild_dev", "sales");      // the same connection name in another tracking store: another identity
        await using var gate = await GateAsync(engine);
        await TrackingStore.InitAsync(gate, name, SchemaName);
        await TrackingStore.InitAsync(gate, name, "dbdatabuild_dev");
        for (var i = 0; i < 5; i++) await TrackingStore.RecordSchemaVersionAsync(gate, production, $"s{i}", $"marts.v{i}", Hash, null, "tool", "p1", null);
        await TrackingStore.RecordSchemaVersionAsync(gate, development, "dev", "marts.dev", Hash, null, "tool", "p9", null);
        var state = OpenState(engine);
        await using var read = await ReadSession.OpenAsync(Login(engine, DbDataBuild.Execution.Login.Read));
        var prod = CopierFor(read, engine, production, state);
        var dev = CopierFor(read, engine, development, state);
        var store = new BuildTrackingStore(state);
        Assert.NotEqual(prod.Identity, dev.Identity);
        Assert.True((await prod.RunAsync(TimeSpan.FromSeconds(60))).Ok);
        Assert.True((await dev.RunAsync(TimeSpan.FromSeconds(60))).Ok);
        Assert.Equal(5, store.Count(prod.Identity, "schema_version"));
        Assert.Equal(1, store.Count(dev.Identity, "schema_version"));

        // the target is restored from a backup that lacks the last two rows
        var q = engine.QuoteIdent;
        await engine.ExecAsync($"DELETE FROM {q(SchemaName)}.{q("schema_version")} WHERE {q("object_name")} IN ('marts.v3', 'marts.v4')");
        var diverged = await prod.RunAsync(TimeSpan.FromSeconds(60));

        Assert.False(diverged.Ok);
        Assert.Contains(diverged.Streams, s => s.Stream == "schema_version" && s.Result.Diverged);
        Assert.Equal(5, store.Count(prod.Identity, "schema_version"));          // nothing was deleted from the copy
        Assert.Equal(1, store.Count(dev.Identity, "schema_version"));            // the development copy was not touched
        Assert.True((await dev.RunAsync(TimeSpan.FromSeconds(60))).Ok);

        // an administrator resolves it by archiving: the copy's later rows are put aside, and copying goes on from the common point
        Assert.True(await prod.Follower("schema_version").ResolveAsync(TrackingResolution.Archive, "admin"));
        await TrackingStore.RecordSchemaVersionAsync(gate, production, "after", "marts.after", Hash, null, "tool", "p1", null);
        Assert.True((await prod.RunAsync(TimeSpan.FromSeconds(60))).Ok);
        Assert.Equal(4, store.Count(prod.Identity, "schema_version"));          // three live rows that both sides hold, and the new one
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task A_copy_that_is_given_no_time_does_nothing_and_a_row_newer_than_the_settle_time_waits(string name)
    {
        await using var engine = EngineEnv.Require(name);
        await engine.StartAsync();
        var scope = new TrackingScope(name, SchemaName, "sales");
        await using var gate = await GateAsync(engine);
        await TrackingStore.InitAsync(gate, name, SchemaName);
        await TrackingStore.RecordSchemaVersionAsync(gate, scope, "s1", "marts.v1", Hash, null, "tool", "p1", null);
        var state = OpenState(engine);
        await using var read = await ReadSession.OpenAsync(Login(engine, DbDataBuild.Execution.Login.Read));

        var none = await CopierFor(read, engine, scope, state).RunAsync(TimeSpan.Zero);
        Assert.Empty(none.Streams);
        Assert.Equal(0, none.Copied);

        var waiting = await CopierFor(read, engine, scope, state, TimeSpan.FromHours(1)).RunAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(0, waiting.Copied);
        Assert.True(waiting.Ok);
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task A_store_of_layout_nine_is_refused_until_the_upgrade_gives_its_intervals_the_time_of_their_run(string name)
    {
        await using var engine = EngineEnv.Require(name);
        await engine.StartAsync();
        var scope = new TrackingScope(name, SchemaName, "sales");
        var q = engine.QuoteIdent;
        await using (var gate = await GateAsync(engine))
        {
            await TrackingStore.InitAsync(gate, name, SchemaName);
            var run = Guid.NewGuid();
            await AuditLog.BeginRunAsync(gate, scope, "r1", run, "marts.v1", "load", "p1", null, Hash, null, "load", null, null, null, null);
            await AuditLog.IntervalAsync(gate, scope, "i1", "marts.v1", run, "a", "b", Hash, "load");
        }

        // the layout as it was: no `recorded_utc`, recorded as nine
        var startedBefore = (await engine.RowsAsync($"SELECT CAST({q("started_utc")} AS VARCHAR(40)) FROM {q(SchemaName)}.{q("run_log")}")).Single();
        await engine.ExecAsync($"ALTER TABLE {q(SchemaName)}.{q("operation_interval")} DROP COLUMN {q("recorded_utc")}");
        await engine.ExecAsync($"DELETE FROM {q(SchemaName)}.{q("tracking_version")} WHERE {q("version")} = 10");
        await engine.ExecAsync($"INSERT INTO {q(SchemaName)}.{q("tracking_version")} ({q("version")}, {q("tool_version")}, {q("applied_utc")}) VALUES (9, 'old', {(name == "postgres" ? "now()" : "SYSUTCDATETIME()")})");
        await using (var read = await ReadSession.OpenAsync(Login(engine, DbDataBuild.Execution.Login.Read)))
        {
            var status = await TrackingStore.StatusAsync(read, name, SchemaName);
            Assert.Equal(new TrackingStatus(TrackingState.UnknownLayout, 9), status);
            Assert.Equal("DDB-505", status.AsDiagnostic(SchemaName)!.Code);
            Assert.Contains("recorded_utc", status.AsDiagnostic(SchemaName)!.Found);
        }

        await using (var gate = await GateAsync(engine))
        {
            await TrackingStore.UpgradeAsync(gate, name, SchemaName, "sales");
            await TrackingStore.UpgradeAsync(gate, name, SchemaName, "sales");        // safe to repeat
        }

        Assert.Equal([TrackingSchema.Version.ToString()], await engine.RowsAsync($"SELECT CAST(MAX({q("version")}) AS VARCHAR(10)) FROM {q(SchemaName)}.{q("tracking_version")}"));
        Assert.Equal(1, int.Parse((await engine.RowsAsync($"SELECT CAST(COUNT(*) AS VARCHAR(10)) FROM {q(SchemaName)}.{q("operation_interval")} i JOIN {q(SchemaName)}.{q("run_log")} r ON r.{q("run_id")} = i.{q("run_id")} WHERE i.{q("recorded_utc")} = r.{q("started_utc")}")).Single()));
    }
}
