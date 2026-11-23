using DbDataKit.Config.Git;
using DbDataSync.Core.Config;
using DbDataSync.Core.Secrets;
using LibGit2Sharp;

namespace DbDataSync.Core.Tests;

/// <summary>
/// CW01: the writer a pipeline gets when no writer is named. Behind an incremental reader it is <c>ChangeApply</c> (every engine), behind a reload reader
/// <c>DeleteInsert</c>; a writer that is named is never changed (an existing <c>MsSqlMerge</c> stays), and a Bulk Load of an unnamed writer reloads. The change of
/// default is in the release notes (<c>docs/release-notes/sync-change-apply.md</c>) and in Sync's "Staging and writers"; this pins it.
/// </summary>
public sealed class DefaultWriterTests : IDisposable
{
    private static readonly GitAuthor Author = new("Test", "test@example.com");

    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-default-writer-").FullName;
    private readonly ConfigRepository _config;

    public DefaultWriterTests()
    {
        Repository.Init(_root);
        _config = new ConfigRepository(
            Path.Combine(_root, "config"), new GitCommitService(_root),
            SecretStore.ForProviders([new InMemorySecretProvider()]));
    }

    public void Dispose() => GitTempDirectory.DeleteRecursively(_root);

    private string TaskFile => Path.Combine(_root, "config", "replications", "sales", "task.yaml");

    private void WriteTask(string reader, string? writer)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(TaskFile)!);
        File.WriteAllText(TaskFile, $"""
            schemaVersion: 2
            name: sales
            scheduling:
              mode: Continuous
              frequencySeconds: 30
            changeProcessing:
              reader:
                kind: {reader}
              cache:
                kind: StagingTable
            {(writer is null ? "" : $"  writer:\n    kind: {writer}\n")}
            """.Replace("\r", ""));
    }

    [Theory]
    [InlineData("MsSqlChangeTracking")]
    [InlineData("MsSqlCdc")]
    [InlineData("Watermark")]
    [InlineData("TriggerAudit")]
    [InlineData("PgLogicalSlot")]
    [InlineData("OracleFlashback")]
    public void AnIncrementalReader_WithNoWriterNamed_GetsTheChangeWriterOnEveryEngine(string reader)
    {
        Assert.Equal("ChangeApply", PipelineResolution.DefaultWriterKind(reader));

        WriteTask(reader, writer: null);
        var task = _config.LoadReplicationTask("sales");

        Assert.Equal("ChangeApply", task.ChangeProcessing.Writer.Kind);
        Assert.True(task.ChangeProcessing.WriterIsDefault);
        Assert.Equal("ChangeApply", PipelineResolution.WriterKind(null, task, mapping: null));
    }

    [Theory]
    [InlineData("BatchReload")]
    [InlineData("MsSqlBatchReload")]
    [InlineData("DuckDbQuery")]
    public void AReloadReader_WithNoWriterNamed_GetsDeleteInsert_AsTheScreenAlwaysDid(string reader)
    {
        WriteTask(reader, writer: null);
        Assert.Equal("DeleteInsert", _config.LoadReplicationTask("sales").ChangeProcessing.Writer.Kind);
    }

    [Theory]
    [InlineData("MsSqlMerge")]
    [InlineData("MsSqlMergeReconcile")]
    [InlineData("DeleteInsert")]
    [InlineData("ChangeApply")]
    [InlineData("Scd2")]
    public void ANamedWriter_IsNeverChanged_WhateverTheReader(string writer)
    {
        WriteTask("MsSqlChangeTracking", writer);
        var task = _config.LoadReplicationTask("sales");

        Assert.Equal(writer, task.ChangeProcessing.Writer.Kind);
        Assert.False(task.ChangeProcessing.WriterIsDefault);
        Assert.Equal(writer, PipelineResolution.Writer(task, mapping: null).Kind);
    }

    [Fact]
    public void AMappingThatOverridesTheReader_GetsTheDefaultForItsOwnReader_WhileTheWriterIsUnnamed()
    {
        WriteTask("BatchReload", writer: null);
        var task = _config.LoadReplicationTask("sales");
        var mapping = new TableMappingConfig
        {
            Name = "orders",
            Sources = [new SourceTableSpec { ConnectionName = "s", Database = "d", Schema = "dbo", Table = "Orders" }],
            Targets = [new TableSpec { ConnectionName = "t", Database = "d", Schema = "dbo", Table = "Orders" }],
            ReaderOverride = new ReaderConfig { Kind = "Watermark", Options = { ["watermarkColumn"] = "ModifiedAt" } },
        };

        Assert.Equal("DeleteInsert", PipelineResolution.Writer(task, mapping: null).Kind);
        Assert.Equal("ChangeApply", PipelineResolution.Writer(task, mapping).Kind);
        // ...and an override of the writer itself is the mapping's own word
        mapping.WriterOverride = new WriterConfig { Kind = "MsSqlMerge" };
        Assert.Equal("MsSqlMerge", PipelineResolution.Writer(task, mapping).Kind);
    }

    [Fact]
    public void ABulkLoad_OfAnUnnamedWriter_Reloads_BecauseTheChangeWriterIsForTheChangeFeed()
    {
        WriteTask("MsSqlChangeTracking", writer: null);
        var task = _config.LoadReplicationTask("sales");

        Assert.Equal("ChangeApply", PipelineResolution.Writer(task, mapping: null).Kind);
        Assert.Equal("DeleteInsert", PipelineResolution.BulkLoadWriter(task, mapping: null).Kind);
    }

    [Fact]
    public void ABulkLoad_OfANamedWriter_InheritsItAsBefore()
    {
        WriteTask("MsSqlChangeTracking", "MsSqlMerge");
        Assert.Equal("MsSqlMerge", PipelineResolution.BulkLoadWriter(_config.LoadReplicationTask("sales"), mapping: null).Kind);
    }

    [Fact]
    public void ADefaultedWriter_ChangeApply_PassesTheSaveTimeValidationOfAMapping()
    {
        WriteTask("Watermark", writer: null);
        var task = _config.LoadReplicationTask("sales");
        var mapping = new TableMappingConfig
        {
            Name = "orders",
            Sources = [new SourceTableSpec { ConnectionName = "s", Database = "d", Schema = "dbo", Table = "Orders" }],
            Targets = [new TableSpec { ConnectionName = "t", Database = "d", Schema = "dbo", Table = "Orders" }],
        };

        ConfigValidation.ValidateIncrementalReconcilePairing(
            mapping, PipelineResolution.Reader(task, mapping).Kind, PipelineResolution.Writer(task, mapping).Kind);
    }

    [Fact]
    public void SavingTheTask_NamesTheWriter_AndWritesNothingAboutHavingBeenTheDefault()
    {
        WriteTask("MsSqlChangeTracking", writer: null);
        _config.SaveReplicationTask(_config.LoadReplicationTask("sales"), Author);

        var yaml = File.ReadAllText(TaskFile);
        Assert.Contains("kind: ChangeApply", yaml);
        Assert.DoesNotContain("writerIsDefault", yaml, StringComparison.OrdinalIgnoreCase);
        Assert.False(_config.LoadReplicationTask("sales").ChangeProcessing.WriterIsDefault);
    }

    [Fact]
    public void TheRefusalMessage_NamesTheChangeWriter()
    {
        var message = ConfigValidation.IncrementalReconcileMessage("orders", "Watermark", "DeleteInsert");
        Assert.Contains("ChangeApply", message);
        Assert.Contains("MsSqlMerge", message);
    }
}
