using System.Text.Json.Nodes;
using DbDataKit.Monitoring;
using DbDataKit.Runner.Execution;
using DbDataKit.Runner.Scheduler;
using DbDataKit.Scheduling;
using DbDataKit.State;
using DbDataKit.State.Tests;
using static DbDataKit.Runner.Tests.SchedulerHarness;

namespace DbDataKit.Runner.Tests;

/// <summary>Plan 29a Q20 (28a R25): an operator rewinds a task's checkpoint from its history through <see cref="RunnerControl"/>, and the act is on the audit.</summary>
public sealed class CheckpointRewindTests : IDisposable
{
    private readonly StateEngines _engines = new();
    private readonly string _directory = Directory.CreateTempSubdirectory("dbdatakit-rewind-").FullName;

    public void Dispose()
    {
        _engines.Dispose();
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* a temp folder left behind is untidy and nothing else */ }
    }

    private static IReadOnlyDictionary<string, ProgressValue> Parts(long at) => new Dictionary<string, ProgressValue> { ["at"] = ProgressValue.OfInt(at) };

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_rewind_goes_back_before_a_change_as_a_new_version_and_is_audited_and_a_stale_one_is_refused(string engine)
    {
        var database = _engines.Provision(engine).Open([SchedulingModule.Module, RunnerModule.Module, RunsModule.Module, DefinitionsModule.Module, ProgressModule.Module, AuditModule.Module], StateScope.Standalone);
        var task = new TaskRef("pkg", "prod", "orders");
        var store = new ProgressStore(database, new ProgressKey("pkg", "prod", "orders"));
        Assert.True(store.Advance("orders", Parts(10), 0, actor: "run"));
        Assert.True(store.Advance("orders", Parts(20), 1, actor: "run"));
        Assert.True(store.Advance("orders", Parts(30), 2, actor: "run"));
        var second = store.History("orders").Single(c => c.New == "20");

        var control = new RunnerControl(database, new RunnerScheduler(database, new NoRun(), new ArtifactCache(Path.Combine(_directory, "cache"), new PayloadArtifactSource(new PayloadStore(database))),
            new InMemoryVersionSource(), new RunnerSchedulerOptions { Channels = ["prod"], Service = RunnerNames.EngineService }));
        var ada = new RunnerActor("u1", "Ada", "console", "c1");

        Assert.True(control.RewindCheckpoint(task, "orders", "", second.Id, ada));

        var now = store.GetCheckpoint("orders")!;
        Assert.Equal(10, now.Parts["at"].AsInt);       // back to before the change that made it 20
        Assert.Equal(4, now.Version);                  // a new version: the rewind itself can be undone
        Assert.Equal("Ada", now.UpdatedBy);
        Assert.Contains(store.History("orders"), c => c.Actor == "Ada" && c.New == "10");

        var entry = new AuditLog(database, RunnerNames.ServiceId).Read(new AuditQuery(VerbPrefix: RunnerControl.RewindVerb)).Single();
        Assert.Equal(("u1", "pkg/prod/orders", AuditEvent.Ok), (entry.Entry.ActorId, entry.Entry.TargetId, entry.Entry.Outcome));
        var detail = JsonNode.Parse(entry.Entry.Detail!)!;
        Assert.Equal(("orders", 3, 4), ((string)detail["checkpoint"]!, (int)detail["from_version"]!, (int)detail["to_version"]!));

        // a change that is not this checkpoint's (or a checkpoint that moved) is refused and on record as failed
        Assert.False(control.RewindCheckpoint(task, "orders", "", 999_999, ada));
        Assert.Equal(10, store.GetCheckpoint("orders")!.Parts["at"].AsInt);
        Assert.Equal(AuditEvent.Failed, new AuditLog(database, RunnerNames.ServiceId).Read(new AuditQuery(VerbPrefix: RunnerControl.RewindVerb)).First().Entry.Outcome);
    }

    private sealed class NoRun : ITaskRunner
    {
        public Task<TaskRunResult> RunAsync(TaskRunRequest request, CancellationToken ct = default) => throw new InvalidOperationException("not used");
    }
}
