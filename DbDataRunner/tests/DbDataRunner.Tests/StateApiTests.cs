using System.Net;
using DbDataKit.Monitoring;
using DbDataKit.State;
using DbDataRunner.Server;
using DbDataKit.Web.Trust;

namespace DbDataRunner.Tests;

/// <summary>The runner on the state API of every server (plan 29b C05): a paired console reads its state, puts it into maintenance (it claims and schedules nothing new, reports idle) and moves it, as it does for the Gateway, DbDataStore and Build.</summary>
public sealed class StateApiTests
{
    [Fact]
    public async Task A_viewer_reads_the_runners_state_and_an_administrator_puts_it_into_maintenance_and_out_again()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        await h.StartAsync();

        var (status, state) = await h.GetAsync("/api/v1/state");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("dbdatarunner", (string?)state!["product"]);
        Assert.Equal("sqlite", (string?)state["engine"], ignoreCase: true);
        Assert.Contains(state["modules"]!.AsArray(), m => (string?)m!["module"] == "runner");

        var (refused, _) = await h.PostAsync("/api/v1/state/maintenance", ServerRole.Operator, new { enabled = true, reason = "move" });
        Assert.Equal(HttpStatusCode.Forbidden, refused);
        Assert.False(h.Engine.Draining);

        var (entered, body) = await h.PostAsync("/api/v1/state/maintenance", ServerRole.Admin, new { enabled = true, reason = "moving the state to PostgreSQL" });
        Assert.Equal(HttpStatusCode.OK, entered);
        Assert.True((bool)body!["enabled"]!);
        Assert.True(h.Engine.Draining);
        // nothing runs, so the runner reports idle at once
        Assert.True((bool)body["idle"]!);

        var (left, after) = await h.PostAsync("/api/v1/state/maintenance", ServerRole.Admin, new { enabled = false });
        Assert.Equal(HttpStatusCode.OK, left);
        Assert.False((bool)after!["enabled"]!);
        Assert.False(h.Engine.Draining);
        Assert.Contains(h.Audit.Read(new AuditQuery(VerbPrefix: "state.")), r => r.Entry.Verb == "state.maintenance" && r.Entry.Outcome == AuditEvent.Ok);
    }

    [Fact]
    public void The_runner_reads_the_shared_state_section_of_its_server_yaml_and_says_where_it_came_from()
    {
        var directory = Directory.CreateTempSubdirectory("ddr-state-section-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(directory, "server.yaml"), "state:\n  engine: postgres\n  connection: \"Host=db;Database=runner;Username=svc;Password=env:RUNNER_DB\"\n");
            var resolved = RunnerState.Resolve(directory, _ => null);
            Assert.Equal(StateEngineIds.Postgres, resolved.Settings.EngineId);
            Assert.Equal(StateSourceKind.File, resolved.Sources.Engine.Kind);
            Assert.Equal(StateSourceKind.Default, RunnerState.Resolve(Path.Combine(directory, "none"), _ => null).Sources.Engine.Kind);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task The_runner_offers_the_move_and_a_dry_run_to_a_sqlite_file_on_its_machine_answers_the_checks_of_its_own_modules()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        await h.StartAsync();
        await h.PostAsync("/api/v1/state/maintenance", ServerRole.Admin, new { enabled = true, reason = "moving" });
        var target = Path.Combine(h.Directory, "moved", "state.db");

        var (status, body) = await h.PostAsync("/api/v1/state/move", ServerRole.Admin, new { engine = "sqlite", sqlite_path = target, dry_run = true });

        Assert.True(status == HttpStatusCode.OK, body?.ToJsonString());
        Assert.Equal("preflight_ok", (string?)body!["state"]);
        Assert.NotEmpty(body["steps"]!.AsArray());
        var (noPath, refusal) = await h.PostAsync("/api/v1/state/move", ServerRole.Admin, new { engine = "sqlite", dry_run = true });
        Assert.Equal(HttpStatusCode.BadRequest, noPath);
        Assert.Contains("sqlite_path", (string?)refusal!["error"]);
    }
}
