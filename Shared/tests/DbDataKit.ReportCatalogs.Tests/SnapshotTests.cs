using DbDataKit.ReportCatalogs;
using DbDataKit.Views;
using DbDataKit.Views.Tests;

namespace DbDataKit.ReportCatalogs.Tests;

internal sealed class ListSink : IReportCatalogSnapshotSink
{
    public List<(int Version, string Json, string Hash)> Versions { get; } = [];

    public Task<StoredCatalogSnapshot?> LatestAsync(string provider, CancellationToken cancellationToken = default) =>
        Task.FromResult(Versions.Count == 0 ? null : (StoredCatalogSnapshot?)new StoredCatalogSnapshot(Versions[^1].Version, Versions[^1].Hash));

    public Task<int> PublishAsync(string provider, string json, string hash, CancellationToken cancellationToken = default)
    {
        Versions.Add((Versions.Count + 1, json, hash));
        return Task.FromResult(Versions.Count);
    }
}

public sealed class SnapshotTests
{
    private static FakeCatalogProvider Fake() => new("fake",
        new ReportCatalogItem("f1", "/Sales", "Sales", ReportItemTypes.Folder, "fake"),
        new ReportCatalogItem("r1", "/Sales/Quarterly", "Quarterly", ReportItemTypes.External, "fake", "Revenue"));

    [Fact]
    public async Task A_run_publishes_version_1_and_a_second_run_with_no_change_publishes_nothing()
    {
        var sink = new ListSink();
        var job = new ReportCatalogSnapshotJob(Fake(), sink);
        var first = await job.RunAsync();
        Assert.Equal((ReportCatalogReadResult.Published, 1), (first.Outcome, first.Version));
        var second = await job.RunAsync();
        Assert.Equal((ReportCatalogReadResult.Unchanged, 1), (second.Outcome, second.Version));
        Assert.Single(sink.Versions);
    }

    [Fact]
    public async Task A_changed_catalog_publishes_the_next_version()
    {
        var sink = new ListSink();
        await new ReportCatalogSnapshotJob(Fake(), sink).RunAsync();
        var changed = new FakeCatalogProvider("fake",
            new ReportCatalogItem("f1", "/Sales", "Sales", ReportItemTypes.Folder, "fake"),
            new ReportCatalogItem("r1", "/Sales/Quarterly", "Quarterly revenue", ReportItemTypes.External, "fake", "Revenue"));
        var result = await new ReportCatalogSnapshotJob(changed, sink).RunAsync();
        Assert.Equal((ReportCatalogReadResult.Published, 2), (result.Outcome, result.Version));
    }

    [Fact]
    public async Task A_failing_provider_leaves_the_version_unchanged_and_the_run_records_why()
    {
        var sink = new ListSink();
        await new ReportCatalogSnapshotJob(Fake(), sink).RunAsync();
        var down = Fake();
        down.Fails = new ReportCatalogException("unavailable", "The service account cannot read the report catalog at https://reports.example.com/Reports (HTTP 403).");
        var result = await new ReportCatalogSnapshotJob(down, sink).RunAsync();
        Assert.Equal(ReportCatalogReadResult.Failed, result.Outcome);
        Assert.Contains("https://reports.example.com/Reports", result.Reason, StringComparison.Ordinal);
        Assert.Single(sink.Versions);
    }

    [Fact]
    public async Task The_snapshot_lists_items_in_path_order_with_their_parameters_and_policies_and_reads_back()
    {
        var snapshot = await ReportCatalogReader.ReadAsync(Fake());
        Assert.Equal(["/Sales", "/Sales/Quarterly"], snapshot.Items.Select(i => i.Path));
        var report = snapshot.Items[1];
        Assert.Equal("Region", report.Parameters.Single().Name);
        Assert.Equal(["CORP\\Analysts"], report.Permissions.Principals);
        var back = ReportCatalogSnapshot.Parse(snapshot.ToJson());
        Assert.Equal(snapshot.Hash(), back.Hash());
        Assert.StartsWith("{\n  \"schema_version\": 1", snapshot.ToJson().Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void A_document_of_another_number_or_not_a_snapshot_is_refused_in_words()
    {
        Assert.Contains("format 1", Assert.Throws<FormatException>(() => ReportCatalogSnapshot.Parse("""{"schema_version":2,"provider":"x","items":[]}""")).Message, StringComparison.Ordinal);
        Assert.Throws<FormatException>(() => ReportCatalogSnapshot.Parse("[1"));
    }
}
