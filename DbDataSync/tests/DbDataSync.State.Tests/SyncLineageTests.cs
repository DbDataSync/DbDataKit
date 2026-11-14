using DbDataKit.Connections;
using DbDataKit.Lineage;
using DbDataSync.Core.Config;
using DbDataSync.Core.Lineage;

namespace DbDataSync.State.Tests;

/// <summary>Sync's lineage (plan 18, G07): a replication's mapping is column edges from the source to the target, and the last run lies along them.</summary>
public sealed class SyncLineageTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("dbdatasync-lineage-tests-").FullName;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private static readonly IReadOnlyDictionary<string, ConnectionConfig> Connections = new Dictionary<string, ConnectionConfig>
    {
        ["crm"] = new() { Name = "crm", DriverType = DriverIds.MsSql, AuthMode = DbDataSync.Core.Config.AuthMode.IntegratedAuth, Host = "SQL01", Database = "Crm" },
        ["dwh"] = new() { Name = "dwh", DriverType = DriverIds.Postgres, AuthMode = DbDataSync.Core.Config.AuthMode.IntegratedAuth, Host = "pg01", Database = "dwh" },
    };

    private static ReplicationTaskConfig Task() => new()
    {
        Name = "crm-to-dwh",
        Scheduling = new SchedulingConfig { Mode = ScheduleMode.Periodic },
        ChangeProcessing = new ChangeProcessingConfig { Reader = new ReaderConfig { Kind = "BatchReload" }, Cache = new CacheConfig { Kind = "StagingTable" }, Writer = new WriterConfig { Kind = "DeleteInsert" } },
        Endpoints = new TaskEndpoints { Source = new EndpointRef { ConnectionName = "crm" }, Target = new EndpointRef { ConnectionName = "dwh" } },
    };

    private static TableMappingConfig Customers() => new()
    {
        Name = "customers",
        Sources = [new SourceTableSpec { Schema = "dbo", Table = "Customers" }],
        Targets = [new TableSpec { Schema = "public", Table = "customers" }],
        ColumnMappings =
        [
            new ColumnMapping { SourceColumn = "CustomerId", TargetColumn = "customer_id" },
            new ColumnMapping { SourceColumn = "Name", TargetColumn = "full_name", Transform = "UPPER({{column}})" },
        ],
    };

    private static TableMappingConfig FromQuery() => new()
    {
        Name = "recent_orders",
        Sources = [new SourceTableSpec { Table = "", Query = "SELECT * FROM dbo.Orders WHERE OrderedOn > '2026-01-01'" }],
        Targets = [new TableSpec { Schema = "public", Table = "recent_orders" }],
    };

    private static string Dataset(string connection, string schema, string table)
    {
        var (engine, _) = (Connections[connection].DriverType == DriverIds.MsSql ? "sqlserver" : "postgres", 0);
        var definition = new ConnectionDefinition { Name = connection, Engine = engine, Address = new AddressSpec { Host = Connections[connection].Host, Database = Connections[connection].Database } };
        return LineageIdentity.Dataset(definition, schema, table).ToString();
    }

    [Fact]
    public void A_mapping_is_a_replicates_edge_and_its_column_mappings_are_column_edges_with_their_transforms()
    {
        var graph = SyncLineage.Design([new SyncReplication(Task(), [Customers()])], Connections);

        var source = Dataset("crm", "dbo", "Customers");
        var target = Dataset("dwh", "public", "customers");
        Assert.Contains(graph.Edges, e => e is { Kind: EdgeKind.Replicates, Granularity: Granularity.Table, Confidence: Confidence.Declared } && e.From == source && e.To == target && e.Detail == "crm-to-dwh/customers");
        var columns = graph.Edges.Where(e => e.Granularity == Granularity.Column).ToList();
        Assert.Equal(2, columns.Count);
        Assert.Contains(columns, e => e.From.EndsWith("#customerid", StringComparison.OrdinalIgnoreCase) && e.To.EndsWith("#customer_id") && e.Detail is null);
        Assert.Contains(columns, e => e.To.EndsWith("#full_name") && e.Detail == "UPPER({{column}})");
        Assert.False(graph.Node(target)!.UnknownUpstream);                                  // Sync fills it from the source
        Assert.True(graph.Node(source)!.UnknownUpstream);                                   // what feeds the source is not Sync's to say
    }

    [Fact]
    public void A_source_that_is_a_query_is_a_node_of_its_own_with_an_unknown_upstream_and_a_missing_connection_is_said()
    {
        var notes = new List<string>();
        var graph = SyncLineage.Design([new SyncReplication(Task(), [FromQuery()])], Connections, notes);

        var query = graph.Nodes.Single(n => n.Id.StartsWith("sync-query://"));
        Assert.True(query.UnknownUpstream);
        Assert.Single(graph.Downstream(query.Id));
        Assert.Empty(notes);

        var lost = SyncLineage.Design([new SyncReplication(Task(), [Customers()])], new Dictionary<string, ConnectionConfig> { ["dwh"] = Connections["dwh"] }, notes);
        Assert.Empty(lost.Edges);
        Assert.Contains(notes, n => n.Contains("source connection `crm` is not configured"));
    }

    [Fact]
    public void The_newest_finished_run_of_each_mapping_lies_along_its_edges_with_the_rows_it_wrote()
    {
        var database = new StateDatabase(Path.Combine(_tempDir, "state.db"));
        var store = new TaskRunStore(database);
        var queue = new WorkQueueStore(database);
        var graph = SyncLineage.Design([new SyncReplication(Task(), [Customers(), FromQuery()])], Connections);

        var first = queue.Enqueue("crm-to-dwh", RunKind.Primary, "customers", segmentLabel: "a");
        store.BeginRun(first, pid: 1);
        store.CompleteRun(first, RunStatus.Succeeded, 40, 40, errorSummary: null);
        var second = queue.Enqueue("crm-to-dwh", RunKind.Primary, "customers", segmentLabel: "b");
        store.BeginRun(second, pid: 1);
        store.CompleteRun(second, RunStatus.Succeeded, 42, 42, errorSummary: null);
        var failed = queue.Enqueue("crm-to-dwh", RunKind.Primary, "recent_orders", segmentLabel: "c");
        store.BeginRun(failed, pid: 1);
        store.CompleteRun(failed, RunStatus.Failed, 0, 0, errorSummary: "the source refused");
        queue.Enqueue("crm-to-dwh", RunKind.Primary, "customers", segmentLabel: "d");          // queued, not finished

        var runs = SyncLineageRuns.Runs(graph, store);

        var customers = Assert.Single(runs, r => r.To == Dataset("dwh", "public", "customers"));
        Assert.Equal((DbDataKit.Lineage.RunOutcome.Succeeded, 42L), (customers.Outcome, customers.Rows));
        var orders = Assert.Single(runs, r => r.To == Dataset("dwh", "public", "recent_orders"));
        Assert.Equal(DbDataKit.Lineage.RunOutcome.Failed, orders.Outcome);
        Assert.Equal(2, runs.Count);
    }

    /// <summary>Plan 18 G07b and G16: Sync keeps its graph and the runs in the <c>lineage</c> module of its own state, and a run stays on its edge after the run history lost it.</summary>
    [Fact]
    public void The_graph_and_the_runs_are_kept_in_the_state_and_a_run_outlives_the_run_history_that_had_it_until_the_retention_of_run_detail()
    {
        Assert.Contains(DbDataKit.Lineage.LineageModule.Module, SyncState.Modules);
        var database = new StateDatabase(Path.Combine(_tempDir, "state.db"));
        var store = new TaskRunStore(database);
        var queue = new WorkQueueStore(database);
        var graph = SyncLineage.Design([new SyncReplication(Task(), [Customers()])], Connections);
        var run = queue.Enqueue("crm-to-dwh", RunKind.Primary, "customers");
        store.BeginRun(run, pid: 1);
        store.CompleteRun(run, RunStatus.Failed, 0, 0, errorSummary: "the source refused");

        var keeper = new DbDataKit.Lineage.LineageKeeper(new DbDataKit.Lineage.LineageStore(database, SyncState.ServiceId), "sync");
        var kept = keeper.Keep(graph, SyncLineageRuns.Runs(graph, store), TimeSpan.FromDays(SyncStateModule.RunDetailDays));
        Assert.Equal((true, 1, 0, 0), (kept.DesignChanged, kept.RunsAdded, kept.RunsPruned, kept.RunsLeftOut));

        // the run history is pruned (nothing is left of the run), and the next answer still has the run on its edge
        var again = keeper.Keep(graph, SyncLineageRuns.Runs(graph, new TaskRunStore(new StateDatabase(Path.Combine(_tempDir, "empty.db")))), TimeSpan.FromDays(SyncStateModule.RunDetailDays));
        Assert.Equal((false, 0), (again.DesignChanged, again.RunsAdded));
        var edge = Assert.Single(keeper.Read().Runs);
        Assert.Equal((DbDataKit.Lineage.RunOutcome.Failed, Dataset("dwh", "public", "customers")), (edge.Outcome, edge.To));
    }
    /// <summary>Plan 28c E22 P5: the Runner's passes (its pass records, as the run history reads them) are laid along the edges with the store's own runs, the newest finished one winning.</summary>
    [Fact]
    public void The_runners_passes_are_runs_of_the_edges_with_the_stores_own()
    {
        var store = new TaskRunStore(new StateDatabase(Path.Combine(_tempDir, "state.db")));
        var graph = SyncLineage.Design([new SyncReplication(Task(), [Customers()])], Connections);
        var ended = DateTimeOffset.UtcNow;
        var pass = new TaskRunRecord(Guid.NewGuid(), "crm-to-dwh", null, RunStatus.Succeeded, RunKind.Primary, "customers", null, ended.AddSeconds(-5), ended.AddSeconds(-5), ended.AddSeconds(-5), ended, 7, 7, null);
        var other = pass with { RunId = Guid.NewGuid(), TaskName = "elsewhere", RowsWritten = 99 };

        var edge = Assert.Single(SyncLineageRuns.Runs(graph, store, [pass, other]));

        Assert.Equal((DbDataKit.Lineage.RunOutcome.Succeeded, 7L, Dataset("dwh", "public", "customers")), (edge.Outcome, edge.Rows, edge.To));
    }

    /// <summary>Plan 24a UC13: the tables a pass reads and writes are the identities the graph names them by (the lines a drain reports as its run's lineage); a query source has no table and is left out.</summary>
    [Fact]
    public void The_tables_of_a_pass_are_the_graphs_identities_and_a_query_source_is_left_out()
    {
        var (reads, writes) = SyncLineage.TablesOf(Task(), Customers(), Connections);
        Assert.Equal([Dataset("crm", "dbo", "Customers")], reads);
        Assert.Equal([Dataset("dwh", "public", "customers")], writes);

        var (none, written) = SyncLineage.TablesOf(Task(), FromQuery(), Connections);
        Assert.Empty(none);
        Assert.Equal([Dataset("dwh", "public", "recent_orders")], written);
    }
}
