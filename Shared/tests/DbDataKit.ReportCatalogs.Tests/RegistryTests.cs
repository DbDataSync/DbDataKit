using DbDataKit.ReportCatalogs;
using DbDataKit.State;
using DbDataKit.State.Tests;
using DbDataKit.Views;
using DbDataKit.Views.Tests;

namespace DbDataKit.ReportCatalogs.Tests;

/// <summary>Plan 32 RC02: the sink over plan 30's registry (kind <c>report-catalog</c>), on the state database of every engine the suite has.</summary>
public sealed class RegistryTests : IDisposable
{
    private readonly StateEngines _engines = new();

    public void Dispose() => _engines.Dispose();

    private (DefinitionStoreSnapshotSink Sink, DefinitionStore Store, DefinitionPublisher Publisher) Open(string engine, string channel = "prod")
    {
        var database = _engines.Provision(engine).Open([DefinitionsModule.Module, PayloadStore.Module], new StateScope(Group: "main"));
        var kinds = ReportCatalogDefinitions.Register(new DefinitionKinds());
        var publisher = new DefinitionPublisher(database, kinds, new PayloadStore(database), "serving");
        var store = new DefinitionStore(database);
        return (new DefinitionStoreSnapshotSink(publisher, store, "report-catalog-reader", channel), store, publisher);
    }

    private static FakeCatalogProvider Fake(string name = "Quarterly") => new("fake",
        new ReportCatalogItem("f1", "/Sales", "Sales", ReportItemTypes.Folder, "fake"),
        new ReportCatalogItem("r1", "/Sales/Quarterly", name, ReportItemTypes.External, "fake", "Revenue"));

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task A_run_publishes_version_1_a_second_run_publishes_nothing_and_a_change_publishes_version_2(string engine)
    {
        var (sink, store, _) = Open(engine);
        Assert.Equal((ReportCatalogReadResult.Published, 1), Pair(await new ReportCatalogSnapshotJob(Fake(), sink).RunAsync()));
        Assert.Equal((ReportCatalogReadResult.Unchanged, 1), Pair(await new ReportCatalogSnapshotJob(Fake(), sink).RunAsync()));
        Assert.Equal((ReportCatalogReadResult.Published, 2), Pair(await new ReportCatalogSnapshotJob(Fake("Quarterly revenue"), sink).RunAsync()));
        Assert.Equal(2, store.Versions(ReportCatalogDefinitions.Kind, "fake").Count);
        Assert.Contains("Quarterly revenue", ReportCatalogSnapshot.Parse(store.Current(ReportCatalogDefinitions.Kind, "fake")!.Content).Items.Select(i => i.Name));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task A_failing_provider_leaves_the_served_version_and_a_channel_has_its_own_snapshot(string engine)
    {
        var (sink, store, publisher) = Open(engine);
        await new ReportCatalogSnapshotJob(Fake(), sink).RunAsync();
        var down = Fake();
        down.Fails = new ReportCatalogException("unavailable", "The service account cannot read the report catalog at https://reports.example.com/Reports (HTTP 403).");
        var failed = await new ReportCatalogSnapshotJob(down, sink).RunAsync();
        Assert.Equal(ReportCatalogReadResult.Failed, failed.Outcome);
        Assert.Equal(1, store.Current(ReportCatalogDefinitions.Kind, "fake")!.Version);

        var test = new DefinitionStoreSnapshotSink(publisher, store, "report-catalog-reader", "test");
        Assert.Equal((ReportCatalogReadResult.Published, 2), Pair(await new ReportCatalogSnapshotJob(Fake("Other"), test).RunAsync()));
        Assert.Equal(1, store.Current(ReportCatalogDefinitions.Kind, "fake", "prod")!.Version);
        Assert.Equal(2, store.Current(ReportCatalogDefinitions.Kind, "fake", "test")!.Version);
    }

    [Fact]
    public void The_kind_refuses_a_document_that_is_not_a_snapshot_of_its_own_name_or_of_another_number()
    {
        var (sink, _, publisher) = Open(StateEngineIds.Sqlite);
        var good = ReportCatalogSnapshot.Parse(new ReportCatalogSnapshot("fake", []).ToJson()).ToJson();
        Assert.Contains("named 'other'", Assert.Throws<PublishRefusedException>(() => publisher.Publish(new PublishRequest("report-catalog", "other", good, "x"))).Message);
        Assert.Throws<PublishRefusedException>(() => publisher.Publish(new PublishRequest("report-catalog", "fake", "{ \"schema_version\": 2, \"provider\": \"fake\", \"items\": [] }", "x")));
        Assert.Throws<PublishRefusedException>(() => publisher.Publish(new PublishRequest("report-catalog", "fake", "not json", "x")));
        Assert.NotNull(sink);
    }

    private static (string, int?) Pair(ReportCatalogReadResult r) => (r.Outcome, r.Version);
}
