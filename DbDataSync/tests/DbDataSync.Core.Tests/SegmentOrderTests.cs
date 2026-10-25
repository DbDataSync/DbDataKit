using DbDataSync.Core.Secrets;
using DbDataSync.Core.Config;
using DbDataKit.Config.Git;
using LibGit2Sharp;

namespace DbDataSync.Core.Tests;

/// <summary>The segment order setting (plan 13, M03): the request, the table mapping, the replication and the target connection each may set it, the most specific wins, and every level survives a save and a load.</summary>
public sealed class SegmentOrderTests : IDisposable
{
    private static readonly GitAuthor Author = new("Test", "test@example.com");

    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-segment-order-").FullName;
    private readonly ConfigRepository _config;

    public SegmentOrderTests()
    {
        Repository.Init(_root);
        _config = new ConfigRepository(
            Path.Combine(_root, "config"), new GitCommitService(_root),
            SecretStore.ForProviders([new InMemorySecretProvider()]));
    }

    public void Dispose() => GitTempDirectory.DeleteRecursively(_root);

    private static ConnectionConfig Connection(SegmentOrder? order) => new() { Name = "c", DriverType = "MsSql", AuthMode = AuthMode.IntegratedAuth, SegmentOrder = order };
    private static ReplicationTaskConfig Task(SegmentOrder? order) => new()
    {
        Name = "sales",
        Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 30 },
        ChangeProcessing = new ChangeProcessingConfig
        {
            Reader = new ReaderConfig { Kind = "MsSqlChangeTracking" },
            Cache = new CacheConfig { Kind = "MsSqlStagingTable" },
            Writer = new WriterConfig { Kind = "MsSqlMerge" },
        },
        Endpoints = new TaskEndpoints
        {
            Source = new EndpointRef { ConnectionName = "sales-src", Database = "App" },
            Target = new EndpointRef { ConnectionName = "sales-tgt", Database = "Dw" },
        },
        SegmentOrder = order,
    };
    private static TableMappingConfig Mapping(SegmentOrder? order) => new()
    {
        Name = "orders",
        Sources = [new SourceTableSpec { Table = "Orders" }],
        Targets = [new TableSpec { Table = "Orders" }],
        SegmentOrder = order,
    };

    [Fact]
    public void Nothing_set_anywhere_is_one_at_a_time_and_unbound()
    {
        Assert.Equal(SegmentOrder.OneAtATime, SegmentOrderResolution.Resolve(null, Connection(null), Task(null), Mapping(null)));
        Assert.Equal(SegmentOrder.OneAtATime, SegmentOrderResolution.Resolve(null, null, null, null));
        Assert.Equal(BindingLevel.Unbound, SegmentOrderResolution.LevelOf(Connection(null), Task(null), Mapping(null)));
    }

    [Fact]
    public void The_most_specific_level_that_says_something_wins()
    {
        var together = SegmentOrder.Together;
        var one = SegmentOrder.OneAtATime;

        Assert.Equal(together, SegmentOrderResolution.Resolve(null, Connection(together), Task(null), Mapping(null)));
        Assert.Equal(BindingLevel.Connection, SegmentOrderResolution.LevelOf(Connection(together), Task(null), Mapping(null)));

        Assert.Equal(one, SegmentOrderResolution.Resolve(null, Connection(together), Task(one), Mapping(null)));
        Assert.Equal(BindingLevel.Replication, SegmentOrderResolution.LevelOf(Connection(together), Task(one), Mapping(null)));

        Assert.Equal(together, SegmentOrderResolution.Resolve(null, Connection(one), Task(one), Mapping(together)));
        Assert.Equal(BindingLevel.Mapping, SegmentOrderResolution.LevelOf(Connection(one), Task(one), Mapping(together)));

        Assert.Equal(one, SegmentOrderResolution.Resolve(one, Connection(together), Task(together), Mapping(together)));
    }

    [Fact]
    public void Every_level_survives_a_save_and_a_load_even_when_it_is_the_default()
    {
        // OneAtATime is the enum's zero, so a non-nullable property would be omitted from the file and a stated choice lost (the phase 46 lesson).
        _config.SaveConnection(new ConnectionInput { Name = "c", DriverType = "MsSql", AuthMode = AuthMode.IntegratedAuth, Host = "h", SegmentOrder = SegmentOrder.OneAtATime }, Author);
        _config.SaveReplicationTask(Task(SegmentOrder.OneAtATime), Author);
        _config.SaveTableMapping("sales", Mapping(SegmentOrder.Together), Author);

        Assert.Equal(SegmentOrder.OneAtATime, _config.LoadConnection("c").SegmentOrder);
        Assert.Equal(SegmentOrder.OneAtATime, _config.LoadReplicationTask("sales").SegmentOrder);
        Assert.Equal(SegmentOrder.Together, _config.LoadTableMapping("sales", "orders").SegmentOrder);
    }

    [Fact]
    public void A_level_that_says_nothing_loads_as_nothing()
    {
        _config.SaveReplicationTask(Task(null), Author);
        _config.SaveTableMapping("sales", Mapping(null), Author);

        Assert.Null(_config.LoadReplicationTask("sales").SegmentOrder);
        Assert.Null(_config.LoadTableMapping("sales", "orders").SegmentOrder);
    }
}
