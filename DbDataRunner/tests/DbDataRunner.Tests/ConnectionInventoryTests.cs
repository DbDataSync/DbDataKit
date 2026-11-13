using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Runner;
using DbDataKit.Runner.Api;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataRunner.Tests;

/// <summary>Plan 28c E23 and E27: the runner reports the connections its packages are bound to and the tasks, versions and instances that use them, and never a secret value.</summary>
public sealed class ConnectionInventoryTests
{
    private static string? Text(JsonNode? node, string key) => node?[key]?.GetValue<string>();

    /// <summary>Version 1 is installed (no slot); the slot <c>source</c> is bound (when asked) and version 2, which declares it, is published and activated.</summary>
    private static async Task Activate(RunnerHarness h, bool bind)
    {
        h.Install(SamplePackage.Task("fails"));
        var service = new RunnerPublishing(h.State);
        if (!bind) return;
        // a slot is bound once the package exists, and a version that declares it is accepted once it is bound
        service.Bind("sample", "dev", new Dictionary<string, string> { ["source"] = "env:DBDATAKIT_TEST_E23_ERP_PASSWORD" }, null, RunnerActor.Local());
        var files = service.Publisher.Artifacts(RunnerPublishing.Kind, "sample", 1).Select(a => new PublishFile(a.Path, a.Sha256, a.Size, a.MediaType)).ToList();
        var manifest = PackageManifestWriter.Write(SamplePackage.Manifest(SamplePackage.Task("ok") with { Connections = [new ConnectionSlot("source", "sqlserver")] }));
        service.Publish(new PublishInput("sample", manifest, files, "dev", PublishMode.Stage), RunnerActor.Local());
        await service.ActivateAsync("sample", "dev", 1, RunnerActor.Local(), new ActivateOptions(Anyway: true));
    }

    [Fact]
    public async Task A_bound_slot_is_reported_with_its_task_version_and_runner_instance_and_no_secret_value()
    {
        const string secretValue = "inventory-secret-value-7781";
        Environment.SetEnvironmentVariable("DBDATAKIT_TEST_E23_ERP_PASSWORD", secretValue);
        try
        {
            await using var h = new RunnerHarness(StateEngineIds.Sqlite);
            await Activate(h, bind: true);
            await h.StartAsync();

            var (status, body) = await h.GetAsync("/api/v1/connections");
            Assert.Equal(HttpStatusCode.OK, status);
            var connection = Assert.Single(body!["connections"]!.AsArray())!;
            Assert.Equal("env:DBDATAKIT_TEST_E23_ERP_PASSWORD", Text(connection, "name"));
            Assert.Equal("sqlserver", Text(connection, "kind"));
            Assert.Equal("reference", Text(connection, "source"));   // E27: bound straight to a secret reference, no definition
            Assert.Equal("env:DBDATAKIT_TEST_E23_ERP_PASSWORD", Text(connection, "secret_ref"));   // a reference, as bound
            var task = Assert.Single(connection["used_by"]!.AsArray(), u => Text(u, "process") == "task")!;
            Assert.Equal("sample/ok", Text(task, "name"));
            Assert.Equal("dev", Text(task, "channel"));
            Assert.Equal("run", Text(task, "role"));
            Assert.NotNull(Text(task, "version"));
            Assert.NotNull(Text(task, "group"));

            // an instance that reports the version active (the staging follower does this on a real runner) is listed as a user too
            new RunnerPublishing(h.State).Publisher.Status.Report("instance-7", RunnerPublishing.Kind, "sample", "dev", 2, InstanceStates.Active);
            var (_, withInstance) = await h.GetAsync("/api/v1/connections");
            Assert.Contains(withInstance!["connections"]![0]!["used_by"]!.AsArray(), u => Text(u, "process") == "runner" && Text(u, "instance") == "instance-7" && Text(u, "version") == "2");
            Assert.DoesNotContain(secretValue, withInstance.ToJsonString());
            Assert.DoesNotContain(secretValue, string.Join('\n', h.Log));
        }
        finally { Environment.SetEnvironmentVariable("DBDATAKIT_TEST_E23_ERP_PASSWORD", null); }
    }

    [Fact]
    public async Task A_package_without_bindings_reports_no_connection_and_a_caller_without_a_token_is_refused()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        await Activate(h, bind: false);
        await h.StartAsync();

        var (status, body) = await h.GetAsync("/api/v1/connections");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Empty(body!["connections"]!.AsArray());
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.CallAsync(HttpMethod.Get, "/api/v1/connections", ServerRole.Viewer, token: false)).Status);
    }

    private const string Variable = "DBDATAKIT_TEST_E27_ERP_PASSWORD";
    private const string Value = "e27-secret-value-90417";

    private static string Db(string name, string host, string database, string engine = "sqlserver") =>
        $"schema: connection/1\nname: {name}\nengine: {engine}\naddress: {{ host: {host}, port: 1433, database: {database} }}\nauth:\n  mode: sql\n  user: reader\n  password_secret: 'env:{Variable}'\n";

    private static JsonObject Entry(JsonNode? body, string name) => (JsonObject)body!["connections"]!.AsArray().Single(c => Text(c, "name") == name)!;

    [Fact]
    public async Task A_bound_name_shows_engine_host_and_database_from_its_copy_and_a_copy_nothing_is_bound_to_is_listed_unused_and_no_value_is_in_the_answer()
    {
        Environment.SetEnvironmentVariable(Variable, Value);
        try
        {
            await using var h = new RunnerHarness(StateEngineIds.Sqlite);
            await Activate(h, bind: false);
            var yaml = Db("erp", "sql01", "Erp");
            var copies = new ConnectionCopies(h.State);
            copies.Put("erp", "database", yaml, "console:admin");
            copies.Put("spare", "database", Db("spare", "sql02", "Spare", "postgres"), "console:admin");
            var service = new RunnerPublishing(h.State);
            service.Bind("sample", "dev", new Dictionary<string, string> { ["source"] = "erp" }, null, RunnerActor.Local());
            var files = service.Publisher.Artifacts(RunnerPublishing.Kind, "sample", 1).Select(a => new PublishFile(a.Path, a.Sha256, a.Size, a.MediaType)).ToList();
            service.Publish(new PublishInput("sample", PackageManifestWriter.Write(SamplePackage.Manifest(SamplePackage.Task("ok") with { Connections = [new ConnectionSlot("source", "sqlserver")] })), files, "dev", PublishMode.Stage), RunnerActor.Local());
            await service.ActivateAsync("sample", "dev", 1, RunnerActor.Local(), new ActivateOptions(Anyway: true));
            await h.StartAsync();

            var (status, body) = await h.GetAsync("/api/v1/connections");
            Assert.Equal(HttpStatusCode.OK, status);
            var erp = Entry(body, "erp");
            Assert.Equal("sqlserver", Text(erp, "engine"));
            Assert.Equal("sql01:1433", Text(erp, "host"));
            Assert.Equal("Erp", Text(erp, "database"));
            Assert.Equal("copy", Text(erp, "source"));
            Assert.Equal(ConnectionCopyVersion.Of(yaml), Text(erp, "version"));
            Assert.Equal($"env:{Variable}", Text(erp, "secret_ref"));   // the reference the copy names, never its value
            Assert.Contains(erp["used_by"]!.AsArray(), u => Text(u, "process") == "task" && Text(u, "name") == "sample/ok");

            var spare = Entry(body, "spare");
            Assert.Empty(spare["used_by"]!.AsArray());
            Assert.Equal("postgres", Text(spare, "engine"));
            Assert.Equal("copy", Text(spare, "source"));

            // sent again with another text, the version moves (the console reads that as stale)
            copies.Put("erp", "database", Db("erp", "sql09", "Erp"), "console:admin");
            Assert.NotEqual(Text(erp, "version"), Text(Entry((await h.GetAsync("/api/v1/connections")).Item2, "erp"), "version"));
            Assert.DoesNotContain(Value, body!.ToJsonString());
            Assert.DoesNotContain(Value, string.Join('\n', h.Log));
        }
        finally { Environment.SetEnvironmentVariable(Variable, null); }
    }

    [Fact]
    public async Task A_file_of_the_runners_own_answers_before_a_copy_is_listed_even_when_nothing_is_bound_and_a_name_with_nothing_behind_it_is_unknown()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        await Activate(h, bind: false);
        new ConnectionCopies(h.State).Put("erp", "database", Db("erp", "console-host", "Erp"), "console:admin");
        System.IO.Directory.CreateDirectory(Path.Combine(h.Directory, "connections"));
        File.WriteAllText(Path.Combine(h.Directory, "connections", "erp.yaml"), Db("erp", "own-host", "Own"));
        File.WriteAllText(Path.Combine(h.Directory, "connections", "orphan.yml"), Db("orphan", "o-host", "O"));
        File.WriteAllText(Path.Combine(h.Directory, "connections", "broken.yaml"), "schema: connection/1\nname: broken\nengine: sqlserver\npassword: typed-here\n");
        var service = new RunnerPublishing(h.State);
        service.Bind("sample", "dev", new Dictionary<string, string> { ["source"] = "nowhere" }, null, RunnerActor.Local());
        var files = service.Publisher.Artifacts(RunnerPublishing.Kind, "sample", 1).Select(a => new PublishFile(a.Path, a.Sha256, a.Size, a.MediaType)).ToList();
        service.Publish(new PublishInput("sample", PackageManifestWriter.Write(SamplePackage.Manifest(SamplePackage.Task("ok") with { Connections = [new ConnectionSlot("source", "sqlserver")] })), files, "dev", PublishMode.Stage), RunnerActor.Local());
        await service.ActivateAsync("sample", "dev", 1, RunnerActor.Local(), new ActivateOptions(Anyway: true));
        await h.StartAsync();

        var (_, body) = await h.GetAsync("/api/v1/connections");
        var erp = Entry(body, "erp");
        Assert.Equal("file", Text(erp, "source"));
        Assert.Equal("own-host:1433", Text(erp, "host"));
        var orphan = Entry(body, "orphan");
        Assert.Equal("file", Text(orphan, "source"));
        Assert.Empty(orphan["used_by"]!.AsArray());
        var nowhere = Entry(body, "nowhere");
        Assert.Equal("unknown", Text(nowhere, "source"));
        Assert.Null(Text(nowhere, "host"));
        Assert.Contains(nowhere["used_by"]!.AsArray(), u => Text(u, "name") == "sample/ok");
        // a file that does not load is named, not echoed
        Assert.Contains(body!["problems"]!.AsArray(), p => p!.GetValue<string>().Contains("broken", StringComparison.Ordinal));
        Assert.DoesNotContain("typed-here", body.ToJsonString());
    }
}
