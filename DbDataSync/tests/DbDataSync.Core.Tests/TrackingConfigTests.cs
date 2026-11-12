using DbDataSync.Core.Secrets;
using DbDataSync.Core.Config;
using DbDataKit.Config.Git;
using LibGit2Sharp;

namespace DbDataSync.Core.Tests;

/// <summary>Plan 28c E17: the <c>tracking:</c> section of a replication file, its words, its round trip and what it resolves to while no writer keeps the state on the target.</summary>
public sealed class TrackingConfigTests : IDisposable
{
    private static readonly GitAuthor Author = new("Test", "test@example.com");

    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-tracking-").FullName;
    private readonly ConfigRepository _config;

    public TrackingConfigTests()
    {
        Repository.Init(_root);
        _config = new ConfigRepository(Path.Combine(_root, "config"), new GitCommitService(_root), SecretStore.ForProviders([new InMemorySecretProvider()]));
    }

    public void Dispose() => GitTempDirectory.DeleteRecursively(_root);

    private string? TaskFile() => Directory.Exists(Path.Combine(_root, "config")) ? Directory.GetFiles(Path.Combine(_root, "config"), "task.yaml", SearchOption.AllDirectories).FirstOrDefault() : null;

    private ReplicationTaskConfig Task(TrackingConfig? tracking) => new()
    {
        Name = "r",
        Scheduling = new SchedulingConfig { Mode = ScheduleMode.Periodic },
        ChangeProcessing = new ChangeProcessingConfig
        {
            Reader = new ReaderConfig { Kind = "Watermark", Options = { ["watermarkColumn"] = "Id" } },
            Cache = new CacheConfig { Kind = "StagingTable" },
            Writer = new WriterConfig { Kind = "DeleteInsert" },
        },
        Tracking = tracking,
    };

    [Fact]
    public void AFileWithoutTheSectionLoadsAsTheDefault()
    {
        _config.SaveReplicationTask(Task(null), Author);
        var loaded = _config.LoadReplicationTask("r");
        Assert.Null(loaded.Tracking);
        Assert.DoesNotContain("tracking", File.ReadAllText(TaskFile()!));
    }

    [Fact]
    public void StateRoundTrips()
    {
        _config.SaveReplicationTask(Task(new TrackingConfig { Mode = "state" }), Author);
        var loaded = _config.LoadReplicationTask("r");
        Assert.Equal("state", loaded.Tracking?.Mode);
        Assert.Null(loaded.Tracking?.Primary);
    }

    [Theory]
    [InlineData("both", "target")]
    [InlineData("both", "state")]
    [InlineData("target", null)]
    public void ModesThatKeepTheStateOnTheTargetAreRefusedUntilAWriterDoesItAndTheMessageSaysSo(string mode, string? primary)
    {
        var ex = Assert.Throws<ConfigValidationException>(() => _config.SaveReplicationTask(Task(new TrackingConfig { Mode = mode, Primary = primary }), Author));
        Assert.Contains("no writer writes it there yet", ex.Message);
        Assert.Contains("'r'", ex.Message);
        Assert.Null(TaskFile());
    }

    [Theory]
    [InlineData(null, null, "needs a `mode`")]
    [InlineData("sometimes", null, "'sometimes' is not one of target, state, both")]
    [InlineData("both", null, "must say which one is the primary")]
    [InlineData("state", "target", "takes no primary")]
    [InlineData("both", "elsewhere", "'elsewhere' is not one of target, state")]
    public void TheShapeIsCheckedAndTheWrongWordIsNamed(string? mode, string? primary, string message)
    {
        var ex = Assert.Throws<ConfigValidationException>(() => TrackingResolution.ValidateShape(new TrackingConfig { Mode = mode, Primary = primary }, "r"));
        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void AMisspelledKeyInTheSectionIsRefusedNamingIt()
    {
        _config.SaveReplicationTask(Task(new TrackingConfig { Mode = "state" }), Author);
        var path = TaskFile()!;
        File.WriteAllText(path, File.ReadAllText(path).Replace("mode: state", "mod: state"));
        var ex = Assert.ThrowsAny<Exception>(() => _config.LoadReplicationTask("r"));
        Assert.Contains("mod", ex.Message);
    }

    [Theory]
    [InlineData(null, "MsSql", "both", "target", "state", true)]
    [InlineData("state", "MsSql", "state", null, "state", false)]
    [InlineData(null, "ArrowFlightSql", "both", "target", "state", true)]
    public void TheDefaultResolvesToStateWithTheReasonWhileNoTargetWriterIsBuilt(string? mode, string? driver, string requested, string? requestedPrimary, string effective, bool defaulted)
    {
        var resolved = TrackingResolution.Resolve(mode is null ? null : new TrackingConfig { Mode = mode }, driver);
        Assert.Equal(requested, resolved.RequestedMode);
        Assert.Equal(requestedPrimary, resolved.RequestedPrimary);
        Assert.Equal(effective, resolved.EffectiveMode);
        Assert.Null(resolved.EffectivePrimary);
        Assert.Equal(defaulted, resolved.Defaulted);
        if (defaulted) Assert.Contains("plan 28c E17", resolved.Reason);
    }

    [Fact]
    public void OnlyTheRelationalEnginesCouldHoldAStateTable()
    {
        Assert.Contains(DriverIds.MsSql, TrackingResolution.TargetsThatCanHoldState);
        Assert.Contains(DriverIds.Postgres, TrackingResolution.TargetsThatCanHoldState);
        Assert.DoesNotContain(DriverIds.DuckDb, TrackingResolution.TargetsThatCanHoldState);
    }
}
