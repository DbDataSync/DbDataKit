using System.Diagnostics;
using System.Text.Json.Nodes;
using DbDataKit.DataModel.Runner;
using DbDataKit.Runner;
using DbDataKit.Runner.Api;
using DbDataKit.Monitoring;
using DbDataKit.Scheduling;
using DbDataKit.State;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace DbDataKit.Server.Servers.Tests;

/// <summary>
/// Plan 33 SC24, first slice, with the real programs: a Store model's tables deployed by a runner with no Store server. The runtime package <c>dbdatastore-runtime</c> (the dbdatastore build output and the manifest of
/// <c>DbDataStore/scripts/runtime-manifest.sh</c>) and the model's definition package (<see cref="ModelDeployPackage"/>) are installed on a runner process, the slot <c>storage</c> is bound to a connection of the runner whose login
/// is in the runner's environment, and the manual task <c>deploy</c> is run: a dry run changes nothing, a deploy creates the model's tables, a second version of the model adds a column by an expand, and a version that would
/// drop a column is refused and changes nothing. SQL Server and PostgreSQL.
/// </summary>
public sealed class StoreDeployOnRunnerTests
{
    private const string Channel = "dev";
    private const string PasswordVariable = "DBDATAKIT_TEST_SC24_PASSWORD";

    public static TheoryData<string> Engines => ["mssql", "postgres"];

    private static string Model(string extra = "") => $$"""
        schemaVersion: 2
        name: sales
        storage: {connection: storedb}
        entities:
          territory:
            key: [code]
            attributes:
              code: {type: VARCHAR(10)}
              name: {type: VARCHAR(100)}{{extra}}
          region:
            key: [code]
            attributes:
              code: {type: VARCHAR(10)}
        """;

    private static string ConnectionFile(string engine, string connection)
    {
        if (engine == "mssql")
        {
            var b = new SqlConnectionStringBuilder(connection);
            return $"schema: connection/1\nname: storedb\nengine: sqlserver\nconnection_string: 'Server={b.DataSource};Database={b.InitialCatalog}'\ntls: {{ encrypt: false, trust_server_certificate: true }}\nauth: {{ mode: sql, user: {b.UserID}, password_secret: 'env:{PasswordVariable}' }}\n";
        }
        var p = new NpgsqlConnectionStringBuilder(connection);
        return $"schema: connection/1\nname: storedb\nengine: postgres\nconnection_string: 'Host={p.Host};Port={p.Port};Database={p.Database}'\ntls: {{ encrypt: false }}\nauth: {{ mode: sql, user: {p.Username}, password_secret: 'env:{PasswordVariable}' }}\n";
    }

    private static string PasswordOf(string engine, string connection) => engine == "mssql" ? new SqlConnectionStringBuilder(connection).Password : new NpgsqlConnectionStringBuilder(connection).Password!;

    /// <summary>The columns of the schema <c>sales</c>, as <c>table.column</c>.</summary>
    private static async Task<List<string>> ColumnsAsync(string engine, string connection)
    {
        await using var c = engine == "mssql" ? (System.Data.Common.DbConnection)new SqlConnection(connection) : new NpgsqlConnection(connection);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT table_name, column_name FROM information_schema.columns WHERE table_schema = 'sales' ORDER BY table_name, column_name";
        var found = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) found.Add($"{reader.GetString(0)}.{reader.GetString(1)}");
        return found;
    }

    private static async Task<string> ManifestOfRuntimeAsync()
    {
        var info = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(Path.Combine(Programs.RepositoryRoot, "DbDataStore", "scripts", "runtime-manifest.sh"));
        info.ArgumentList.Add(OperatingSystem.IsWindows() ? "win-x64" : "linux-x64");
        info.ArgumentList.Add("1.0.0");
        using var process = Process.Start(info)!;
        var text = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await process.StandardError.ReadToEndAsync());
        return text;
    }

    private static void Copy(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_models_tables_are_deployed_by_a_runner_task_dry_run_first_then_created_then_expanded_and_a_removal_is_the_contract_task_of_the_approved_plan(string engine)
    {
        if (OperatingSystem.IsWindows()) Assert.Skip("the runtime manifest is made by a bash script");
        var root = Path.Combine(Path.GetTempPath(), "ddk-e2e-sc24-" + Guid.NewGuid().ToString("N")[..8]);
        var (connection, drop) = await ConsoleJoinsRunnerGroupTests.ProvisionAsync(engine);
        RealProcess? server = null;
        try
        {
            var runner = Programs.Runner;
            var runnerDirectory = Path.Combine(root, "runner");
            Directory.CreateDirectory(Path.Combine(runnerDirectory, "connections"));
            File.WriteAllText(Path.Combine(runnerDirectory, "connections", "storedb.yaml"), ConnectionFile(engine, connection));

            // the runtime: the dbdatastore build output under bin/, the manifest the script writes
            var runtime = Path.Combine(root, "runtime");
            Copy(Programs.StoreBuildOutput, Path.Combine(runtime, "files", "bin"));
            File.WriteAllText(Path.Combine(runtime, "package.yaml"), await ManifestOfRuntimeAsync());
            await RealProcess.RunAsync(runner, "package", "install", Path.Combine(runtime, "package.yaml"), Path.Combine(runtime, "files"), "--channel", Channel, "--repo", runnerDirectory);

            // the model's package, and its storage slot bound to the runner's connection
            async Task InstallAsync(string modelText, string folder)
            {
                var problems = new List<string>();
                var built = ModelDeployPackage.Build(modelText, problems);
                Assert.True(built is not null, string.Join("\n", problems));
                var directory = Path.Combine(root, folder);
                ModelDeployPackage.Write(built!, directory);
                await RealProcess.RunAsync(runner, "package", "install", Path.Combine(directory, "package.yaml"), Path.Combine(directory, "files"), "--channel", Channel, "--repo", runnerDirectory);
            }

            // the binding first: a version whose write slot has no binding in the channel is not served
            var package = ModelDeployPackage.NameOf("sales");
            var state = StateDatabase.OpenModules(Path.Combine(runnerDirectory, "state.db"), RunnerNames.EngineModules, new StateScope(Group: "main", Service: RunnerNames.ServiceId));
            new RunnerPublishing(state).Bind(package, Channel, new Dictionary<string, string> { [ModelDeploySlots.StorageSlot] = "storedb" }, null, RunnerActor.Local());
            await InstallAsync(Model(), "v1");

            var password = PasswordOf(engine, connection);
            var url = new Uri($"https://127.0.0.1:{Programs.FreePort()}");
            server = RealProcess.Start(runner, ["server", "run", "--repo", runnerDirectory, "--listen", url.AbsoluteUri.TrimEnd('/')], environment: new Dictionary<string, string> { [PasswordVariable] = password });
            await server.WaitUntilAsync(new Uri(url, "/health"), TimeSpan.FromSeconds(90));
            var runs = new RunStore(state, RunnerNames.EngineService);
            var job = $"{package}/{Channel}/{ModelDeployPackage.DeployTask}";

            var contractJob = $"{package}/{Channel}/{ModelDeployPackage.ContractTask}";
            Task<RunOutcome> DeployAsync(params string[] inputs) => RunAsync(job, inputs);
            async Task<RunOutcome> RunAsync(string which, params string[] inputs)
            {
                var before = runs.Read(new RunQuery(RunnerNames.EngineService, which)).Count;
                await RealProcess.RunAsync(runner, ["tasks", "run", which, .. inputs, "--repo", runnerDirectory]);
                var until = DateTime.UtcNow.AddSeconds(120);
                while (DateTime.UtcNow < until)
                {
                    var all = runs.Read(new RunQuery(RunnerNames.EngineService, which));
                    if (all.Count > before && all[0].Run.Outcome is not RunOutcome.Running and var done) return done;
                    await Task.Delay(500);
                }
                Assert.Fail($"The {which} run did not end within 120 s.\n{server.Output}\n{Logs(which)}");
                return RunOutcome.Failed;
            }

            string Logs(string? which = null) => string.Join("\n", runs.Read(new RunQuery(RunnerNames.EngineService, which ?? job)).Take(1).SelectMany(r => runs.Logs(r.Id, limit: 80)).Select(l => $"[{l.Level}] {l.Message}"));
            // the newest report of the contract task, as the Runner's steps route serves it
            JsonNode ContractReport() => JsonNode.Parse(new StepStore(state, new ProgressKey(package, Channel, ModelDeployPackage.ContractTask)).Recent("contract-report", 1, null, null).Single().ResultJson!)!;

            // a dry run (the default) says what it would do and changes nothing
            Assert.True(await DeployAsync() == RunOutcome.Succeeded, Logs());
            Assert.Empty(await ColumnsAsync(engine, connection));

            // the deploy creates the model's tables
            Assert.True(await DeployAsync("dry_run=false") == RunOutcome.Succeeded, Logs());
            var created = await ColumnsAsync(engine, connection);
            Assert.Contains("territory.code", created);
            Assert.Contains("territory.name", created);
            Assert.Contains("region.code", created);

            // a second version with a new attribute: the deploy expands, adding the column
            await InstallAsync(Model("\n      manager: {type: VARCHAR(50)}"), "v2");
            Assert.True(await DeployAsync("dry_run=false") == RunOutcome.Succeeded, Logs());
            Assert.Contains("territory.manager", await ColumnsAsync(engine, connection));

            // a version without `name` is not removed by a deploy: it is noted and left (a contract step is planned on its own); the table keeps the column
            await InstallAsync(Model().Replace("\n      name: {type: VARCHAR(100)}", ""), "v3");
            Assert.True(await DeployAsync("dry_run=false") == RunOutcome.Succeeded, Logs());
            Assert.Contains("territory.name", await ColumnsAsync(engine, connection));

            // the preflight, read only: the deploy's dry run says nothing is missing and nothing stands in its way; the contract's plan is told, to be approved on its own
            var preflightJob = $"{package}/{Channel}/{DbDataKit.Runner.Preflight.TaskName}";
            Assert.True(await RunAsync(preflightJob) == RunOutcome.Succeeded, Logs(preflightJob));
            var parts = new StepStore(state, new ProgressKey(package, Channel, DbDataKit.Runner.Preflight.TaskName)).Recent(DbDataKit.Runner.Preflight.StepName, 10, null, null).Select(st => JsonNode.Parse(st.ResultJson!)!).ToList();
            Assert.Equal(["contract sales", "deploy sales"], parts.Select(p => (string?)p["part"]).Order(StringComparer.Ordinal));
            var findings = DbDataKit.Runner.Preflight.Findings(parts);
            Assert.DoesNotContain(findings, f => f.Severity == DbDataKit.Runner.Preflight.Error);
            Assert.Contains(findings, f => f.Message.StartsWith("The contract plan ", StringComparison.Ordinal) && f.Message.Contains("2 step(s)", StringComparison.Ordinal));
            Assert.Contains("territory.name", await ColumnsAsync(engine, connection));

            // the contract task: without an approval a plan (the column to drop, as the lineage graph names it, and the plan's hash), changing nothing
            Assert.True(await RunAsync(contractJob) == RunOutcome.Succeeded, Logs(contractJob));
            var plan = ContractReport();
            Assert.True((bool)plan["dryRun"]!);
            Assert.False((bool)plan["applied"]!);
            var hash = (string)plan["hash"]!;
            // v3 has neither `name` nor v2's `manager`: both are dropped, each named as the lineage graph names a column
            var dropped = plan["dropped"]!.AsArray().Select(d => ((string)d!["columnId"]!).ToLowerInvariant()).Order(StringComparer.Ordinal).ToList();
            Assert.Collection(dropped, d => Assert.EndsWith(".territory#manager", d), d => Assert.EndsWith(".territory#name", d));
            Assert.Contains("territory.name", await ColumnsAsync(engine, connection));
            // an approval of another plan fails the run and changes nothing
            Assert.True(await RunAsync(contractJob, "approve=" + new string('0', 64)) == RunOutcome.Failed);
            Assert.Contains("territory.name", await ColumnsAsync(engine, connection));
            // the approved plan runs: the column is dropped
            Assert.True(await RunAsync(contractJob, "approve=" + hash) == RunOutcome.Succeeded, Logs(contractJob));
            Assert.True((bool)ContractReport()["applied"]!);
            var contracted = await ColumnsAsync(engine, connection);
            Assert.DoesNotContain("territory.name", contracted);
            Assert.DoesNotContain("territory.manager", contracted);
            Assert.Contains("territory.code", contracted);

            // the login is in no log line of the runs or the runner
            Assert.DoesNotContain(password, server.Output);
            Assert.DoesNotContain(password, Logs());
            Assert.DoesNotContain(password, Logs(contractJob));
        }
        finally
        {
            if (server is not null) await server.DisposeAsync();
            await drop();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
