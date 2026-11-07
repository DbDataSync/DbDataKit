using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Runner;
using DbDataKit.Runner.Api;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataRunner.Tests;

/// <summary>Plan 28c E23: the runner reports the connections its packages are bound to and the tasks, versions and instances that use them, and never a secret value.</summary>
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
        service.Bind("sample", "dev", new Dictionary<string, string> { ["source"] = "env:DBDATA_E23_ERP_PASSWORD" }, null, RunnerActor.Local());
        var files = service.Publisher.Artifacts(RunnerPublishing.Kind, "sample", 1).Select(a => new PublishFile(a.Path, a.Sha256, a.Size, a.MediaType)).ToList();
        var manifest = PackageManifestWriter.Write(SamplePackage.Manifest(SamplePackage.Task("ok") with { Connections = [new ConnectionSlot("source", "sqlserver")] }));
        service.Publish(new PublishInput("sample", manifest, files, "dev", PublishMode.Stage), RunnerActor.Local());
        await service.ActivateAsync("sample", "dev", 1, RunnerActor.Local(), new ActivateOptions(Anyway: true));
    }

    [Fact]
    public async Task A_bound_slot_is_reported_with_its_task_version_and_runner_instance_and_no_secret_value()
    {
        const string secretValue = "inventory-secret-value-7781";
        Environment.SetEnvironmentVariable("DBDATA_E23_ERP_PASSWORD", secretValue);
        try
        {
            await using var h = new RunnerHarness(StateEngineIds.Sqlite);
            await Activate(h, bind: true);
            await h.StartAsync();

            var (status, body) = await h.GetAsync("/api/v1/connections");
            Assert.Equal(HttpStatusCode.OK, status);
            var connection = Assert.Single(body!["connections"]!.AsArray())!;
            Assert.Equal("env:DBDATA_E23_ERP_PASSWORD", Text(connection, "name"));
            Assert.Equal("sqlserver", Text(connection, "kind"));
            Assert.Equal("env:DBDATA_E23_ERP_PASSWORD", Text(connection, "secret_ref"));   // a reference, as bound
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
        finally { Environment.SetEnvironmentVariable("DBDATA_E23_ERP_PASSWORD", null); }
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
}
