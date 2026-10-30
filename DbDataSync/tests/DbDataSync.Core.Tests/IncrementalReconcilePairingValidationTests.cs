using DbDataKit.Config.Git;
using DbDataSync.Core.Config;
using DbDataSync.Core.Secrets;
using LibGit2Sharp;

namespace DbDataSync.Core.Tests;

/// <summary>
/// A reconciling writer replaces everything in its scope with the rows a pass staged, which behind an
/// incremental reader are only what changed: an unsegmented Watermark pass that found nothing ran
/// <c>DELETE … WHERE 1 = 1</c>, inserted nothing, and reported success. Refused at save — see
/// <see cref="ConfigValidation.ValidateIncrementalReconcilePairing"/>. A mapping saved before this rule
/// still loads; <c>RunExecutorTests</c> covers it being refused when it runs.
/// </summary>
public sealed class IncrementalReconcilePairingValidationTests : IDisposable
{
    private static readonly GitAuthor Author = new("Test", "test@example.com");

    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-incremental-reconcile-").FullName;

    public void Dispose() => GitTempDirectory.DeleteRecursively(_root);

    private static TableMappingConfig Mapping() => new()
    {
        Name = "orders",
        Sources = [new SourceTableSpec { ConnectionName = "s", Database = "d", Schema = "dbo", Table = "Orders" }],
        Targets = [new TableSpec { ConnectionName = "t", Database = "d", Schema = "dbo", Table = "Orders" }],
    };

    [Theory]
    [InlineData("Watermark", "DeleteInsert")]
    [InlineData("Watermark", "MsSqlDeleteInsert")]
    [InlineData("MsSqlChangeTracking", "MsSqlMergeReconcile")]
    [InlineData("MsSqlCdc", "MsSqlDeleteInsert")]
    [InlineData("TriggerAudit", "DeleteInsert")]
    [InlineData("PgLogicalSlot", "DeleteInsert")]
    [InlineData("OracleFlashback", "DeleteInsert")]
    public void AnIncrementalReader_WithAReconcilingWriter_IsRejected(string readerKind, string writerKind)
    {
        var problem = Assert.Throws<ConfigValidationException>(
            () => ConfigValidation.ValidateIncrementalReconcilePairing(Mapping(), readerKind, writerKind));

        Assert.Contains("'orders'", problem.Message);
        Assert.Contains($"'{readerKind}'", problem.Message);
        Assert.Contains($"'{writerKind}'", problem.Message);
    }

    [Theory]
    [InlineData("Watermark", "MsSqlMerge")]
    [InlineData("MsSqlChangeTracking", "Scd2")]
    [InlineData("BatchReload", "DeleteInsert")]
    [InlineData("MsSqlBatchReload", "MsSqlMergeReconcile")]
    [InlineData("KeyReconcile", "KeyReconcileDelete")]
    public void AnyOtherPairing_IsNotThisChecksBusiness(string readerKind, string writerKind) =>
        ConfigValidation.ValidateIncrementalReconcilePairing(Mapping(), readerKind, writerKind);

    [Fact]
    public void SavingAMapping_ThatResolvesToTheRefusedPairing_IsRejected()
    {
        Repository.Init(_root);
        var config = new ConfigRepository(
            Path.Combine(_root, "config"), new GitCommitService(_root),
            SecretStore.ForProviders([new InMemorySecretProvider()]));
        config.SaveReplicationTask(new ReplicationTaskConfig
        {
            Name = "r",
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Periodic, CronExpression = "0 0 * * *" },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = "Watermark", Options = { ["watermarkColumn"] = "ModifiedAt" } },
                Cache = new CacheConfig { Kind = "MsSqlStagingTable" },
                Writer = new WriterConfig { Kind = "MsSqlDeleteInsert" },
            },
        }, Author);

        var mapping = Mapping();
        mapping.ColumnMappings = [new ColumnMapping { SourceColumn = "ModifiedAt", TargetColumn = "ModifiedAt" }];

        var problem = Assert.Throws<ConfigValidationException>(() => config.SaveTableMapping("r", mapping, Author));
        Assert.Contains("upsert writer", problem.Message);
    }
}
