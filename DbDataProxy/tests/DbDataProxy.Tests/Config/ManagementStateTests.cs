using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataProxy.Tests.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataProxy.Tests.Config;

/// <summary>The Proxy's state endpoints (plan 29b, C05): <c>GET /api/v1/state</c>, the maintenance mode, the audit, on the engines the Proxy's state can be on.</summary>
public sealed partial class ManagementTests
{
    private async Task StartOnAsync(string engine, StateVerbDatabases databases)
    {
        if (engine == "sqlite") { await StartAsync(); return; }
        var connection = databases.Provision(engine);
        Environment.SetEnvironmentVariable(StateVerbDatabases.PasswordVariable, databases.Password);
        await StartAsync(extra: [$"--Proxy:State:Engine={engine}", $"--Proxy:State:Connection={connection}"]);
    }

    private static async Task<JsonNode> Json(HttpResponseMessage response) => JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    [Theory]
    [MemberData(nameof(StateVerbDatabases.Engines), MemberType = typeof(StateVerbDatabases))]
    public async Task A_viewer_reads_the_state_with_a_masked_connection_the_module_versions_and_the_live_instances(string engine)
    {
        using var databases = new StateVerbDatabases();
        try
        {
            await StartOnAsync(engine, databases);
            // a group (a server engine) keeps its pairing codes in the state it shares
            var codes = new PairingFiles(DbDataProxy.Server.Configuration.ProxyConfigFile.PathIn(dataDirectory), Path.Combine(dataDirectory, "pairing-codes.json"), engine == "sqlite" ? null : app.Services.GetRequiredService<StateDatabase>()).Codes();
            var server = await PairingClient.PairAsync(ManagementUrl, codes.Create(ServerRoles.Close(ServerRole.Admin)).Code, console);

            var response = await CallAsync(server, HttpMethod.Get, "/api/v1/state", ServerRole.Viewer);
            var text = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var state = JsonNode.Parse(text)!;
            Assert.Equal("dbdataproxy", (string?)state["product"]);
            Assert.Equal(server.InstanceId, (string?)state["instance_id"]);
            Assert.Equal(engine == "sqlite" ? StateEngineIds.Sqlite : engine == "mssql" ? StateEngineIds.MsSql : StateEngineIds.Postgres, (string?)state["engine"]);
            Assert.True((bool)state["connected"]!);
            Assert.Equal(0, (int)state["pending_migrations"]!);
            var modules = state["modules"]!.AsArray();
            Assert.Contains(modules, m => (string?)m!["module"] == "serving" && (int)m["binary_version"]! == (int)m["registered_version"]!);
            Assert.Contains(modules, m => (string?)m!["module"] == "identity");

            // the instance table: this process, marked as the one that answered
            var instance = Assert.Single(state["instances"]!.AsArray())!;
            Assert.True((bool)instance["self"]!);
            Assert.False(string.IsNullOrEmpty((string?)instance["host"]));
            // U02: its row carries the software version and the module versions of this binary (a round trip through the serving_instance table)
            Assert.False(string.IsNullOrEmpty((string?)instance["software_version"]));
            var serving = DbDataProxy.Server.State.ServingModule.Module;
            Assert.Equal(serving.Version, (int)instance["module_versions"]!["serving"]!);
            Assert.All(DbDataProxy.Server.State.ProxyState.Modules, m => Assert.Equal(m.Version, (int)instance["module_versions"]![m.Name]!));
            // 24a UC01: and the formats of each kind of published definition it reads
            var formats = instance["definition_formats"]!.AsObject();
            Assert.Equal(["data-source", "entity-model", "semantic-model"], formats.Select(f => f.Key).Order(StringComparer.Ordinal));
            Assert.All(formats, f => Assert.Equal(1, (int)f.Value!["current"]!));

            // two instances at different versions are listed with their versions: another instance, one version behind, registers its row
            var stateDatabase = app.Services.GetRequiredService<StateDatabase>();
            using (var connection = stateDatabase.OpenConnection())
            {
                using var insert = stateDatabase.Command("serving", connection, "INSERT INTO {table:serving_instance} (instance_key, host, process_id, started_at, heartbeat_at, software_version, module_versions) VALUES ($key, $host, $pid, $at, $at, $software, $modules);");
                insert.Bind(stateDatabase, "key", "older-instance").Bind(stateDatabase, "host", "other-host").Bind(stateDatabase, "pid", 7).Bind(stateDatabase, "at", DateTimeOffset.UtcNow)
                    .Bind(stateDatabase, "software", "0.9.0").Bind(stateDatabase, "modules", "serving=2;identity=1");
                insert.ExecuteNonQuery();
            }

            var again = JsonNode.Parse(await (await CallAsync(server, HttpMethod.Get, "/api/v1/state", ServerRole.Viewer)).Content.ReadAsStringAsync())!;
            var listed = again["instances"]!.AsArray();
            Assert.Equal(2, listed.Count);
            var older = listed.Single(i => (string?)i!["key"] == "older-instance")!;
            Assert.Equal("0.9.0", (string?)older["software_version"]);
            Assert.Equal(2, (int)older["module_versions"]!["serving"]!);
            Assert.False((bool)older["self"]!);

            if (engine == "sqlite") Assert.Equal("default", (string?)state["sources"]!["engine"]!["kind"]);
            else
            {
                Assert.Contains($"env:{StateVerbDatabases.PasswordVariable}", (string?)state["connection"]);   // as written, never what it resolves to
                Assert.DoesNotContain(databases.Password, text);
                Assert.Equal(engine == "mssql" ? "mssql" : "postgres", ((string?)state["engine"])!.ToLowerInvariant());
            }

            // a viewer reads; nobody without a role does
            Assert.Equal(HttpStatusCode.OK, (await CallAsync(server, HttpMethod.Get, "/api/v1/state/maintenance", ServerRole.Viewer)).StatusCode);
        }
        finally { Environment.SetEnvironmentVariable(StateVerbDatabases.PasswordVariable, null); }
    }

    [Fact]
    public async Task A_viewer_cannot_enable_maintenance_and_the_refusal_is_in_the_audit_with_who_asked()
    {
        await StartAsync();
        var server = await PairAsync();

        var refused = await CallAsync(server, HttpMethod.Post, "/api/v1/state/maintenance", ServerRole.Viewer, """{"enabled": true, "reason": "let me"}""");

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(server, HttpMethod.Get, "/api/v1/about", ServerRole.Viewer)).StatusCode);
        var entry = Assert.Single(Audited());
        Assert.Equal("refused", entry.Entry.Outcome);
        Assert.Equal("ada", entry.Entry.ActorId);
    }

    private IReadOnlyList<AuditRecord> Audited() =>
        new AuditLog(app.Services.GetRequiredService<StateDatabase>(), DbDataProxy.Server.State.ProxyState.ServiceId).Read(new AuditQuery(Verb: "state.maintenance"));

    [Fact]
    public async Task An_admin_puts_the_proxy_into_maintenance_and_ordinary_calls_get_503_while_the_state_and_the_admin_still_work_and_leaving_restores_service()
    {
        await StartAsync();
        var server = await PairAsync();

        var enabled = await CallAsync(server, HttpMethod.Post, "/api/v1/state/maintenance", ServerRole.Admin, """{"enabled": true, "reason": "moving the state to PostgreSQL"}""");
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        var status = await Json(enabled);
        Assert.True((bool)status["enabled"]!);
        Assert.True((bool)status["idle"]!);

        var refused = await CallAsync(server, HttpMethod.Get, "/api/v1/about", ServerRole.Viewer);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.NotNull(refused.Headers.RetryAfter);
        Assert.Equal("moving the state to PostgreSQL", (string?)(await Json(refused))["reason"]);
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(server, HttpMethod.Get, "/api/v1/state", ServerRole.Viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(server, HttpMethod.Get, "/api/v1/about", ServerRole.Admin)).StatusCode);

        var left = await CallAsync(server, HttpMethod.Post, "/api/v1/state/maintenance", ServerRole.Admin, """{"enabled": false}""");
        Assert.False((bool)(await Json(left))["enabled"]!);
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(server, HttpMethod.Get, "/api/v1/about", ServerRole.Viewer)).StatusCode);

        var audit = Audited();
        Assert.Equal(2, audit.Count);
        Assert.All(audit, r => { Assert.Equal("ok", r.Entry.Outcome); Assert.Equal("ada", r.Entry.ActorId); });
        Assert.Contains(audit, r => r.Entry.TargetId!.Contains("moving the state to PostgreSQL"));
        Assert.Contains(audit, r => r.Entry.TargetId == "disable");
    }
}
