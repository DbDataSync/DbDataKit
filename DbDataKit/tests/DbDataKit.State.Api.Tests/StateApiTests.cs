using System.Net;
using DbDataKit.Secrets;
using DbDataKit.Web.Trust;
using static DbDataKit.State.Api.Tests.ScratchDatabases;

namespace DbDataKit.State.Api.Tests;

/// <summary>The state endpoints (plan 29b, C05): <c>GET /api/v1/state</c>, and the maintenance mode with its middleware.</summary>
public sealed class StateApiTests : IDisposable
{
    private readonly ScratchDatabases _databases = new();
    private readonly List<StateApiHost> _hosts = [];

    public void Dispose()
    {
        foreach (var host in _hosts) host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _databases.Dispose();
    }

    private StateApiHost Host(StateManagementOptions options, IMaintenanceGate? gate = null)
    {
        var host = new StateApiHost(options, gate ?? options.Gate);
        _hosts.Add(host);
        return host;
    }

    private StateApiHost SqliteHost(out ScratchDatabases.Db db, IMaintenanceGate? gate = null)
    {
        db = _databases.Provision(StateEngineIds.Sqlite);
        db.Open([Module("alpha")]);
        return Host(StateApiHost.Options(db, [Module("alpha")], gate: gate, audit: new MemoryAudit()));
    }

    [Theory]
    [MemberData(nameof(Engines), MemberType = typeof(ScratchDatabases))]
    public async Task A_viewer_reads_the_state_with_a_masked_connection_and_the_pending_migration(string engine)
    {
        var db = _databases.Provision(engine);
        db.Open([Module("alpha", 1)]);   // the database as an older binary made it
        await using var host = new StateApiHost(StateApiHost.Options(db, [Module("alpha")], audit: new MemoryAudit()), new MaintenanceGate());

        var (response, text) = await host.SendAsync(HttpMethod.Get, "/api/v1/state", "viewer");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var state = StateApiHost.Parse(text);
        Assert.Equal("testproduct", (string?)state["product"]);
        Assert.Equal("1.2.3", (string?)state["software_version"]);
        Assert.Equal(ManagementApi.Version, (int)state["management_api_version"]!);
        Assert.Equal(engine, (string?)state["engine"]);
        Assert.Equal(1, (int)state["pending_migrations"]!);
        var module = Assert.Single(state["modules"]!.AsArray());
        Assert.Equal("alpha", (string?)module!["module"]);
        Assert.Equal(2, (int)module["binary_version"]!);
        Assert.Equal(1, (int)module["registered_version"]!);
        Assert.Equal(1, (int)module["pending"]!);
        Assert.Equal("default", (string?)state["sources"]!["engine"]!["kind"]);
        Assert.Equal("instance-1", (string?)state["instance"]!["instance_id"]);
        Assert.Equal(2, (int)state["instance"]!["modules"]![0]!["binary_version"]!);
        Assert.Equal(1, (int)state["instance"]!["modules"]![0]!["registered_version"]!);
        Assert.Empty(state["instances"]!.AsArray());
        Assert.False((bool)state["maintenance"]!["enabled"]!);

        // read-only: the migration was not applied
        Assert.Equal(1, (int)StateApiHost.Parse((await host.SendAsync(HttpMethod.Get, "/api/v1/state", "viewer")).Text)["pending_migrations"]!);

        if (engine != StateEngineIds.Sqlite)
        {
            var password = db.PasswordOf();
            Assert.False(string.IsNullOrEmpty(password));
            Assert.DoesNotContain(password!, text);
            Assert.Contains("***", (string?)state["connection"]);
        }
    }

    [Theory]
    [MemberData(nameof(Engines), MemberType = typeof(ScratchDatabases))]
    public async Task A_secret_reference_in_the_connection_is_shown_as_written_and_never_resolved(string engine)
    {
        if (engine == StateEngineIds.Sqlite) return;   // no connection string
        var db = _databases.Provision(engine);
        db.Open([Module("alpha")]);
        var password = db.PasswordOf()!;
        var resolver = new SecretResolverChain([new EnvSecretProvider(name => name == "STATE_TEST_PASSWORD" ? password : null)]);
        await using var host = new StateApiHost(StateApiHost.Options(db, [Module("alpha")], db.WithPasswordReference(), () => resolver, audit: new MemoryAudit()), new MaintenanceGate());

        var (response, text) = await host.SendAsync(HttpMethod.Get, "/api/v1/state", "viewer");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var state = StateApiHost.Parse(text);
        Assert.Contains("env:STATE_TEST_PASSWORD", (string?)state["connection"]);
        Assert.DoesNotContain(password, text);
        Assert.Equal(0, (int)state["pending_migrations"]!);
        Assert.True((bool)state["connected"]!);
    }

    [Fact]
    public async Task A_literal_password_in_the_connection_never_appears_in_any_answer()
    {
        // a server that is not there: the connection fails, and neither the masked connection nor the failure may show the password
        var db = new ScratchDatabases.Db(StateEngineIds.Postgres, "unused");
        var connection = $"Host=127.0.0.1;Port=1;Database=nothing;Username=somebody;Password={Password};Timeout=2;Pooling=false";
        await using var host = new StateApiHost(StateApiHost.Options(db, [Module("alpha")], connection, audit: new MemoryAudit()), new MaintenanceGate());

        foreach (var path in new[] { "/api/v1/state", "/api/v1/state/maintenance" })
        {
            var (response, text) = await host.SendAsync(HttpMethod.Get, path, "viewer");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.DoesNotContain(Password, text);
        }

        var state = StateApiHost.Parse((await host.SendAsync(HttpMethod.Get, "/api/v1/state", "viewer")).Text);
        Assert.False((bool)state["read"]!);
        Assert.NotNull((string?)state["error"]);
        Assert.False((bool)state["connected"]!);

        var (_, enabled) = await host.SendAsync(HttpMethod.Post, "/api/v1/state/maintenance", "admin", json: """{"enabled": true, "reason": "check"}""");
        Assert.DoesNotContain(Password, enabled);
        Assert.DoesNotContain(Password, string.Join('\n', host.Audit.Entries.Select(e => e.Target + e.Outcome + e.Command)));
    }

    [Fact]
    public async Task A_call_with_no_role_reads_nothing()
    {
        var host = SqliteHost(out _);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendAsync(HttpMethod.Get, "/api/v1/state", null)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendAsync(HttpMethod.Get, "/api/v1/state/maintenance", null)).Response.StatusCode);
    }

    [Fact]
    public async Task A_viewer_cannot_enable_maintenance_and_the_refusal_is_audited()
    {
        var host = SqliteHost(out _);

        var (response, text) = await host.SendAsync(HttpMethod.Post, "/api/v1/state/maintenance", "viewer", "victor", """{"enabled": true, "reason": "let me in"}""");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("admin", text);
        Assert.False(host.Gate.Enabled);
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Get, "/work", "viewer")).Response.StatusCode);
        var entry = Assert.Single(host.Audit.Entries);
        Assert.Equal("state.maintenance", entry.Command);
        Assert.Equal("refused", entry.Outcome);
        Assert.Equal("victor", entry.UserId);
    }

    [Fact]
    public async Task An_admin_enables_maintenance_and_ordinary_requests_get_503_with_the_reason()
    {
        var host = SqliteHost(out _);

        var (enabled, enabledText) = await host.SendAsync(HttpMethod.Post, "/api/v1/state/maintenance", "admin", "alice", """{"enabled": true, "reason": "moving the state to PostgreSQL"}""");

        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        var status = StateApiHost.Parse(enabledText);
        Assert.True((bool)status["enabled"]!);
        Assert.True((bool)status["idle"]!);
        Assert.Equal("moving the state to PostgreSQL", (string?)status["reason"]);
        Assert.Equal(0, (int)status["inflight"]!);
        Assert.NotNull((string?)status["since"]);

        var (refused, refusedText) = await host.SendAsync(HttpMethod.Get, "/work", "viewer");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Equal("30", refused.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var body = StateApiHost.Parse(refusedText);
        Assert.Equal("moving the state to PostgreSQL", (string?)body["reason"]);
        Assert.True((bool)body["maintenance"]!);
        Assert.Contains("moving the state to PostgreSQL", (string?)body["error"]);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await host.SendAsync(HttpMethod.Get, "/work", null)).Response.StatusCode);

        // the management state, the health endpoint and an administrator's own requests still answer
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Get, "/api/v1/state", "viewer")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Get, "/api/v1/state/maintenance", "viewer")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Get, "/health", null)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Get, "/work", "admin")).Response.StatusCode);

        var state = StateApiHost.Parse((await host.SendAsync(HttpMethod.Get, "/api/v1/state", "viewer")).Text);
        Assert.True((bool)state["maintenance"]!["enabled"]!);
    }

    [Fact]
    public async Task Idle_turns_true_when_the_requests_in_flight_have_finished()
    {
        var host = SqliteHost(out _);
        var slow = host.Client.SendAsync(host.Request(HttpMethod.Get, "/slow", "viewer"));
        await host.SlowStarted;

        var (_, enabledText) = await host.SendAsync(HttpMethod.Post, "/api/v1/state/maintenance", "admin", json: """{"enabled": true, "reason": "drain"}""");
        var status = StateApiHost.Parse(enabledText);
        Assert.True((bool)status["enabled"]!);
        Assert.False((bool)status["idle"]!);
        Assert.Equal(1, (int)status["inflight"]!);

        // new work is refused meanwhile, and the polling of the status does not count as work
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await host.SendAsync(HttpMethod.Get, "/work", "viewer")).Response.StatusCode);
        Assert.False((bool)StateApiHost.Parse((await host.SendAsync(HttpMethod.Get, "/api/v1/state/maintenance", "viewer")).Text)["idle"]!);

        host.ReleaseSlow();
        Assert.Equal(HttpStatusCode.OK, (await slow).StatusCode);   // what was in flight finished normally

        var after = StateApiHost.Parse((await host.SendAsync(HttpMethod.Get, "/api/v1/state/maintenance", "viewer")).Text);
        Assert.True((bool)after["idle"]!);
        Assert.Equal(0, (int)after["inflight"]!);
    }

    [Fact]
    public async Task The_products_own_work_counts_before_the_server_is_idle()
    {
        var running = 2;
        var gate = new MaintenanceGate(work: () => Volatile.Read(ref running));
        var host = SqliteHost(out _, gate);
        await host.SendAsync(HttpMethod.Post, "/api/v1/state/maintenance", "admin", json: """{"enabled": true, "reason": "plans"}""");

        var busy = StateApiHost.Parse((await host.SendAsync(HttpMethod.Get, "/api/v1/state/maintenance", "viewer")).Text);
        Assert.False((bool)busy["idle"]!);
        Assert.Equal(2, (int)busy["inflight"]!);

        Volatile.Write(ref running, 0);
        Assert.True((bool)StateApiHost.Parse((await host.SendAsync(HttpMethod.Get, "/api/v1/state/maintenance", "viewer")).Text)["idle"]!);
    }

    [Fact]
    public async Task Disabling_restores_service_and_runs_the_product_hooks()
    {
        var entered = new List<string>();
        var left = 0;
        var gate = new MaintenanceGate(onEnter: entered.Add, onLeave: () => left++);
        var host = SqliteHost(out _, gate);

        await host.SendAsync(HttpMethod.Post, "/api/v1/state/maintenance", "admin", json: """{"enabled": true, "reason": "first"}""");
        await host.SendAsync(HttpMethod.Post, "/api/v1/state/maintenance", "admin", json: """{"enabled": true, "reason": "second"}""");   // again: the reason changes, the hook does not run twice
        Assert.Equal(["first"], entered);
        Assert.Equal("second", gate.Status().Reason);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await host.SendAsync(HttpMethod.Get, "/work", "viewer")).Response.StatusCode);

        var (response, text) = await host.SendAsync(HttpMethod.Post, "/api/v1/state/maintenance", "admin", json: """{"enabled": false}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((bool)StateApiHost.Parse(text)["enabled"]!);
        Assert.Equal(1, left);
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Get, "/work", "viewer")).Response.StatusCode);
        Assert.Null((string?)StateApiHost.Parse(text)["reason"]);
    }

    [Fact]
    public async Task Every_change_is_audited_with_the_actor_and_the_reason()
    {
        var host = SqliteHost(out _);

        await host.SendAsync(HttpMethod.Post, "/api/v1/state/maintenance", "admin", "alice", """{"enabled": true, "reason": "window 14"}""");
        await host.SendAsync(HttpMethod.Post, "/api/v1/state/maintenance", "admin", "bob", """{"enabled": false}""");

        var entries = host.Audit.Entries;
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => { Assert.Equal("state.maintenance", e.Command); Assert.Equal("ok", e.Outcome); });
        Assert.Equal("alice", entries[0].UserId);
        Assert.Contains("window 14", entries[0].Target);
        Assert.StartsWith("enable", entries[0].Target);
        Assert.Equal("bob", entries[1].UserId);
        Assert.Equal("disable", entries[1].Target);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"reason": "no flag"}""")]
    [InlineData("""{"enabled": "yes", "reason": "x"}""")]
    [InlineData("""{"enabled": true}""")]
    [InlineData("""{"enabled": true, "reason": 7}""")]
    public async Task A_malformed_request_is_refused_audited_and_changes_nothing(string json)
    {
        var host = SqliteHost(out _);

        var (response, _) = await host.SendAsync(HttpMethod.Post, "/api/v1/state/maintenance", "admin", json: json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(host.Gate.Enabled);
        Assert.Equal("refused", Assert.Single(host.Audit.Entries).Outcome);
    }

    [Fact]
    public async Task The_other_live_instances_come_from_the_products_directory()
    {
        var db = _databases.Provision(StateEngineIds.Sqlite);
        db.Open([Module("alpha")]);
        var instances = new FixedInstances(
            new StateInstance("a", IsSelf: true, Host: "machine-1", ProcessId: 10),
            new StateInstance("b", IsSelf: false, Host: "machine-2", ProcessId: 20, Version: "1.2.2", Modules: new Dictionary<string, int> { ["alpha"] = 1 }));
        await using var host = new StateApiHost(StateApiHost.Options(db, [Module("alpha")], instances: instances, audit: new MemoryAudit()), new MaintenanceGate());

        var state = StateApiHost.Parse((await host.SendAsync(HttpMethod.Get, "/api/v1/state", "viewer")).Text);

        var listed = state["instances"]!.AsArray();
        Assert.Equal(2, listed.Count);
        Assert.True((bool)listed[0]!["self"]!);
        Assert.False((bool)listed[1]!["self"]!);
        Assert.Equal("1.2.2", (string?)listed[1]!["software_version"]);
        Assert.Equal(1, (int)listed[1]!["modules"]!["alpha"]!);
    }

    [Fact]
    public async Task Settings_that_cannot_be_used_are_said_in_the_answer()
    {
        var db = new ScratchDatabases.Db(StateEngineIds.Sqlite, "unused");
        var options = StateApiHost.Options(db, [Module("alpha")], audit: new MemoryAudit()) with { Target = () => throw new InvalidOperationException("server.yaml: state.engine 'oracle' is not an engine") };
        await using var host = new StateApiHost(options, new MaintenanceGate());

        var (response, text) = await host.SendAsync(HttpMethod.Get, "/api/v1/state", "viewer");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var state = StateApiHost.Parse(text);
        Assert.False((bool)state["read"]!);
        Assert.Contains("server.yaml", (string?)state["error"]);
    }

    [Fact]
    public void The_audit_in_the_state_records_the_verb_the_actor_and_the_reason_and_no_secret()
    {
        var db = _databases.Provision(StateEngineIds.Sqlite);
        var state = StateOpener.Open(new StateSettings(), db.Target.Replace("state.db", "audit.db"), [AuditModule.Module], StateScope.Install);
        Directory.CreateDirectory(Path.GetDirectoryName(db.Target)!);
        var sink = new StateAuditSink(state, "testproduct");
        var caller = StateCaller.FromSession("user-7", "Alice", _ => true);

        sink.Append(caller.Audit(StateManagement.AuditCommand, "enable: moving", "ok", DateTimeOffset.UtcNow));
        sink.Append(caller.Audit(StateManagement.AuditCommand, "disable", "refused", DateTimeOffset.UtcNow));

        var records = new AuditLog(state, "testproduct").Read(new AuditQuery(Verb: "state.maintenance"));
        Assert.Equal(2, records.Count);
        Assert.All(records, r => Assert.Equal("user-7", r.Entry.ActorId));
        Assert.Contains(records, r => r.Entry.Outcome == "ok" && r.Entry.TargetId == "enable: moving");
        Assert.Contains(records, r => r.Entry.Outcome == "refused");
        Assert.Equal(2, sink.Read().Count);
    }

    private sealed class FixedInstances(params StateInstance[] instances) : IInstanceDirectory
    {
        public IReadOnlyList<StateInstance> Live() => instances;
    }
}
