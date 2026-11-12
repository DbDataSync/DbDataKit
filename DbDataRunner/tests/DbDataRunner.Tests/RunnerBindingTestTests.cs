using System.Net;
using DbDataKit.Connections.Inventory;
using DbDataKit.Runner;
using DbDataKit.Runner.Api;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataRunner.Tests;

/// <summary>UI08: <c>POST /api/v1/bindings/{channel}/integration/{name}/test</c> tries one slot's binding the way a run would and says how far it got, with a connection file of the runner's own directory as the fake connection. Never a value.</summary>
public sealed class RunnerBindingTestTests
{
    private const string Password = "binding-test-secret-3921";
    private const string Variable = "DBDATAKIT_TEST_UI08_PASSWORD";

    private static async Task Bind(RunnerHarness h, string connection)
    {
        h.Install(SamplePackage.Task("fails"));
        var service = new RunnerPublishing(h.State);
        service.Bind("sample", "dev", new Dictionary<string, string> { ["source"] = connection }, null, RunnerActor.Local());
        var files = service.Publisher.Artifacts(RunnerPublishing.Kind, "sample", 1).Select(a => new PublishFile(a.Path, a.Sha256, a.Size, a.MediaType)).ToList();
        var manifest = PackageManifestWriter.Write(SamplePackage.Manifest(SamplePackage.Task("ok") with { Connections = [new ConnectionSlot("source", "sqlserver")] }));
        service.Publish(new PublishInput("sample", manifest, files, "dev", PublishMode.Stage), RunnerActor.Local());
        await service.ActivateAsync("sample", "dev", 1, RunnerActor.Local(), new ActivateOptions(Anyway: true));
    }

    private static void WriteConnection(RunnerHarness h, string name)
    {
        Directory.CreateDirectory(Path.Combine(h.Directory, "connections"));
        File.WriteAllText(Path.Combine(h.Directory, "connections", name + ".yaml"),
            $"schema: connection/1\nname: {name}\nengine: sqlserver\nconnection_string: 'Server=fake;Database=d'\nauth: {{ mode: sql, user: u, password_secret: 'env:{Variable}' }}\n");
    }

    [Fact]
    public async Task The_test_says_how_far_a_binding_gets_a_connection_the_runner_lacks_a_secret_nobody_wrote_and_then_a_login_that_resolves_and_never_a_value()
    {
        Environment.SetEnvironmentVariable(Variable, null);
        try
        {
            await using var h = new RunnerHarness(StateEngineIds.Sqlite);
            await Bind(h, "erp");
            await h.StartAsync();
            const string path = "/api/v1/bindings/dev/integration/sample/test";
            var body = new { slot = "source" };

            var (_, lacking) = await h.CallAsync(HttpMethod.Post, path, ServerRole.Admin, body);
            Assert.False((bool)lacking!["ok"]!);
            Assert.Equal(("connection", "erp"), ((string)lacking["step"]!, (string)lacking["bound_to"]!));
            Assert.Contains("'erp'", (string)lacking["message"]!);

            WriteConnection(h, "erp");
            var (_, noSecret) = await h.CallAsync(HttpMethod.Post, path, ServerRole.Admin, body);
            Assert.False((bool)noSecret!["ok"]!);
            Assert.Equal("login", (string)noSecret["step"]!);
            Assert.Contains(Variable, (string)noSecret["message"]!);

            Environment.SetEnvironmentVariable(Variable, Password);
            var (status, fine) = await h.CallAsync(HttpMethod.Post, path, ServerRole.Admin, body);
            Assert.Equal(HttpStatusCode.OK, status);
            // the login resolves, and the connect step is tried: nothing listens at `fake`, so it says the class of the failure, in fixed words, and no value
            Assert.False((bool)fine!["ok"]!);
            Assert.Equal("connect", (string)fine["step"]!);
            Assert.False((bool)fine["connected"]!);
            Assert.DoesNotContain("fake", (string)fine["message"]!);
            Assert.DoesNotContain(Password, fine.ToJsonString());
            Assert.DoesNotContain(Password, string.Join('\n', h.Log));
            Assert.Equal(3, h.Audit.Read(new AuditQuery(VerbPrefix: RunnerBindingTestApi.Verb)).Count);
            Assert.DoesNotContain(h.Audit.Read(new AuditQuery(VerbPrefix: RunnerBindingTestApi.Verb)), e => (e.Entry.Detail ?? "").Contains(Password));
        }
        finally { Environment.SetEnvironmentVariable(Variable, null); }
    }

    [Fact]
    public async Task A_slot_that_is_not_bound_and_a_body_without_a_slot_are_answered_in_words_and_only_an_administrator_tries()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        await Bind(h, "erp");
        await h.StartAsync();
        const string path = "/api/v1/bindings/dev/integration/sample/test";

        var (_, unbound) = await h.CallAsync(HttpMethod.Post, path, ServerRole.Admin, new { slot = "elsewhere" });
        Assert.False((bool)unbound!["ok"]!);
        Assert.Equal("binding", (string)unbound["step"]!);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.CallAsync(HttpMethod.Post, path, ServerRole.Admin, new { })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.CallAsync(HttpMethod.Post, path, ServerRole.Operator, new { slot = "source" })).Status);
        Assert.Contains(h.Audit.Read(new AuditQuery(VerbPrefix: RunnerBindingTestApi.Verb)), e => e.Entry.Outcome == AuditEvent.Refused);
    }

    /// <summary>UI08, the connect step on a real SQL Server (<c>DBDATAKIT_TEST_SQLSERVER</c>): a right login opens a connection, a wrong password is the class "login rejected", and neither answer holds a value.</summary>
    [Fact]
    public async Task The_connect_step_opens_the_connection_on_SQL_Server_and_a_wrong_password_is_said_as_a_class_never_the_value()
    {
        var engine = Environment.GetEnvironmentVariable("DBDATAKIT_TEST_SQLSERVER");
        if (string.IsNullOrWhiteSpace(engine)) { Assert.Skip("set DBDATAKIT_TEST_SQLSERVER to run on SQL Server"); return; }
        var b = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(engine);
        const string wrong = "not-the-password-8841";
        try
        {
            await using var h = new RunnerHarness(StateEngineIds.Sqlite);
            await Bind(h, "erp");
            await h.StartAsync();
            Directory.CreateDirectory(Path.Combine(h.Directory, "connections"));
            File.WriteAllText(Path.Combine(h.Directory, "connections", "erp.yaml"),
                $"schema: connection/1\nname: erp\nengine: sqlserver\nconnection_string: 'Server={b.DataSource}'\ntls: {{ encrypt: false, trust_server_certificate: true }}\nauth: {{ mode: sql, user: {b.UserID}, password_secret: 'env:{Variable}' }}\n");
            const string path = "/api/v1/bindings/dev/integration/sample/test";

            Environment.SetEnvironmentVariable(Variable, b.Password);
            var (_, opened) = await h.CallAsync(HttpMethod.Post, path, ServerRole.Admin, new { slot = "source" });
            Assert.True((bool)opened!["ok"]!, opened.ToJsonString());
            Assert.Equal("connect", (string)opened["step"]!);
            Assert.True((bool)opened["connected"]!);
            Assert.DoesNotContain(b.Password, opened.ToJsonString());

            Environment.SetEnvironmentVariable(Variable, wrong);
            var (_, rejected) = await h.CallAsync(HttpMethod.Post, path, ServerRole.Admin, new { slot = "source" });
            Assert.False((bool)rejected!["ok"]!);
            Assert.Equal("connect", (string)rejected["step"]!);
            Assert.False((bool)rejected["connected"]!);
            Assert.Contains("login was rejected", (string)rejected["message"]!);
            Assert.DoesNotContain(wrong, rejected.ToJsonString());
            Assert.DoesNotContain(wrong, string.Join('\n', h.Log));
            Assert.DoesNotContain(h.Audit.Read(new AuditQuery(VerbPrefix: RunnerBindingTestApi.Verb)), e => (e.Entry.Detail ?? "").Contains(wrong) || (e.Entry.Detail ?? "").Contains(b.Password));
        }
        finally { Environment.SetEnvironmentVariable(Variable, null); }
    }
}
