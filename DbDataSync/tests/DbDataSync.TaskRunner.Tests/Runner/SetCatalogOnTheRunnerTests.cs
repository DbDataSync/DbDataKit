using System.Text.Json.Nodes;
using DbDataKit.Runner;
using DbDataKit.Runner.Execution;
using DbDataSync.TaskRunner.Runner;
using Microsoft.Data.Sqlite;

namespace DbDataSync.TaskRunner.Tests.Runner;

/// <summary>
/// The set's own <c>discover</c> and <c>describe</c> started by a runner, as the console starts them for a connection whose driver is Sync's own: the engine's process reads the YAML-driver SQLite connection of
/// <see cref="SqliteYamlSet"/> and keeps its answer where the console reads the built-in package's, in a step named after the task and keyed by the run's id (a JSON payload for <c>discover</c>, a value for <c>describe</c>).
/// The runner's state is SQLite and the database is a file, so this needs no engine.
/// </summary>
public sealed class SetCatalogOnTheRunnerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-setcatalog-runner-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* a temp folder */ }
    }

    /// <summary>A package source of the engine's build output (<c>engine/</c>) and the repository (<c>repo/</c>), as a published version holds them.</summary>
    private string PackageSource()
    {
        var source = Path.Combine(_root, "source");
        SqliteYamlSet.Write(Path.Combine(source, "repo"));
        var engine = EngineBuildOutput();
        try { Directory.CreateSymbolicLink(Path.Combine(source, "engine"), engine); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            foreach (var file in Directory.EnumerateFiles(engine, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(source, "engine", Path.GetRelativePath(engine, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
        }

        File.WriteAllText(Path.Combine(source, "package.yaml"), PackageManifestWriter.Write(RunnerRig.EngineManifest()));
        return source;
    }

    private static string EngineBuildOutput()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var framework = new DirectoryInfo(AppContext.BaseDirectory).Name;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "DbDataSync.TaskRunner", "bin", configuration, framework);
            if (File.Exists(Path.Combine(candidate, "DbDataSync.TaskRunner.runtimeconfig.json"))) return candidate;
        }

        throw new InvalidOperationException("The engine's build output was not found above " + AppContext.BaseDirectory);
    }

    private static TaskRunRequest Request(TaskRunRequest drain, string task, Dictionary<string, string> inputs) =>
        drain with { Task = new TaskRef(RunnerRig.Package, RunnerRig.Channel, task), Inputs = inputs, RunId = Guid.NewGuid(), Chain = Guid.CreateVersion7().ToString() };

    private static string Describe(RunnerRig rig, TaskRunResult result) => $"{result.End.State} (exit {result.ExitCode}); log:\n{rig.LogText(result.RunId)}";

    [Fact]
    public async Task Discover_and_describe_run_in_the_engines_process_and_keep_their_answers_keyed_by_the_run()
    {
        var source = PackageSource();
        using var rig = await RunnerRig.StartAsync(DbDataKit.State.StateEngineIds.Sqlite, new Dictionary<string, string>());
        var drain = await rig.DrainRequestAsync(source, "unused");

        var discover = Request(drain, SetCatalogSteps.Discover, new() { ["connection"] = "lite", ["schemas"] = "", ["pattern"] = "", ["table"] = "", ["repo_dir"] = "repo" });
        var read = await rig.Executor.RunAsync(discover);
        Assert.True(read.End.State == TaskEndState.Succeeded && read.ExitCode == 0, Describe(rig, read));
        var step = Assert.Single(new DbDataKit.State.StepStore(rig.Database, new DbDataKit.State.ProgressKey(RunnerRig.Package, RunnerRig.Channel, SetCatalogSteps.Discover)).List(discover.Chain));
        Assert.Equal((SetCatalogSteps.Discover, read.RunId.ToString("D")), (step.Name, step.Key));
        JsonObject answer;
        using (var payload = new DbDataKit.State.PayloadStore(rig.Database).OpenRead(step.ResultPayload!.Value))
            answer = JsonNode.Parse(payload)!.AsObject();
        Assert.Equal("sqlite.generic", (string?)answer["engine"]);
        Assert.Null(answer["read"]);   // the console records which Runner and channel it asked
        Assert.Equal(["items", "notes"], answer["tables"]!.AsArray().Select(t => (string)t!["name"]!).Order());
        Assert.DoesNotContain("apple", answer.ToJsonString(), StringComparison.Ordinal);

        var describe = Request(drain, SetCatalogSteps.Describe, new() { ["connection"] = "lite", ["sql"] = "SELECT id, name FROM items", ["repo_dir"] = "repo" });
        var described = await rig.Executor.RunAsync(describe);
        Assert.True(described.End.State == TaskEndState.Succeeded && described.ExitCode == 0, Describe(rig, described));
        var value = Assert.Single(new DbDataKit.State.StepStore(rig.Database, new DbDataKit.State.ProgressKey(RunnerRig.Package, RunnerRig.Channel, SetCatalogSteps.Describe)).List(describe.Chain));
        Assert.Equal(described.RunId.ToString("D"), value.Key);
        var result = JsonNode.Parse(value.ResultJson!)!;
        Assert.Equal(["id", "name"], result["columns"]!.AsArray().Select(c => (string)c!["name"]!));
        Assert.Equal("WrappedZeroRowProbe", (string?)result["strategy"]);

        // a query that writes fails the run with the read-only policy's reason, and the table keeps its rows
        var writes = await rig.Executor.RunAsync(Request(drain, SetCatalogSteps.Describe, new() { ["connection"] = "lite", ["sql"] = "DELETE FROM items", ["repo_dir"] = "repo" }));
        Assert.Equal(TaskEndState.Failed, writes.End.State);
        Assert.Contains("read-only", rig.Run(writes.RunId).Run.Failure?.Message ?? "", StringComparison.Ordinal);
    }
}
