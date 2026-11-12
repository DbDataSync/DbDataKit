using DbDataBuild.Cli;
using DbDataBuild.Cli.Server;
using DbDataBuild.Execution;
using DbDataKit.Tracking.Build;
using DbDataBuild.State;
using DbDataKit.State;
using DbDataKit.State.Tracking;
using Xunit;

namespace DbDataBuild.Tests.Unit;

/// <summary>
/// Plan 28c E16: the formats of Build's copy of its tracking tables, fixed now (the module, the streams, the document, the retention, the setting). The module runs here on SQLite; the same checks on SQL Server and PostgreSQL are in the
/// conformance project (<c>BuildTrackingCopyConformanceTests</c>), which also copies real tracking rows.
/// </summary>
public sealed class BuildTrackingTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ddb-tracking-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* a file still open on Windows: the temp folder is cleaned by the machine */ }
    }

    private StateDatabase Open(TimeProvider? time = null) => StateDatabase.OpenModules(Path.Combine(_dir, "state.db"), BuildTrackingState.Modules, BuildTrackingState.Scope, time);

    [Fact]
    public async Task Build_runs_are_read_from_the_copy_into_the_runs_module_once_and_finished_when_their_steps_end()
    {
        // plan 29a Q21: the runs module reads Build's runs from the copy, so one query lists Build's runs beside the other services'
        var database = StateDatabase.OpenModules(Path.Combine(_dir, "runs.db"), [.. BuildTrackingState.Modules, RunsModule.Module], BuildTrackingState.Scope);
        var store = new BuildTrackingStore(database);
        var stream = BuildStreams.Get("run_log").Stream;
        const string identity = "buildtracking:mssql://sql01/wh.dbdatabuild.tracking";
        var run = Guid.NewGuid();
        string Step(string id, string status, long? rows, string? ended) =>
            $"{{\"run_id\":\"{run:D}\",\"step_id\":\"{id}\",\"model\":\"m_{id}\",\"status\":\"{status}\",\"rows_affected\":{(rows is null ? "null" : rows)},\"started_utc\":\"2026-11-11T10:00:0{id[^1]}.0000000Z\",\"ended_utc\":{(ended is null ? "null" : $"\"{ended}\"")}}}";
        string Key(string id) => $"{run:D}|{id}";
        var runs = new RunStore(database, "build");

        await store.WriteAsync(identity, stream, [new TrackingRow(Key("s1"), 10, Step("s1", "ok", 5, "2026-11-11T10:00:01.0000000Z")), new TrackingRow(Key("s2"), 11, Step("s2", "started", null, null))], default);
        var running = Assert.Single(runs.Read(new RunQuery(Service: "build")));
        Assert.Equal(DbDataKit.Monitoring.RunOutcome.Running, running.Run.Outcome);
        Assert.Equal(identity, running.Run.JobId);

        await store.WriteAsync(identity, stream, [new TrackingRow(Key("s2"), 11, Step("s2", "ok", 7, "2026-11-11T10:00:09.0000000Z"))], default);
        var done = Assert.Single(runs.Read(new RunQuery(Service: "build")));
        Assert.Equal(DbDataKit.Monitoring.RunOutcome.Succeeded, done.Run.Outcome);
        Assert.Equal(12, done.Run.Rows);
        Assert.Equal(DateTimeOffset.Parse("2026-11-11T10:00:09Z"), done.Run.Finished);

        // writing the same rows again changes nothing: a run ends once
        await store.WriteAsync(identity, stream, [new TrackingRow(Key("s2"), 11, Step("s2", "failed", 7, "2026-11-11T10:00:12.0000000Z"))], default);
        Assert.Equal(DbDataKit.Monitoring.RunOutcome.Succeeded, Assert.Single(runs.Read(new RunQuery(Service: "build"))).Run.Outcome);

        // a failed step is a failed run that names the item, never a statement
        var bad = Guid.NewGuid();
        await store.WriteAsync(identity, stream, [new TrackingRow($"{bad:D}|s1", 20, Step("s1", "failed", null, "2026-11-11T11:00:01.0000000Z").Replace(run.ToString("D"), bad.ToString("D")))], default);
        var failed = runs.Get(bad)!;
        Assert.Equal(DbDataKit.Monitoring.RunOutcome.Failed, failed.Run.Outcome);
        Assert.Equal("m_s1", failed.Run.FailedItem);
    }

    [Fact]
    public void The_module_follows_the_conventions_and_builds_on_sqlite()
    {
        Assert.Empty(StateConventions.Violations(BuildTrackingModule.Module));
        Assert.Equal(StateScopeKind.Group, BuildTrackingModule.Module.Scope);
        var database = Open();
        var store = new BuildTrackingStore(database);
        Assert.Equal(0, store.Count("id", "run_log"));
    }

    [Fact]
    public void The_streams_are_pinned_so_a_change_is_a_deliberate_one()
    {
        Assert.Equal(
        [
            "tracking_version|applied_utc|version|Append|keep",
            "schema_version|first_seen_utc|object_name|first_seen_utc|Append|keep",
            "migration_log|applied_utc|plan_id|applied_utc|Append|keep",
            "block_log|created_utc|block_id|Append|keep",
            "metadata_document|recorded_utc|subject_kind|subject|recorded_utc|Append|keep",
            "plan_store|stored_utc|plan_id|Append|run",
            "refresh_plan_member|applied_utc|plan_id|position|Append|run",
            "operation_interval|recorded_utc|interval_id|Append|run",
            "ddl_log|executed_utc|ddl_id|Updated|run",
            "run_log|started_utc|run_id|step_id|Updated|run",
        ], BuildStreams.All.Select(s => $"{s.Name}|{s.OrderColumn}|{s.RowKeyName}|{s.Kind}|{(s.RunLike ? "run" : "keep")}"));
        Assert.Equal(["object_info", "metadata_dependency"], BuildStreams.Snapshots);
        // every one of the tracking tables is kept by the copy, as a stream or a snapshot, and no other
        Assert.Equal(TrackingSchema.Tables.Select(t => t.Name).Order(), BuildStreams.Tables.Order());
        foreach (var stream in BuildStreams.All) Assert.NotNull(stream.Stream);      // the follower accepts each one's ordering and row key
    }

    [Fact]
    public void A_document_has_every_column_but_the_connection_in_the_tables_order_with_times_to_the_tick()
    {
        var stream = BuildStreams.Get("ddl_log");
        var values = new object?[] { Guid.Parse("11111111-2222-3333-4444-555555555555"), "dbo.t", "ab".PadRight(64, 'c'), "CREATE", null, null, "me", "p1", null, new DateTime(2026, 11, 7, 10, 11, 12, 123, DateTimeKind.Unspecified).AddTicks(4), "started" };
        Assert.Equal(
            "{\"ddl_id\":\"11111111-2222-3333-4444-555555555555\",\"object_name\":\"dbo.t\",\"statement_hash\":\"" + "ab".PadRight(64, 'c') + "\",\"statement_text\":\"CREATE\",\"hash_before\":null,\"hash_after\":null,\"invoker\":\"me\",\"plan_id\":\"p1\",\"git_commit\":null,\"executed_utc\":\"2026-11-07T10:11:12.1230004Z\",\"status\":\"started\"}",
            BuildStreams.Document(stream, values));
        Assert.True(BuildStreams.IsUnsettled(stream, BuildStreams.Document(stream, values)));
        Assert.False(BuildStreams.IsUnsettled(BuildStreams.Get("schema_version"), "{\"status\":\"started\"}"));
        Assert.Equal(BuildStreams.RowHash("x"), BuildStreams.RowHash("x"));
    }

    [Fact]
    public void Keys_sort_the_way_the_rows_do()
    {
        var run = BuildStreams.Get("refresh_plan_member");
        Assert.True(string.CompareOrdinal(BuildStreams.KeyPart(run, "position", 2), BuildStreams.KeyPart(run, "position", 10)) < 0);
        var schema = BuildStreams.Get("schema_version");
        var early = BuildStreams.KeyPart(schema, "first_seen_utc", new DateTime(2026, 1, 1));
        var late = BuildStreams.KeyPart(schema, "first_seen_utc", new DateTime(2026, 1, 1).AddMilliseconds(1));
        Assert.True(string.CompareOrdinal(early, late) < 0);
        Assert.Equal(19, early.Length);
    }

    [Fact]
    public async Task Run_like_history_goes_after_a_year_and_the_audit_streams_never()
    {
        var rule = Assert.Single(BuildTrackingModule.Module.Retention);
        Assert.Equal("tracked_row", rule.Table);
        Assert.Equal(365, rule.DefaultDays);
        foreach (var kept in new[] { "schema_version", "migration_log", "block_log", "metadata_document", "object_info", "metadata_dependency", "tracking_version" })
            Assert.DoesNotContain($"'{kept}'", rule.Where);
        foreach (var aged in new[] { "ddl_log", "run_log", "operation_interval", "refresh_plan_member", "plan_store" })
            Assert.Contains($"'{aged}'", rule.Where);

        var database = Open();
        var store = new BuildTrackingStore(database);
        var old = DateTime.UtcNow.AddDays(-400).Ticks;
        var recent = DateTime.UtcNow.AddDays(-10).Ticks;
        foreach (var name in new[] { "ddl_log", "run_log", "schema_version", "migration_log", "block_log", "metadata_document" })
        {
            var stream = BuildStreams.Get(name).Stream;
            await store.WriteAsync("id", stream, [new TrackingRow("old", old, "{\"status\":\"ok\"}"), new TrackingRow("recent", recent, "{\"status\":\"ok\"}")], default);
        }

        new RetentionJob(database).Run();
        Assert.Equal(1, store.Count("id", "ddl_log"));
        Assert.Equal(1, store.Count("id", "run_log"));
        foreach (var name in new[] { "schema_version", "migration_log", "block_log", "metadata_document" }) Assert.Equal(2, store.Count("id", name));
    }

    [Fact]
    public async Task A_follower_copies_into_the_module_in_one_transaction_with_its_checkpoint_and_marks_unsettled_rows()
    {
        var database = Open();
        var store = new BuildTrackingStore(database);
        var stream = BuildStreams.Get("run_log");
        var primary = new InMemoryTrackingStore();
        const string identity = "buildtracking:mssql://sql01/wh.dbdatabuild.tracking@sales";
        string Doc(string status) => $"{{\"status\":\"{status}\"}}";
        primary.Add(identity, stream.Stream, new TrackingRow("a|s1", 10, Doc("ok")), new TrackingRow("a|s2", 11, Doc("started")));
        var follower = new TrackingFollower(identity, stream.Stream, primary, store, new ProgressStore(database, BuildTrackingState.ProgressKey), new StateTrackingDivergences(database));

        var result = await follower.CatchUpAsync(TimeSpan.FromSeconds(10));

        Assert.True(result.CaughtUp);
        Assert.Equal(2, result.Copied);
        Assert.Equal(new TrackingPosition(11, "a|s2"), follower.Position);
        var unsettled = Assert.Single(store.Unsettled(identity, "run_log", 10));
        Assert.Equal("a|s2", unsettled.Key);
        Assert.Equal(BuildStreams.RowHash(Doc("started")), unsettled.Hash);
        Assert.Equal(0, store.Count("another-identity", "run_log"));
    }

    // ---------- the runner's copy: the project's off switch ----------

    private string Project(string yaml)
    {
        var dir = Path.Combine(_dir, "project" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), yaml);
        return dir;
    }

    [Fact]
    public async Task The_runner_copy_is_on_unless_the_project_says_runner_copy_false_and_then_the_follower_step_says_so_and_does_nothing()
    {
        const string head = "defaults: {connections: [sqlserver]}\n";
        Assert.False(BuildTrackedConnections.RunnerCopyOff(Project(head)));
        Assert.False(BuildTrackedConnections.RunnerCopyOff(Project(head + "tracking: {connection: sqlserver}\n")));
        Assert.False(BuildTrackedConnections.RunnerCopyOff(Project(head + "tracking: {connection: sqlserver, runner_copy: true}\n")));
        var off = Project(head + "tracking: {connection: sqlserver, runner_copy: false}\n");
        Assert.True(BuildTrackedConnections.RunnerCopyOff(off));

        // the copy is never touched (a null copy would throw), and the log line names the switch
        var lines = await DbDataBuild.Cli.Runner.RunnerTrackingCopy.CopyAsync(null!, off, null, _ => "x", TimeSpan.FromSeconds(5), default);
        var line = Assert.Single(lines);
        Assert.Contains("runner_copy: false", line);
    }

    [Fact]
    public void A_runner_copy_that_is_not_true_or_false_is_a_diagnostic_naming_the_key()
    {
        var diagnostics = new List<DbDataBuild.Core.Diagnostic>();
        DbDataBuild.Models.ProjectConfigLoader.LoadFromProject(Project("defaults: {connections: [sqlserver]}\ntracking: {connection: sqlserver, runner_copy: maybe}\n"), diagnostics);
        Assert.Contains(diagnostics, d => d.Found.Contains("runner_copy", StringComparison.Ordinal));
    }

    // ---------- the setting ----------

    private string ServerDirectory(string? yaml)
    {
        var dir = Path.Combine(_dir, "server" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        if (yaml is not null) File.WriteAllText(Path.Combine(dir, "server.yaml"), yaml);
        return dir;
    }

    [Fact]
    public void The_setting_defaults_to_both_with_the_target_primary_and_reads_the_section()
    {
        Assert.Equal(TrackingSettings.Default, BuildTrackingSettings.Read(ServerDirectory(null)));
        Assert.Equal(TrackingSettings.Default, BuildTrackingSettings.Read(ServerDirectory("instance_id: x\n")));
        Assert.Equal(TrackingSettings.Default, BuildTrackingSettings.Read(ServerDirectory("tracking:\n  mode: both\n")));
        Assert.Equal(TrackingSettings.TargetOnly, BuildTrackingSettings.Read(ServerDirectory("tracking:\n  mode: target\n")));
    }

    [Theory]
    [InlineData("tracking:\n  mod: both\n", "tracking.mod is not a tracking setting", "did you mean 'mode'")]
    [InlineData("tracking:\n  mode: sometimes\n", "'sometimes' is not one of target, state, both", "tracking")]
    [InlineData("tracking:\n  mode: target\n  primary: target\n", "takes no primary", "tracking")]
    public void A_setting_that_is_not_valid_stops_the_start_naming_the_file(string yaml, string says, string also)
    {
        var dir = ServerDirectory(yaml);
        var ex = Assert.Throws<InvalidOperationException>(() => BuildTrackingSettings.Read(dir));
        Assert.Contains(Path.Combine(dir, "server.yaml"), ex.Message);
        Assert.Contains(says, ex.Message);
        Assert.Contains(also, ex.Message);
    }

    // ---------- layout 10 ----------

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("postgres")]
    public void Layout_ten_gives_operation_interval_its_time_and_the_upgrade_fills_it(string target)
    {
        Assert.Contains(TrackingSchema.Table("operation_interval").Columns, c => c.Name == "recorded_utc" && c.Nullable && c.Type == TrackingType.TimestampUtc);
        var init = TrackingDdl.Render(TrackingDdl.For(target).InitScript("dbdatabuild", "x"));
        Assert.Contains("recorded_utc", init);
        var upgrade = TrackingDdl.Render(TrackingDdl.For(target).UpgradeScript("dbdatabuild", "c"));
        Assert.Contains("(layout 10)", upgrade);
        Assert.Contains("recorded_utc", upgrade);
        Assert.Contains("started_utc", upgrade);      // a row of layout 9 gets the start of its run
    }

    // ---------- state first (plan 28c E16, mode state with a state primary) ----------

    [Fact]
    public void A_state_primary_is_a_setting_the_server_reads_and_writes_back_keeping_the_rest_of_the_file()
    {
        Assert.Equal(TrackingSide.State, BuildTrackingSettings.Read(ServerDirectory("tracking:\n  mode: state\n")).PrimarySide);
        Assert.Equal(TrackingSide.State, BuildTrackingSettings.Read(ServerDirectory("tracking:\n  mode: both\n  primary: state\n")).PrimarySide);

        var none = ServerDirectory("instance_id: x\n");
        Assert.False(BuildTrackingSettings.IsExplicit(none));
        Assert.Null(BuildTrackingSettings.PrimaryOverride(none));       // no section: the projects' keys decide

        var dir = ServerDirectory("# the server\ninstance_id: x\ntracking:\n  mode: both\n  primary: target\n");
        Assert.Equal("target", BuildTrackingSettings.PrimaryOverride(dir));
        BuildTrackingSettings.Write(dir, TrackingSettings.Both(TrackingSide.State));
        Assert.Equal("state", BuildTrackingSettings.PrimaryOverride(dir));
        var text = File.ReadAllText(Path.Combine(dir, "server.yaml"));
        Assert.Contains("# the server", text);
        Assert.Contains("instance_id: x", text);
        BuildTrackingSettings.Write(dir, TrackingSettings.Both(TrackingSide.Target));
        Assert.Equal(TrackingSettings.Default, BuildTrackingSettings.Read(dir));

        // a server with no section gets one when it is switched
        var fresh = ServerDirectory("instance_id: y\n");
        BuildTrackingSettings.Write(fresh, TrackingSettings.Both(TrackingSide.State));
        Assert.Equal("state", BuildTrackingSettings.PrimaryOverride(fresh));
        Assert.Throws<ArgumentException>(() => BuildTrackingSettings.Write(fresh, TrackingSettings.TargetOnly));
    }

    [Theory]
    [InlineData("tracking: {connection: sqlserver}\n", false)]
    [InlineData("tracking: {connection: sqlserver, mode: target}\n", false)]
    [InlineData("tracking: {connection: sqlserver, mode: both}\n", false)]
    [InlineData("tracking: {connection: sqlserver, mode: both, primary: target}\n", false)]
    [InlineData("tracking: {connection: sqlserver, mode: both, primary: state}\n", true)]
    [InlineData("tracking: {connection: sqlserver, mode: state}\n", true)]
    [InlineData("tracking: {connection: sqlserver, mode: state, primary: state}\n", true)]
    public void The_project_says_its_tracking_is_state_first_with_mode_and_primary(string tracking, bool stateFirst)
    {
        var diagnostics = new List<DbDataBuild.Core.Diagnostic>();
        var config = DbDataBuild.Models.ProjectConfigLoader.LoadFromProject(Project("defaults: {connections: [sqlserver]}\n" + tracking), diagnostics);
        Assert.DoesNotContain(diagnostics, d => d.Severity == DbDataBuild.Core.Severity.Error);
        Assert.Equal(stateFirst, config.Tracking.StatePrimary);
    }

    [Theory]
    [InlineData("tracking: {connection: sqlserver, mode: sometimes}\n", "tracking.mode")]
    [InlineData("tracking: {connection: sqlserver, primary: both}\n", "tracking.primary")]
    [InlineData("tracking: {connection: sqlserver, mode: target, primary: state}\n", "takes no `primary`")]
    [InlineData("tracking: {connection: sqlserver, mode: state, primary: target}\n", "its primary is `state`")]
    public void A_mode_or_primary_that_does_not_fit_is_a_diagnostic_naming_the_key(string tracking, string names)
    {
        var diagnostics = new List<DbDataBuild.Core.Diagnostic>();
        DbDataBuild.Models.ProjectConfigLoader.LoadFromProject(Project("defaults: {connections: [sqlserver]}\n" + tracking), diagnostics);
        Assert.Contains(diagnostics, d => d.Found.Contains(names, StringComparison.Ordinal));
    }

    private static LoginSettings TrackingLogin() => LoginSettings.FromEnvironment("sqlserver", DbDataBuild.Execution.Login.Read, _ => "Server=sql01;Database=wh;User Id=a;Password=b;TrustServerCertificate=true").Settings!;

    private DbDataBuild.Models.ProjectConfig ConfigOf(string tracking)
    {
        var diagnostics = new List<DbDataBuild.Core.Diagnostic>();
        return DbDataBuild.Models.ProjectConfigLoader.LoadFromProject(Project("defaults: {connections: [sqlserver]}\n" + tracking), diagnostics);
    }

    [Fact]
    public void A_state_first_project_with_no_state_to_write_to_is_refused_saying_what_to_set_and_a_target_first_one_is_not_touched()
    {
        var scope = new TrackingScope("sqlserver", "dbdatabuild", "sales");
        var targetFirst = ConfigOf("tracking: {connection: sqlserver}\n");
        Assert.Same(scope, TrackingJournals.For(targetFirst, scope, TrackingLogin(), _ => null));

        var stateFirst = ConfigOf("tracking: {connection: sqlserver, mode: state}\n");
        var ex = Assert.Throws<InvalidOperationException>(() => TrackingJournals.For(stateFirst, scope, TrackingLogin(), _ => null));
        Assert.Contains("state-first", ex.Message);
        Assert.Contains(TrackingJournals.ServerDirectoryVariable, ex.Message);

        // a host that has a state gives it to the flow, and the scope gets a journal for this tracking store's identity
        var sink = new StateBuildTrackingSink(new BuildTrackingStore(Open()));
        using (TrackingJournalSource.Use(sink))
        {
            var journaled = TrackingJournals.For(stateFirst, scope, TrackingLogin(), _ => null);
            Assert.NotNull(journaled.Journal);
            Assert.StartsWith(BuildTrackingIdentity.Prefix, journaled.Journal!.Identity, StringComparison.Ordinal);
            Assert.EndsWith("@sales", journaled.Journal.Identity, StringComparison.Ordinal);
        }

        Assert.Null(TrackingJournalSource.Sink);
    }

    [Fact]
    public void The_servers_primary_wins_over_the_projects_keys_for_the_commands_it_starts()
    {
        var targetFirst = ConfigOf("tracking: {connection: sqlserver}\n");
        var stateFirst = ConfigOf("tracking: {connection: sqlserver, mode: state}\n");
        string? Env(string v) => v == TrackingJournals.PrimaryVariable ? "state" : null;
        string? EnvTarget(string v) => v == TrackingJournals.PrimaryVariable ? "target" : null;
        Assert.True(TrackingJournals.IsStateFirst(targetFirst, Env));
        Assert.False(TrackingJournals.IsStateFirst(stateFirst, EnvTarget));
        Assert.True(TrackingJournals.IsStateFirst(stateFirst, _ => null));
        Assert.False(TrackingJournals.IsStateFirst(targetFirst, _ => null));
    }

    [Fact]
    public async Task A_state_first_project_has_no_follower_a_server_copies_only_for_the_comparison_of_a_switch()
    {
        var root = Project("defaults: {connections: [sqlserver]}\ntracking: {connection: sqlserver, mode: state}\n");
        string? Env(string v) => v == "DBDATAKIT_BUILD_SQLSERVER_READ" ? "Server=sql01;Database=wh;User Id=a;Password=b" : null;
        Assert.True(BuildTrackedConnections.StateFirst(root, Env));
        Assert.Empty(BuildTrackedConnections.Of(root, "sqlserver", Env));
        Assert.Single(BuildTrackedConnections.Of(root, "sqlserver", Env, includeStateFirst: true));

        // the runner's step says why it copies nothing
        var lines = await DbDataBuild.Cli.Runner.RunnerTrackingCopy.CopyAsync(null!, root, null, Env, TimeSpan.FromSeconds(5), default);
        Assert.Contains("state-first", Assert.Single(lines));
    }

    // ---------- the journal ----------

    private static readonly string H = new('a', 64);
    private const string Sales = "buildtracking:mssql://sql01/wh.dbdatabuild.tracking@sales";

    private static Dictionary<string, object?> Ddl(Guid id) => new()
    {
        ["ddl_id"] = id, ["object_name"] = "marts.v1", ["statement_hash"] = H, ["statement_text"] = "CREATE VIEW", ["hash_before"] = null, ["invoker"] = "me", ["plan_id"] = "p1",
        ["git_commit"] = null, ["executed_utc"] = new DateTime(2026, 11, 12, 10, 0, 0, DateTimeKind.Unspecified).AddTicks(1234560), ["status"] = "started",
    };

    [Fact]
    public async Task The_journal_writes_the_rows_the_follower_would_copy_and_an_update_changes_only_its_columns()
    {
        var store = new BuildTrackingStore(Open());
        var journal = new BuildTrackingJournal(Sales, new StateBuildTrackingSink(store));
        var id = Guid.NewGuid();

        await journal.InsertAsync("ddl_log", Ddl(id), default);
        var started = Assert.Single(store.Rows(Sales, "ddl_log"));
        Assert.Equal(id.ToString("D"), started.Key);
        Assert.Equal(new DateTime(2026, 11, 12, 10, 0, 0).AddTicks(1234560).Ticks, started.Order);
        Assert.Contains("\"status\":\"started\"", started.Document);
        Assert.Contains("\"executed_utc\":\"2026-11-12T10:00:00.1234560Z\"", started.Document);
        Assert.Equal(id.ToString("D"), Assert.Single(store.Unsettled(Sales, "ddl_log", 10)).Key);        // marked unsettled while the step has not ended

        await journal.UpdateAsync("ddl_log", new Dictionary<string, object?> { ["ddl_id"] = id }, new Dictionary<string, object?> { ["status"] = "ok", ["hash_after"] = H }, default);
        var done = Assert.Single(store.Rows(Sales, "ddl_log"));
        Assert.Equal(started.Order, done.Order);
        Assert.Contains("\"status\":\"ok\"", done.Document);
        Assert.Contains($"\"hash_after\":\"{H}\"", done.Document);
        Assert.Contains("\"statement_text\":\"CREATE VIEW\"", done.Document);       // the columns it did not change are as they were
        Assert.Empty(store.Unsettled(Sales, "ddl_log", 10));

        // a journal that did not write the row (the step was started by another process) finds it in the state and finishes it
        var other = new BuildTrackingJournal(Sales, new StateBuildTrackingSink(store));
        var second = Guid.NewGuid();
        await journal.InsertAsync("ddl_log", Ddl(second), default);
        await other.UpdateAsync("ddl_log", new Dictionary<string, object?> { ["ddl_id"] = second }, new Dictionary<string, object?> { ["status"] = "failed" }, default);
        Assert.Contains("\"status\":\"failed\"", store.Rows(Sales, "ddl_log").Single(r => r.Key == second.ToString("D")).Document);

        // a row the state never held is a message, not a silent gap
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => other.UpdateAsync("ddl_log", new Dictionary<string, object?> { ["ddl_id"] = Guid.NewGuid() }, new Dictionary<string, object?> { ["status"] = "ok" }, default));
        Assert.Contains("state holds no row", ex.Message);
    }

    [Fact]
    public async Task The_journal_keys_and_orders_every_stream_the_way_the_target_source_does()
    {
        var store = new BuildTrackingStore(Open());
        var journal = new BuildTrackingJournal(Sales, new StateBuildTrackingSink(store));
        var at = new DateTime(2026, 11, 12, 10, 0, 0, DateTimeKind.Unspecified);

        await journal.InsertAsync("migration_log", new Dictionary<string, object?> { ["plan_id"] = "p1", ["plan_hash"] = H, ["plan_text"] = "t", ["applied_by"] = "me", ["applied_utc"] = at, ["status"] = "started" }, default);
        foreach (var position in new[] { 10, 2, 1 })
            await journal.InsertAsync("refresh_plan_member", new Dictionary<string, object?> { ["plan_id"] = "p1", ["position"] = position, ["membership"] = "member", ["model"] = "m", ["load_name"] = "l", ["load_plan_hash"] = H, ["selected"] = "selected" }, default);
        var run = Guid.NewGuid();
        await journal.InsertAsync("run_log", new Dictionary<string, object?> { ["run_id"] = run, ["step_id"] = "r1", ["model"] = "m", ["operation"] = "load", ["status"] = "started", ["started_utc"] = at }, default);
        await journal.UpdateAsync("run_log", new Dictionary<string, object?> { ["run_id"] = run, ["step_id"] = "r1" }, new Dictionary<string, object?> { ["status"] = "ok", ["rows_affected"] = 5L, ["ended_utc"] = at.AddSeconds(3), ["duration_ms"] = 3000L }, default);

        Assert.Equal([$"p1|{at.Ticks:D19}"], store.Rows(Sales, "migration_log").Select(r => r.Key));
        // plan members are ordered by the plan's first migration_log time, and their keys sort by position
        Assert.Equal(["p1|0000000001", "p1|0000000002", "p1|0000000010"], store.Rows(Sales, "refresh_plan_member").Select(r => r.Key));
        Assert.All(store.Rows(Sales, "refresh_plan_member"), r => Assert.Equal(at.Ticks, r.Order));
        var runRow = Assert.Single(store.Rows(Sales, "run_log"));
        Assert.Equal($"{run:D}|r1", runRow.Key);
        Assert.Contains("\"rows_affected\":5", runRow.Document);
        Assert.Contains("\"ended_utc\":\"2026-11-12T10:00:03.0000000Z\"", runRow.Document);
    }

    [Fact]
    public async Task A_new_metadata_document_rebuilds_the_snapshots_in_the_state_and_the_layout_record_is_left_to_init()
    {
        var store = new BuildTrackingStore(Open());
        var journal = new BuildTrackingJournal(Sales, new StateBuildTrackingSink(store), (kind, json) => kind == "model" ? [new InfoRow("model", "", "", "string", "title")] : null);
        await journal.InsertAsync("metadata_document", new Dictionary<string, object?> { ["subject_kind"] = "model", ["subject"] = "marts.v1", ["document"] = "{}", ["document_hash"] = H, ["recorded_utc"] = new DateTime(2026, 11, 12, 10, 0, 0), ["tool_version"] = "1" }, default);
        Assert.Single(store.Rows(Sales, "metadata_document"));
        Assert.Single(store.Rows(Sales, "object_info"));

        // tracking_version is written by the init script, and a snapshot table is not a row a writer hands over: neither is journaled
        await journal.InsertAsync("tracking_version", new Dictionary<string, object?> { ["version"] = 10, ["tool_version"] = "x", ["applied_utc"] = DateTime.UtcNow }, default);
        Assert.Equal(0, store.Count(Sales, "tracking_version"));
        await journal.InsertAsync("object_info", new Dictionary<string, object?>(), default);
        Assert.Equal(1, store.Count(Sales, "object_info"));
    }
}
