using DbDataBuild.Cli.Server;
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
    [InlineData("tracking:\n  mode: state\n", "keeps the records on the target first", "tracking")]
    [InlineData("tracking:\n  mode: both\n  primary: state\n", "keeps the records on the target first", "tracking")]
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
}
