using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Secrets;
using DbDataKit.State.Engines.Snapshots;
using static DbDataKit.State.Api.Tests.ScratchDatabases;

namespace DbDataKit.State.Api.Tests;

/// <summary>The server moves its own state (plan 29b, C06): <c>POST</c> and <c>GET /api/v1/state/move</c>, from SQLite to SQL Server and to PostgreSQL.</summary>
public sealed class StateMoveTests : IDisposable
{
    private const string PasswordVariable = "STATE_TEST_PASSWORD";
    private readonly ScratchDatabases _databases = new();
    private readonly string _directory = Directory.CreateTempSubdirectory("dbdatakit-statemove-").FullName;

    public void Dispose()
    {
        _databases.Dispose();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private sealed record Setup(StateApiHost Host, Db Source, Db Target, string SettingsFile, MemoryAudit Audit, string Password);

    private Setup Create(string targetEngine, bool move = true, Func<MoveOptions, MoveOptions>? tune = null, bool targetHoldsModules = false)
    {
        var source = _databases.Provision(StateEngineIds.Sqlite);
        source.Open([Module("alpha")]);
        var target = _databases.Provision(targetEngine);
        if (targetHoldsModules) target.Open([Module("alpha")]).RegisteredModules();
        var password = target.PasswordOf()!;
        Environment.SetEnvironmentVariable(PasswordVariable, password);
        var settings = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".yaml");
        var audit = new MemoryAudit();
        var options = StateApiHost.Options(source, [Module("alpha")], resolver: () => new SecretResolverChain([new EnvSecretProvider()]), audit: audit, settingsFile: settings, move: move, tuneMove: tune);
        return new Setup(new StateApiHost(options, options.Gate), source, target, settings, audit, password);
    }

    private static string Body(Setup s, string? connection = null, bool dryRun = false) =>
        new JsonObject { ["engine"] = EngineName(s.Target.Engine), ["connection"] = connection ?? s.Target.WithPasswordReference(), ["dry_run"] = dryRun }.ToJsonString();

    private static async Task Maintenance(Setup s)
    {
        var (response, _) = await s.Host.SendAsync(HttpMethod.Post, "/api/v1/state/maintenance", "admin", json: """{"enabled": true, "reason": "moving"}""");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<JsonNode> Finished(Setup s)
    {
        for (var i = 0; i < 600; i++)
        {
            var (_, text) = await s.Host.SendAsync(HttpMethod.Get, "/api/v1/state/move", "viewer");
            var node = StateApiHost.Parse(text);
            if ((string?)node["state"] is "succeeded" or "failed") return node;
            await Task.Delay(100);
        }

        throw new TimeoutException("The move did not finish.");
    }

    [Theory]
    [InlineData(StateEngineIds.Postgres)]
    [InlineData(StateEngineIds.MsSql)]
    public async Task A_frozen_server_moves_its_state_and_the_settings_are_written(string engine)
    {
        var s = Create(engine);
        await using var host = s.Host;
        await Maintenance(s);

        var (start, startText) = await host.SendAsync(HttpMethod.Post, "/api/v1/state/move", "admin", "carol", Body(s));
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
        var done = await Finished(s);

        Assert.Equal("succeeded", (string?)done["state"]);
        Assert.True((bool)done["settings_changed"]!);
        Assert.Contains("Restart", (string?)done["restart"]);
        Assert.NotNull((string?)done["way_back"]);
        Assert.NotEmpty(done["tables"]!.AsArray());
        Assert.True((bool)done["verified_against_snapshot"]!);
        Assert.True((bool)done["verified_against_source"]!);
        Assert.Equal(engine, (string?)done["to_engine"]);
        Assert.Contains("env:" + PasswordVariable, (string?)done["to_connection"]);

        // the settings name the new engine with the reference, never the password, and the target holds the module
        var yaml = File.ReadAllText(s.SettingsFile);
        Assert.Contains(EngineName(engine), yaml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("env:" + PasswordVariable, yaml);
        Assert.DoesNotContain(s.Password, yaml);
        Assert.Contains(s.Target.Open([Module("alpha")]).RegisteredModules(), m => m.Module == "alpha");

        // the server stays in maintenance, and no response or audit entry holds the password
        Assert.True(host.Gate.Enabled);
        Assert.DoesNotContain(s.Password, startText + done.ToJsonString());
        Assert.DoesNotContain(s.Audit.Entries, e => (e.Target ?? "").Contains(s.Password, StringComparison.Ordinal));

        // audited: who started it and how it ended
        var entries = s.Audit.Entries.Where(e => e.Command == "state.move").ToList();
        Assert.Contains(entries, e => e.Outcome == "started" && e.UserId == "carol");
        Assert.Contains(entries, e => e.Outcome == "ok");

        // a second move needs the restart first
        var (again, _) = await host.SendAsync(HttpMethod.Post, "/api/v1/state/move", "admin", json: Body(s));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task A_viewer_cannot_move_and_the_refusal_is_audited()
    {
        var s = Create(StateEngineIds.Postgres);
        await using var host = s.Host;
        await Maintenance(s);
        var (response, _) = await host.SendAsync(HttpMethod.Post, "/api/v1/state/move", "viewer", "vera", Body(s));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(s.Audit.Entries, e => e.Command == "state.move" && e.Outcome == "refused" && e.UserId == "vera");
        Assert.False(File.Exists(s.SettingsFile));
        var (anonymous, _) = await host.SendAsync(HttpMethod.Get, "/api/v1/state/move", null);
        Assert.Equal(HttpStatusCode.Forbidden, anonymous.StatusCode);
    }

    [Fact]
    public async Task A_server_not_in_maintenance_or_not_idle_is_refused_with_409()
    {
        var s = Create(StateEngineIds.Postgres);
        await using var host = s.Host;
        var (open, text) = await host.SendAsync(HttpMethod.Post, "/api/v1/state/move", "admin", json: Body(s));
        Assert.Equal(HttpStatusCode.Conflict, open.StatusCode);
        Assert.Contains("maintenance", text);

        // maintenance on, a request still in flight: not idle
        var slow = host.Client.GetAsync("/slow");
        await host.SlowStarted;
        await Maintenance(s);
        var (busy, busyText) = await host.SendAsync(HttpMethod.Post, "/api/v1/state/move", "admin", json: Body(s));
        Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
        Assert.Contains("idle", busyText);
        host.ReleaseSlow();
        await slow;
        Assert.False(File.Exists(s.SettingsFile));
    }

    [Fact]
    public async Task A_target_that_already_holds_modules_is_refused_and_nothing_changes()
    {
        var s = Create(StateEngineIds.Postgres, targetHoldsModules: true);
        await using var host = s.Host;
        await Maintenance(s);
        var (response, text) = await host.SendAsync(HttpMethod.Post, "/api/v1/state/move", "admin", json: Body(s));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("empty", text);
        Assert.DoesNotContain(s.Password, text);
        Assert.False(File.Exists(s.SettingsFile));
    }

    [Fact]
    public async Task A_failure_after_the_import_leaves_the_settings_unchanged_and_says_what_was_kept()
    {
        var s = Create(StateEngineIds.Postgres, tune: o => o with { AfterImport = (_, _) => throw new InvalidOperationException("injected failure") });
        await using var host = s.Host;
        await File.WriteAllTextAsync(s.SettingsFile, "listen: 1\n");
        await Maintenance(s);
        var (start, _) = await host.SendAsync(HttpMethod.Post, "/api/v1/state/move", "admin", json: Body(s));
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
        var done = await Finished(s);

        Assert.Equal("failed", (string?)done["state"]);
        Assert.False((bool)done["settings_changed"]!);
        Assert.Contains("injected failure", (string?)done["error"]);
        Assert.Contains("was not changed", (string?)done["kept"]);
        Assert.Equal("listen: 1\n", File.ReadAllText(s.SettingsFile));
        Assert.Empty(Directory.GetFiles(_directory, "*.before-state-move*"));
        Assert.DoesNotContain(s.Password, done.ToJsonString());
        Assert.Contains(s.Audit.Entries, e => e.Command == "state.move" && e.Outcome == "failed");
    }

    [Fact]
    public async Task A_password_written_out_is_refused_and_a_dry_run_changes_nothing()
    {
        var s = Create(StateEngineIds.Postgres);
        await using var host = s.Host;
        await Maintenance(s);
        var (literal, text) = await host.SendAsync(HttpMethod.Post, "/api/v1/state/move", "admin", json: Body(s, connection: s.Target.Target));
        Assert.Equal(HttpStatusCode.BadRequest, literal.StatusCode);
        Assert.DoesNotContain(s.Password, text);

        var (dry, dryText) = await host.SendAsync(HttpMethod.Post, "/api/v1/state/move", "admin", json: Body(s, dryRun: true));
        Assert.Equal(HttpStatusCode.OK, dry.StatusCode);
        Assert.Equal("preflight_ok", (string?)StateApiHost.Parse(dryText)["state"]);
        Assert.False(File.Exists(s.SettingsFile));
        Assert.Equal("none", (string?)StateApiHost.Parse((await host.SendAsync(HttpMethod.Get, "/api/v1/state/move", "viewer")).Text)["state"]);

        // a reference nobody can resolve is named, with the command that sets it
        var (missing, missingText) = await host.SendAsync(HttpMethod.Post, "/api/v1/state/move", "admin",
            json: Body(s, connection: s.Target.WithPasswordReference().Replace("env:" + PasswordVariable, "env:NO_SUCH_STATE_VARIABLE_X"), dryRun: true));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("secret set", missingText);
    }

    [Fact]
    public async Task A_product_without_a_settings_file_has_no_move_call()
    {
        var s = Create(StateEngineIds.Postgres, move: false);
        await using var host = s.Host;
        var (post, _) = await host.SendAsync(HttpMethod.Post, "/api/v1/state/move", "admin", json: Body(s));
        var (get, _) = await host.SendAsync(HttpMethod.Get, "/api/v1/state/move", "admin");
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }
}
