using DbDataKit.ReportCatalogs;
using DbDataKit.Runner;
using DbDataKit.Runner.Execution;
using DbDataKit.State;
using DbDataKit.State.Tests;
using DbDataKit.Views;
using DbDataKit.Views.Tests;

namespace DbDataKit.ReportCatalogs.Tests;

/// <summary>Plan 32 RC02: the system package <c>report-catalogs</c> and its task <c>read</c>.</summary>
public sealed class CatalogReadTaskTests : IDisposable
{
    private readonly StateEngines _engines = new();

    public void Dispose() => _engines.Dispose();

    private static readonly TaskRef Read = new(CatalogReadTasks.Package, "prod", CatalogReadTasks.Task);

    private static TaskRunRequest Request(Dictionary<string, string>? inputs = null) => new()
    {
        Task = Read, PackageDirectory = "/tmp", PackageVersion = "1", Launch = new PackageLaunch(LaunchKind.Executable, Command: "dbdatakit"), Chain = Guid.NewGuid().ToString("N"), Inputs = inputs ?? [],
    };

    private static FakeCatalogProvider Fake(string name) => new(name, new ReportCatalogItem("r1", "/Quarterly", "Quarterly", ReportItemTypes.External, name, "Revenue"));

    private sealed class Rig : IDisposable
    {
        public StateDatabase Database { get; }
        public DefinitionStore Store { get; }
        public DefinitionStoreSnapshotSink Sink { get; }
        private readonly StateEngines _engines = new();

        public Rig(string engine)
        {
            Database = _engines.Provision(engine).Open([DefinitionsModule.Module, PayloadStore.Module, RunsModule.Module], new StateScope(Group: "main"));
            Store = new DefinitionStore(Database);
            var publisher = new DefinitionPublisher(Database, ReportCatalogDefinitions.Register(new DefinitionKinds()), new PayloadStore(Database), "serving");
            Sink = new DefinitionStoreSnapshotSink(publisher, Store, "report-catalog-reader");
        }

        public void Dispose() => _engines.Dispose();
    }

    [Fact]
    public void The_manifest_is_a_valid_package_with_one_scheduled_task_and_the_catalog_input()
    {
        var manifest = PackageManifestReader.Load(CatalogReadTasks.Manifest(new CatalogReadSettings(TimeSpan.FromMinutes(30)))).ThrowIfInvalid();
        Assert.Equal("report-catalogs", manifest.Name);
        var task = Assert.Single(manifest.Tasks);
        Assert.Equal(("read", TimeSpan.FromMinutes(30)), (task.Name, task.Schedule.Every));
        Assert.Equal(("catalog", false), (task.Inputs[0].Name, task.Inputs[0].Required));
        Assert.Equal(TimeSpan.FromMinutes(15), PackageManifestReader.Load(CatalogReadTasks.Manifest()).ThrowIfInvalid().Tasks[0].Schedule.Every);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task A_run_publishes_each_catalog_a_second_run_publishes_nothing_and_the_run_is_recorded(string engine)
    {
        using var rig = new Rig(engine);
        var tasks = new CatalogReadTasks(() => [new("fake", Fake("fake"), rig.Sink), new("other", Fake("other"), rig.Sink)], rig.Database);
        var first = await tasks.RunAsync(Request(), default);
        Assert.Equal((TaskEndState.Succeeded, 2), (first.End.State, first.End.Processed));
        Assert.Equal(1, rig.Store.Current(ReportCatalogDefinitions.Kind, "fake")!.Version);
        Assert.Equal(DbDataKit.Monitoring.RunOutcome.Succeeded, new RunStore(rig.Database, "runner").Get(first.RunId)!.Run.Outcome);
        await tasks.RunAsync(Request(), default);
        Assert.Equal(1, rig.Store.Current(ReportCatalogDefinitions.Kind, "fake")!.Version);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task A_failing_catalog_leaves_its_version_and_the_run_says_which_and_why(string engine)
    {
        using var rig = new Rig(engine);
        var down = Fake("down");
        var sources = new List<CatalogSource> { new("fake", Fake("fake"), rig.Sink), new("down", down, rig.Sink) };
        var tasks = new CatalogReadTasks(() => sources, rig.Database);
        Assert.Equal(TaskEndState.Succeeded, (await tasks.RunAsync(Request(), default)).End.State);
        down.Fails = new ReportCatalogException("unavailable", "The service account cannot read the report catalog at https://reports.example.com/Reports (HTTP 403).");
        var some = await tasks.RunAsync(Request(), default);
        Assert.Equal(TaskEndState.CompletedWithFailures, some.End.State);
        Assert.Contains("down: unavailable", some.End.FirstFailedItem, StringComparison.Ordinal);
        Assert.Contains("https://reports.example.com/Reports", some.End.FirstFailedItem, StringComparison.Ordinal);
        Assert.Equal(1, rig.Store.Current(ReportCatalogDefinitions.Kind, "down")!.Version);

        var all = await new CatalogReadTasks(() => [sources[1]], rig.Database).RunAsync(Request(), default);
        Assert.Equal(TaskEndState.Failed, all.End.State);
        Assert.Contains("last snapshots stay", all.End.Failure!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_input_reads_one_catalog_and_an_unknown_one_fails_the_run()
    {
        using var rig = new Rig(StateEngineIds.Sqlite);
        var tasks = new CatalogReadTasks(() => [new("fake", Fake("fake"), rig.Sink), new("other", Fake("other"), rig.Sink)], rig.Database);
        Assert.Equal(1, (await tasks.RunAsync(Request(new() { ["catalog"] = "other" }), default)).End.Processed);
        Assert.Null(rig.Store.Current(ReportCatalogDefinitions.Kind, "fake"));
        var unknown = await tasks.RunAsync(Request(new() { ["catalog"] = "nope" }), default);
        Assert.Equal(TaskEndState.Failed, unknown.End.State);
        Assert.Contains("'fake', 'other'", unknown.End.Failure!.Message, StringComparison.Ordinal);
    }
}
