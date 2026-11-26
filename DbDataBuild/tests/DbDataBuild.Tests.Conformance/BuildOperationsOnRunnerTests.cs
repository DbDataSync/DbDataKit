using DbDataKit.Runner.Client;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>
/// (A part of the apply group.) Plan 28c E21, Build's half through a real runner process: `project publish --to runner --operations` publishes the project's person-driven writes to a real dbdatarunner (in process, its task process real,
/// the published tool as the runtime package, <see cref="BuildRuntimePackage"/>), the write login is the runner's own secret behind the channel's binding, and a run of `apply_plan` asked for on a person's behalf applies a plan that
/// person made: the tracking tables name the person as the runner recorded them, and nobody else. What the in-process proof (<c>ApplyOnRunnerConformanceTests</c>) does not reach is this process path: the package as published, the slot's
/// login resolved by the runner, the requester carried into the task process.
/// </summary>
public partial class ApplyConformanceTests
{
    /// <summary>The connection the runner keeps for the binding `wh`: the engine's logins are secrets of the runner's own store, sent to it below.</summary>
    private static string RunnerConnectionFile(string engine) =>
        $"schema: connection/1\nname: wh\nengine: {engine}\ntls:\n  encrypt: false\n  trust_server_certificate: true\n" +
        "read_login: { connection_string_secret: 'secret:dbdatakit:build:connection:wh:read' }\n" +
        "write_login: { connection_string_secret: 'secret:dbdatakit:build:connection:wh:write' }\n";

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task A_plan_a_person_made_is_applied_by_the_published_operations_package_on_a_real_runner_and_the_tracking_tables_name_that_person(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        var client = Directory.CreateTempSubdirectory("ddb-e21-client-").FullName;
        try
        {
            // the tests' engines have self-signed certificates, which the conformance run accepts for its own process (TestEngineTls); the task is a process of its own, so the project's connection file says so (as BuildOnRunner's copy does)
            run.Write($"connections/{name}.yaml", $"schema: connection/1\nname: {name}\nengine: {name}\ntls:\n  encrypt: false\n  trust_server_certificate: true\n");
            Ok(run.Cli("connection", "init", "--apply"), "init");
            Ok(run.Cli("project", "compile"), "compile");
            var plan = run.Cli("connection", "deploy", "--write-plan");
            Ok(plan, "plan");
            var planFile = run.PlanFile(plan.Out);
            var planId = Path.GetFileName(planFile).Replace(".plan.yml", "", StringComparison.Ordinal);

            await using var runner = await ProjectPublishOnRunnerTests.Runner.StartAsync(BuildRuntimePackage.Directory());
            Directory.CreateDirectory(Path.Combine(runner.Settings.Directory, "connections"));
            File.WriteAllText(Path.Combine(runner.Settings.Directory, "connections", "wh.yaml"), RunnerConnectionFile(name));
            // the operations package has a slot only for the connections the project uses, here the one it builds on, bound to the runner's `wh`; the command line writes the package and the client library the console's deploy uses
            // binds the slot and stages the version
            var package = Path.Combine(client, "package");
            var o = new StringWriter(); var e = new StringWriter();
            var exit = DbDataBuild.Cli.CliApp.Run(["project", "publish", "--to", "runner", "--operations", "--project", run.Dir, "--package", "orders-ops", "--out", package, "--format", "json"], o, e, environment: _ => null);
            Assert.True(exit == 0, o + "\n" + e);
            var link = new RunnerPublishClient(new RunnerClientStore(client), _ => null);
            await link.BindAsync(runner.Url, runner.Code(), "orders-ops", "dev", new Dictionary<string, string> { [name] = "wh" });
            var version = (await link.PublishAsync(runner.Url, null, Path.Combine(package, "package.yaml"), Path.Combine(package, "files"), "dev", stage: true, expectedVersion: null)).Version;
            await runner.WaitActiveAsync("orders-ops", "dev", version);

            // the logins are the runner's: sent to its store, never in the package or the request
            await link.SendSecretAsync(runner.Url, null, "dbdatakit:build:connection:wh:read", engine.ConnectionString);
            await link.SendSecretAsync(runner.Url, null, "dbdatakit:build:connection:wh:write", engine.ConnectionString);

            var inputs = new Dictionary<string, string> { ["connection"] = name, ["plan"] = planId, ["plan_text"] = File.ReadAllText(planFile) };
            var runId = await link.RunNowAsync(runner.Url, null, "orders-ops", "dev", "apply_plan", inputs, "Ada via Main console");
            Assert.NotNull(runId);
            ClientRun? done = null;
            for (var until = DateTime.UtcNow.AddMinutes(4); DateTime.UtcNow < until; await Task.Delay(300))
                if ((done = await link.RunAsync(runner.Url, null, Guid.Parse(runId))) is { Finished: not null }) break;
            Assert.NotNull(done?.Finished);
            var logs = await link.RunLogsAsync(runner.Url, null, Guid.Parse(runId)) ?? [];
            Assert.True(done!.Outcome == "succeeded", $"{done.Outcome}: {done.FailureMessage}\n{string.Join('\n', logs.Select(l => l.Message))}");

            // the person as the runner recorded them (the caller beside them), and nobody else
            var tracking = run.Q("dbdatabuild") + ".";
            var events = tracking + run.Q("migration_log");
            Assert.True(await CountAsync(run, events, run.Q("applied_by") + " LIKE 'Ada via Main console (sent by %'") > 0);
            Assert.Equal(0, await CountAsync(run, events, run.Q("applied_by") + " NOT LIKE 'Ada via Main console (sent by %'"));        }
        finally
        {
            if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true);
            try { Directory.Delete(client, true); } catch (IOException) { /* temp */ }
        }
    }
}
