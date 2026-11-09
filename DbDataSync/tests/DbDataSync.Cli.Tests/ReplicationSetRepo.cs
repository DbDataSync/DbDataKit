using DbDataKit.Config.Git;
using DbDataSync.Core.Config;
using DbDataSync.Core.Secrets;

namespace DbDataSync.Cli.Tests;

/// <summary>A Sync repository on disk with three replications of two connections and two mappings each (plan 28c E08): what `package publish` is run over. Every mapping has its column cache, unless a test says otherwise.</summary>
internal sealed class ReplicationSetRepo : IDisposable
{
    private static readonly GitAuthor Author = new("Test", "test@example.com");
    private readonly ConfigRepository _config;

    public ReplicationSetRepo(string prefix = "dbdatasync-set-")
    {
        Root = Directory.CreateTempSubdirectory(prefix).FullName;
        _config = new ConfigRepository(Path.Combine(Root, "config"), new GitCommitService(Root), SecretStore.ForProviders([new InMemorySecretProvider()]));
        _config.SaveConnection(new ConnectionInput { Name = "crm-src", DriverType = DriverIds.MsSql, Host = "src", Database = "App", AuthMode = AuthMode.SqlAuth, UserId = "u", Password = "super-secret-pw" }, Author);
        _config.SaveConnection(new ConnectionInput { Name = "dwh", DriverType = DriverIds.Postgres, Host = "dw", Database = "dw", AuthMode = AuthMode.SqlAuth, UserId = "u", Password = "other-secret-pw" }, Author);
        Replication("orders", ScheduleMode.Periodic, cron: "*/15 * * * *");
        Replication("customers", ScheduleMode.Periodic, cron: "0 2 * * *");
        Replication("events", ScheduleMode.Continuous, frequency: 30);
    }

    public string Root { get; }

    public void Dispose() => GitTempDirectory.DeleteRecursively(Root);

    public void Replication(string name, ScheduleMode mode, string? cron = null, int? frequency = null, bool cached = true, bool enabled = true, string? sourceTable = null)
    {
        _config.SaveReplicationTask(new ReplicationTaskConfig
        {
            Name = name,
            Enabled = enabled,
            Scheduling = new SchedulingConfig { Mode = mode, CronExpression = cron, FrequencySeconds = frequency, IdleTimeoutSeconds = frequency is null ? null : frequency * 4 },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = "MsSqlChangeTracking" },
                Cache = new CacheConfig { Kind = "MsSqlStagingTable" },
                Writer = new WriterConfig { Kind = "PostgresMerge" },
            },
        }, Author);
        for (var i = 1; i <= 2; i++)
            _config.SaveTableMapping(name, new TableMappingConfig
            {
                Name = $"t{i}",
                Sources = [new SourceTableSpec { ConnectionName = "crm-src", Database = "App", Schema = "dbo", Table = sourceTable is not null && i == 1 ? sourceTable : $"T{i}" }],
                Targets = [new TableSpec { ConnectionName = "dwh", Database = "dw", Schema = "public", Table = $"t{i}" }],
                SourceColumns = cached ? [new CachedColumn("id", "bigint", false, true, false, 1)] : [],
                TargetColumns = cached ? [new CachedColumn("id", "bigint", false, true, false, 1)] : [],
            }, Author);
    }
}
