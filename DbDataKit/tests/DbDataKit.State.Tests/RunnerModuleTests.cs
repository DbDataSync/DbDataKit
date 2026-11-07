using DbDataKit.Monitoring;

namespace DbDataKit.State.Tests;

/// <summary>Plan 28a R04: the <c>runner</c> module and the runs columns the runner records, on every engine.</summary>
public sealed class RunnerModuleTests : IDisposable
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-07T10:00:00Z");

    private readonly StateEngines _engines = new();
    private readonly StateTestClock _clock = new(Start);

    public void Dispose() => _engines.Dispose();

    private StateDatabase Open(string engine) => _engines.Provision(engine).Open([RunnerModule.Module, RunsModule.Module], StateScope.Standalone, time: _clock);

    [Fact]
    public void The_module_follows_the_conventions()
    {
        Assert.Empty(StateConventions.Violations(RunnerModule.Module));
        Assert.Empty(StateConventions.Violations(RunsModule.Module));
    }

    [Fact]
    public void The_module_is_kept_per_group_and_the_retention_rule_names_a_real_table_and_children()
    {
        Assert.Equal(StateScopeKind.Group, RunnerModule.Module.Scope);
        Assert.Equal("runner", RunnerModule.Module.Name);
        var rule = Assert.Single(RunnerModule.Module.Retention);
        Assert.Equal("instance", rule.Table);
        Assert.Equal(["instance_label", "capability"], rule.Children!.Select(c => c.Table));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void The_layout_builds_and_every_table_is_there(string engine)
    {
        var database = Open(engine);
        var layout = database.Layout!;
        using var connection = database.OpenConnection();
        foreach (var table in new[] { "instance", "instance_label", "capability", "posture_choice", "task_projection", "trigger_state" })
        {
            using var cmd = database.Command(RunnerModule.Name, connection, $"SELECT COUNT(*) FROM {{table:{table}}};");
            Assert.Equal(0L, Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
        }

        Assert.Contains(layout.Namespaces, n => n.Module.Name == "runner");
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void An_instance_is_live_until_its_lease_runs_out_and_a_heartbeat_renews_it(string engine)
    {
        var instances = new RunnerInstances(Open(engine));
        var a = instances.Register("console", "host-a", "1.0.0");
        _clock.Advance(TimeSpan.FromSeconds(20));
        var b = instances.Register("dbdatarunner", "host-b", "1.0.1");

        var stored = instances.Get(a)!;
        Assert.Equal(("console", "host-a", "1.0.0", RunnerInstance.Running), (stored.Runner, stored.Host, stored.Version, stored.State));
        Assert.Equal(Start, stored.StartedAt);
        Assert.Equal([a, b], instances.ListLive(TimeSpan.FromSeconds(30)).Select(i => i.Id));

        _clock.Advance(TimeSpan.FromSeconds(20));                       // a: 40 s quiet, b: 20 s
        Assert.Equal([b], instances.ListLive(TimeSpan.FromSeconds(30)).Select(i => i.Id));

        Assert.True(instances.Heartbeat(a));                            // renewed
        Assert.Equal([a, b], instances.ListLive(TimeSpan.FromSeconds(30)).Select(i => i.Id));
        Assert.Equal(Start.AddSeconds(40), instances.Get(a)!.HeartbeatAt);
        Assert.False(instances.Heartbeat(Guid.NewGuid()));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void An_instance_marked_gone_is_not_live_and_cannot_heartbeat_again(string engine)
    {
        var instances = new RunnerInstances(Open(engine));
        var a = instances.Register("dbdatarunner", "host-a", "1.0.0");
        var b = instances.Register("dbdatarunner", "host-b", "1.0.0");

        Assert.True(instances.MarkGone(a));
        Assert.False(instances.MarkGone(a));
        Assert.False(instances.Heartbeat(a));
        Assert.Equal(RunnerInstance.Gone, instances.Get(a)!.State);
        Assert.Equal([b], instances.ListLive(TimeSpan.FromMinutes(5)).Select(i => i.Id));
        Assert.Null(instances.Get(Guid.NewGuid()));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void Labels_are_set_replaced_and_removed_with_their_source(string engine)
    {
        var instances = new RunnerInstances(Open(engine));
        var id = instances.Register("dbdatarunner", "host-a", "1.0.0");
        var other = instances.Register("dbdatarunner", "host-b", "1.0.0");

        instances.SetLabel(id, "gpu", InstanceLabel.Admin);
        instances.SetLabel(id, "windows", InstanceLabel.Reported);
        instances.SetLabel(id, "windows", InstanceLabel.Admin);       // an administrator takes it over
        instances.SetLabel(other, "gpu", InstanceLabel.Reported);

        Assert.Equal([new InstanceLabel("gpu", "admin"), new InstanceLabel("windows", "admin")], instances.Labels(id));
        Assert.True(instances.RemoveLabel(id, "gpu"));
        Assert.False(instances.RemoveLabel(id, "gpu"));
        Assert.Equal([new InstanceLabel("windows", "admin")], instances.Labels(id));
        Assert.Equal([new InstanceLabel("gpu", "reported")], instances.Labels(other));
        Assert.Throws<ArgumentException>(() => instances.SetLabel(id, "x", "somebody"));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void Capabilities_are_replaced_as_a_whole(string engine)
    {
        var instances = new RunnerInstances(Open(engine));
        var id = instances.Register("dbdatarunner", "host-a", "1.0.0");
        var other = instances.Register("dbdatarunner", "host-b", "1.0.0");
        instances.ReplaceCapabilities(other, new Dictionary<string, string> { ["network"] = "inside" });

        instances.ReplaceCapabilities(id, new Dictionary<string, string> { ["network"] = "outside", ["memory_limit"] = "watched" });
        Assert.Equal([("memory_limit", "watched"), ("network", "outside")], instances.Capabilities(id).Select(c => (c.Name, c.Value)));
        Assert.All(instances.Capabilities(id), c => Assert.Equal(Start, c.ReportedAt));

        _clock.Advance(TimeSpan.FromMinutes(1));
        instances.ReplaceCapabilities(id, new Dictionary<string, string> { ["memory_limit"] = "enforced" });
        var now = instances.Capabilities(id);
        Assert.Equal([("memory_limit", "enforced")], now.Select(c => (c.Name, c.Value)));      // network is no longer reported, so it is gone
        Assert.Equal(Start.AddMinutes(1), now[0].ReportedAt);
        Assert.Equal([("network", "inside")], instances.Capabilities(other).Select(c => (c.Name, c.Value)));

        instances.ReplaceCapabilities(id, new Dictionary<string, string>());
        Assert.Empty(instances.Capabilities(id));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_posture_choice_is_recorded_listed_and_found_for_a_package_or_for_all(string engine)
    {
        var choices = new PostureChoices(Open(engine));
        Assert.False(choices.IsChosen("orders", "run_in_process"));

        choices.Record("orders", "run_in_process", "alice", "the vendor library is not thread safe");
        choices.Record(RunnerModule.AllPackages, "longer_limits", "bob");

        Assert.True(choices.IsChosen("orders", "run_in_process"));
        Assert.False(choices.IsChosen("customers", "run_in_process"));          // another package, no choice
        Assert.True(choices.IsChosen("customers", "longer_limits"));            // install-wide
        Assert.False(choices.IsChosen("orders", "privileged_account"));
        var choice = choices.Find("orders", "run_in_process")!;
        Assert.Equal(("orders", "alice", Start, "the vendor library is not thread safe"), (choice.Scope, choice.ChosenBy, choice.ChosenAt, choice.Note));
        Assert.Null(choices.Find("customers", "longer_limits")!.Note);

        _clock.Advance(TimeSpan.FromHours(1));
        choices.Record("orders", "run_in_process", "carol");                    // chosen again: the latest stands
        Assert.Equal(("carol", Start.AddHours(1), (string?)null), (choices.Find("orders", "run_in_process")!.ChosenBy, choices.Find("orders", "run_in_process")!.ChosenAt, choices.Find("orders", "run_in_process")!.Note));

        Assert.Equal([("*", "longer_limits"), ("orders", "run_in_process")], choices.List().Select(c => (c.Scope, c.Setting)));
        Assert.Equal([("orders", "run_in_process")], choices.List("orders").Select(c => (c.Scope, c.Setting)));
        Assert.True(choices.Revoke("orders", "run_in_process"));
        Assert.False(choices.Revoke("orders", "run_in_process"));
        Assert.False(choices.IsChosen("orders", "run_in_process"));
    }

    private static TaskProjectionRow Row(string task, string version = "1.0.0", string? schedule = "{\"cron\":\"0 2 * * *\"}") =>
        new(task, version, schedule, "[{\"after\":\"x\"}]", "{\"timeout\":\"1h\"}", ["gpu", "outside"], "hash-" + task);

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_projection_is_replaced_wholesale_for_a_package_and_channel(string engine)
    {
        var projections = new TaskProjections(Open(engine));
        projections.Replace("orders", "prod", [Row("sync"), Row("report")]);
        projections.Replace("orders", "preview", [Row("sync", "2.0.0-rc1")]);
        projections.Replace("customers", "prod", [Row("sync")]);

        Assert.Equal([("customers", "sync"), ("orders", "report"), ("orders", "sync")], projections.ListByChannel("prod").Select(p => (p.Package, p.Task)));
        var read = projections.Get("orders", "prod", "sync")!;
        Assert.Equal(("1.0.0", "{\"cron\":\"0 2 * * *\"}", "[{\"after\":\"x\"}]", "{\"timeout\":\"1h\"}", "hash-sync", Start), (read.Version, read.Schedule, read.Triggers, read.Limits, read.ProgressHash, read.RebuiltAt));
        Assert.Equal(["gpu", "outside"], read.RequiredLabels);                  // the text goes back exactly as given

        _clock.Advance(TimeSpan.FromMinutes(5));
        projections.Replace("orders", "prod", [Row("sync", "1.1.0", schedule: null), Row("cleanup", "1.1.0")]);

        Assert.Equal([("orders", "cleanup", "1.1.0"), ("orders", "sync", "1.1.0")], projections.ListByChannel("prod", "orders").Select(p => (p.Package, p.Task, p.Version)));
        Assert.Null(projections.Get("orders", "prod", "sync")!.Schedule);
        Assert.Null(projections.Get("orders", "prod", "report"));                // no longer in the manifest
        Assert.Equal(Start.AddMinutes(5), projections.Get("orders", "prod", "cleanup")!.RebuiltAt);
        Assert.Equal("1.0.0", projections.Get("customers", "prod", "sync")!.Version);   // another package untouched
        Assert.Equal("2.0.0-rc1", projections.Get("orders", "preview", "sync")!.Version);   // another channel untouched

        projections.Replace("orders", "prod", []);
        Assert.Empty(projections.ListByChannel("prod", "orders"));
        Assert.Single(projections.ListByChannel("prod"));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_replace_that_fails_part_way_leaves_the_old_rows(string engine)
    {
        var projections = new TaskProjections(Open(engine));
        projections.Replace("orders", "prod", [Row("sync"), Row("report")]);

        // the same task twice breaks the key on the second insert, after the old rows were deleted and the first new one written
        Assert.ThrowsAny<Exception>(() => projections.Replace("orders", "prod", [Row("sync", "9.9.9"), Row("sync", "9.9.9")]));

        Assert.Equal([("report", "1.0.0"), ("sync", "1.0.0")], projections.ListByChannel("prod", "orders").Select(p => (p.Task, p.Version)));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_trigger_remembers_when_it_fired_and_its_cursor(string engine)
    {
        var triggers = new TriggerStates(Open(engine));
        Assert.Null(triggers.Get("orders", "prod", "sync", "files"));

        triggers.Set("orders", "prod", "sync", "files", Start, "inbox/2026-11-07/0042.csv");
        triggers.Set("orders", "prod", "sync", "after-load", null, null);
        triggers.Set("orders", "preview", "sync", "files", Start.AddDays(-1), "older");

        var state = triggers.Get("orders", "prod", "sync", "files")!;
        Assert.Equal((Start, "inbox/2026-11-07/0042.csv"), (state.LastFiredAt, state.Cursor));
        var empty = triggers.Get("orders", "prod", "sync", "after-load")!;
        Assert.Equal((null, null), (empty.LastFiredAt, empty.Cursor));

        triggers.Set("orders", "prod", "sync", "files", Start.AddHours(1), "{\"offset\": 7,  \"a\":1}");   // opaque: kept as written
        state = triggers.Get("orders", "prod", "sync", "files")!;
        Assert.Equal((Start.AddHours(1), "{\"offset\": 7,  \"a\":1}"), (state.LastFiredAt, state.Cursor));
        Assert.Equal("older", triggers.Get("orders", "preview", "sync", "files")!.Cursor);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void The_retention_job_removes_a_quiet_instance_with_its_labels_and_capabilities_and_keeps_the_rest(string engine)
    {
        var database = Open(engine);
        var instances = new RunnerInstances(database);
        var old = instances.Register("dbdatarunner", "old-host", "1.0.0");
        instances.SetLabel(old, "gpu", InstanceLabel.Admin);
        instances.ReplaceCapabilities(old, new Dictionary<string, string> { ["network"] = "outside" });
        _clock.Advance(TimeSpan.FromDays(40));
        var live = instances.Register("dbdatarunner", "new-host", "1.0.0");
        instances.SetLabel(live, "gpu", InstanceLabel.Admin);
        new PostureChoices(database).Record("*", "longer_limits", "alice");
        new TaskProjections(database).Replace("orders", "prod", [Row("sync")]);

        var result = new RetentionJob(database).Run();

        Assert.Equal(1, result.Removed["runner.instance"]);
        Assert.Null(instances.Get(old));
        Assert.Empty(instances.Labels(old));
        Assert.Empty(instances.Capabilities(old));
        Assert.NotNull(instances.Get(live));
        Assert.Single(instances.Labels(live));
        Assert.Single(new PostureChoices(database).List());                     // current state is never aged out
        Assert.Single(new TaskProjections(database).ListByChannel("prod"));
    }

    // ---------- the runs columns (migration 2 of the runs module) ----------

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_run_keeps_its_package_version_channel_and_attempt_and_reads_back_by_package_and_channel(string engine)
    {
        var runs = new RunStore(Open(engine), "dbdatarunner");
        var first = runs.Start("sync-orders", package: "orders", packageVersion: "1.2.0", channel: "prod", attempt: 1);
        _clock.Advance(TimeSpan.FromMinutes(1));
        var retry = runs.Start("sync-orders", package: "orders", packageVersion: "1.2.0", channel: "prod", attempt: 2);
        _clock.Advance(TimeSpan.FromMinutes(1));
        var preview = runs.Start("sync-orders", package: "orders", packageVersion: "2.0.0-rc1", channel: "preview", attempt: 1);
        var plain = runs.Start("old-style-job");
        runs.Finish(first, RunOutcome.Failed, failure: new RunFailure("Temporary:Network", "reset"));

        var read = runs.Get(first)!;
        Assert.Equal(("orders", "1.2.0", "prod", 1), (read.Package, read.PackageVersion, read.Channel, read.Attempt));
        Assert.Equal(RunOutcome.Failed, read.Run.Outcome);                        // Finish leaves the new columns alone
        Assert.Equal(2, runs.Get(retry)!.Attempt);
        var none = runs.Get(plain)!;
        Assert.Equal((null, null, null, null), (none.Package, none.PackageVersion, none.Channel, none.Attempt));

        Assert.Equal([preview, retry, first], runs.Read(new RunQuery(Package: "orders")).Select(r => r.Id));
        Assert.Equal([retry, first], runs.Read(new RunQuery(Package: "orders", Channel: "prod")).Select(r => r.Id));
        Assert.Equal([preview], runs.Read(new RunQuery(Channel: "preview")).Select(r => r.Id));
        Assert.Empty(runs.Read(new RunQuery(Package: "customers")));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void The_runs_of_before_the_migration_keep_their_rows_and_read_with_null_package_columns(string engine)
    {
        var location = _engines.Provision(engine);
        var before = new StateModule(RunsModule.Name, StateScopeKind.Install, [RunsModule.Module.Migrations[0]], retention: RunsModule.Module.Retention);

        var id = Guid.NewGuid();
        {
            // the structure of Q12, written with plain statements: the current RunStore names columns the old one does not have
            var old = location.Open([before], time: _clock);
            using var connection = old.OpenConnection();
            using (var run = old.Command(RunsModule.Name, connection, "INSERT INTO {table:run} (id, service, job_id, started_at, finished_at, outcome, rows_moved) VALUES ($id, $service, $job, $at, $at, $outcome, $rows);"))
            {
                run.Bind(old, "id", id).Bind(old, "service", "dbdatasync").Bind(old, "job", "orders-sync").Bind(old, "at", Start).Bind(old, "outcome", "Succeeded").Bind(old, "rows", 12L);
                run.ExecuteNonQuery();
            }

            using var log = old.Command(RunsModule.Name, connection, "INSERT INTO {table:run_log} (run_id, logged_at, level, message) VALUES ($id, $at, $level, $message);");
            log.Bind(old, "id", id).Bind(old, "at", Start).Bind(old, "level", "Info").Bind(old, "message", "written before");
            log.ExecuteNonQuery();
        }

        var database = location.Open([RunsModule.Module], time: _clock);       // opening with the current module applies migration 2
        var store = new RunStore(database, "dbdatasync");
        var kept = store.Get(id)!;
        Assert.Equal(("orders-sync", RunOutcome.Succeeded, 12L), (kept.Run.JobId, kept.Run.Outcome, kept.Run.Rows));
        Assert.Equal((null, null, null, null), (kept.Package, kept.PackageVersion, kept.Channel, kept.Attempt));
        Assert.Equal(["written before"], store.Logs(id).Select(l => l.Message));

        var next = store.Start("orders-sync", package: "orders", packageVersion: "1.0.0", channel: "prod", attempt: 1);
        Assert.Equal("orders", store.Get(next)!.Package);
        Assert.Equal(2, RunsModule.Module.Version);
    }
}
