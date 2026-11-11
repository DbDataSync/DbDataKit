using System.Net;
using System.Text.Json.Nodes;
using DbDataConsole.Server;
using DbDataKit.Monitoring;
using DbDataKit.State;
using DbDataKit.Web.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DbDataConsole.Tests;

/// <summary>Plan 29a Q19: with the state on a server database the console reads every service's runs, audit entries and notifications from it; on its own SQLite file it says it is not shared.</summary>
public sealed class ConsoleActivityTests : IAsyncLifetime
{
    private readonly string directory = Directory.CreateTempSubdirectory("ddk-console-activity").FullName;
    private readonly string? server = Environment.GetEnvironmentVariable("DBDATAKIT_TEST_POSTGRES");
    private readonly string database = $"console_activity_{Guid.NewGuid():N}";
    private string? connection;

    public ValueTask InitializeAsync()
    {
        if (server is null) return ValueTask.CompletedTask;
        using var admin = new NpgsqlConnection(server);
        admin.Open();
        using var create = admin.CreateCommand();
        create.CommandText = $"CREATE DATABASE {database}";
        create.ExecuteNonQuery();
        connection = new NpgsqlConnectionStringBuilder(server) { Database = database }.ConnectionString;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        if (server is not null)
        {
            using var admin = new NpgsqlConnection(server);
            admin.Open();
            using var drop = admin.CreateCommand();
            drop.CommandText = $"DROP DATABASE IF EXISTS {database} WITH (FORCE)";
            drop.ExecuteNonQuery();
        }
        try { Directory.Delete(directory, true); } catch (IOException) { }
        return ValueTask.CompletedTask;
    }

    private async Task<(WebApplication App, HttpClient Http)> Start(StateSettings? state)
    {
        var app = ConsoleHost.Build(new ConsoleSettings(directory, new Uri("https://localhost:0"), State: state), ConsoleHost.Certificate(directory), _ => { }, webRoot: Path.Combine(directory, "none"));
        await app.StartAsync();
        var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(app.Urls.First()) };
        return (app, http);
    }

    private static async Task<(HttpStatusCode Status, JsonNode Body)> Get(WebApplication app, HttpClient http, string path, UserRole role)
    {
        var session = app.Services.GetRequiredService<ISessionStore>().Create(app.Services.GetRequiredService<IUserAccounts>().CreateUser("Ana-" + Guid.NewGuid().ToString("N")[..6], null, role).Id).Id;
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={session}");
        var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? new JsonObject() : JsonNode.Parse(text)!);
    }

    [Fact]
    public async Task On_its_own_sqlite_file_the_console_says_nothing_is_shared()
    {
        var (app, http) = await Start(null);
        using (http) await using (app)
        {
            foreach (var path in new[] { "/api/activity/runs", "/api/activity/audit", "/api/activity/notifications" })
            {
                var (status, body) = await Get(app, http, path, UserRole.Admin);
                Assert.Equal(HttpStatusCode.OK, status);
                Assert.False((bool)body["shared"]!);
            }
        }
    }

    [Fact]
    public async Task On_a_shared_server_database_one_query_returns_what_every_service_wrote()
    {
        if (connection is null) Assert.Skip("set DBDATAKIT_TEST_POSTGRES to run on PostgreSQL");
        var (app, http) = await Start(new StateSettings("postgres", connection));
        using (http) await using (app)
        {
            // two other services write to the same database, as they would in topology C
            var db = app.Services.GetRequiredService<StateDatabase>();
            var sync = new RunStore(db, "dbdatasync");
            var build = new RunStore(db, "dbdatabuild");
            var run = sync.Start("crm-to-dwh");
            sync.Finish(run, RunOutcome.Succeeded, rows: 12);
            build.Finish(build.Start("marts"), RunOutcome.Failed, failedItem: "dim_customer");
            // a drain of a replication the runner owns that failed on a pass (plan 28c E13): the item is the unit of the pass (the mapping and a claim), the kind the runner's, the message what Sync classified
            var drain = new RunStore(db, "runner.engine");
            drain.Finish(drain.Start("sales-set/prod/orders-set"), RunOutcome.Failed, failedItem: "orders#1a2b3c4d", failure: new RunFailure("Permanent", "The pass over 'orders' ended Failed (PositionExpired): the source discarded the history"));
            new AuditLog(db, "dbdatasync").Record(new AuditEvent("user", "ana", "sync.run.start"));
            new AuditLog(db, "dbdatabuild").Record(new AuditEvent("user", "bo", "build.run.start"));
            new NotificationFeedStore(db, "dbdatasync").Raise("failure", "sync failed");
            new NotificationFeedStore(db, "dbdatabuild").Raise("failure", "build failed");

            var runs = (await Get(app, http, "/api/activity/runs", UserRole.Viewer)).Body;
            Assert.True((bool)runs["shared"]!);
            Assert.Equal(["dbdatabuild", "dbdatasync", "runner.engine"], runs["runs"]!.AsArray().Select(r => (string)r!["service"]!).Order().ToArray());
            var failed = runs["runs"]!.AsArray().Single(r => (string)r!["service"]! == "runner.engine")!;
            Assert.Equal(("failed", "orders#1a2b3c4d", "Permanent"), (((string)failed["outcome"]!).ToLowerInvariant(), (string)failed["failedItem"]!, (string)failed["failureKind"]!));
            Assert.Contains("(PositionExpired)", (string)failed["failureMessage"]!);
            Assert.Null(runs["runs"]!.AsArray().Single(r => (string)r!["service"]! == "dbdatasync")!["failureKind"]);

            var onlySync = (await Get(app, http, "/api/activity/runs?service=dbdatasync", UserRole.Viewer)).Body;
            Assert.Equal("crm-to-dwh", (string)onlySync["runs"]![0]!["job"]!);
            Assert.Single(onlySync["runs"]!.AsArray());

            var audit = (await Get(app, http, "/api/activity/audit", UserRole.Admin)).Body;
            Assert.Equal(["dbdatabuild", "dbdatasync"], audit["entries"]!.AsArray().Select(e => (string)e!["service"]!).Order().ToArray());
            Assert.Equal(HttpStatusCode.Forbidden, (await Get(app, http, "/api/activity/audit", UserRole.Viewer)).Status);

            var notifications = (await Get(app, http, "/api/activity/notifications", UserRole.Viewer)).Body;
            Assert.Equal(2, notifications["notifications"]!.AsArray().Count);

            Assert.Equal(HttpStatusCode.BadRequest, (await Get(app, http, "/api/activity/runs?limit=0", UserRole.Viewer)).Status);
        }
    }
}
