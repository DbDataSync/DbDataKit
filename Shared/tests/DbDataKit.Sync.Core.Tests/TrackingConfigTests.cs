using DbDataSync.Core.Secrets;
using DbDataSync.Core.Config;
using DbDataKit.Config.Git;
using LibGit2Sharp;

namespace DbDataSync.Core.Tests;

/// <summary>Plan 28c E17: the <c>tracking:</c> section of a replication file, its words, its round trip and what it resolves to per target engine.</summary>
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
    [InlineData("both", "target", "MySql", "use `mode: state`")]
    [InlineData("target", null, "Oracle", "no writer keeps the position log")]
    [InlineData("both", "state", "DuckDb", "cannot hold a state table")]
    [InlineData("target", null, "ArrowFlightSql", "cannot hold a state table")]
    public void ModesThatKeepTheStateOnTheTargetAreRefusedForAnEngineWithoutAWriterAndTheMessageSaysSo(string mode, string? primary, string engine, string why)
    {
        var ex = Assert.Throws<ConfigValidationException>(() => TrackingResolution.Validate(new TrackingConfig { Mode = mode, Primary = primary }, "r", engine));
        Assert.Contains(why, ex.Message);
        Assert.Contains("'r'", ex.Message);
    }

    [Theory]
    [InlineData("both", "target", "MsSql")]
    [InlineData("both", "state", "Postgres")]
    [InlineData("target", null, "MsSql")]
    [InlineData("target", null, null)]
    [InlineData("state", null, "MySql")]
    public void TheModesAreAcceptedWhereAWriterExistsOrTheEngineIsNotKnown(string mode, string? primary, string? engine)
    {
        TrackingResolution.Validate(new TrackingConfig { Mode = mode, Primary = primary }, "r", engine);
        _config.SaveReplicationTask(Task(new TrackingConfig { Mode = mode, Primary = primary }), Author);
        Assert.Equal(mode, _config.LoadReplicationTask("r").Tracking?.Mode);
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
    [InlineData("MsSql")]
    [InlineData("Postgres")]
    [InlineData("MySql")]
    [InlineData("ArrowFlightSql")]
    [InlineData(null)]
    public void NoSectionKeepsTheRunnersStateAndSaysSo(string? driver)
    {
        var resolved = TrackingResolution.Resolve(null, driver);
        Assert.Equal("state", resolved.EffectiveMode);
        Assert.Null(resolved.EffectivePrimary);
        Assert.True(resolved.Defaulted);
        Assert.Contains("No tracking section", resolved.Reason);
    }

    [Theory]
    [InlineData("both", "target", "MsSql", "both", "target")]
    [InlineData("both", "state", "Postgres", "both", "state")]
    [InlineData("target", null, "MsSql", "target", null)]
    [InlineData("both", "target", "MySql", "state", null)]
    [InlineData("target", null, "ArrowFlightSql", "state", null)]
    public void AnAskedForModeAppliesWhereAWriterExistsAndFallsBackToStateWithTheReasonElsewhere(string mode, string? primary, string driver, string effective, string? effectivePrimary)
    {
        var resolved = TrackingResolution.Resolve(new TrackingConfig { Mode = mode, Primary = primary }, driver);
        Assert.Equal(mode, resolved.RequestedMode);
        Assert.Equal(effective, resolved.EffectiveMode);
        Assert.Equal(effectivePrimary, resolved.EffectivePrimary);
        Assert.False(resolved.Defaulted);
        if (effective == "state") Assert.Contains("state is kept in the runner's state only", resolved.Reason);
    }

    [Fact]
    public void RetentionRoundTripsAndAnAbsentOneIsTheDefaultOf1000EntriesAnd7Days()
    {
        _config.SaveReplicationTask(Task(new TrackingConfig { Mode = "both", Primary = "target", Retention = new TrackingRetention { Entries = 50, Days = 2.5 } }), Author);
        var loaded = _config.LoadReplicationTask("r");
        Assert.Equal(50, loaded.Tracking!.Retention!.Entries);
        Assert.Equal(2.5, loaded.Tracking.Retention.Days);
        Assert.Contains("retention", File.ReadAllText(TaskFile()!));

        var defaults = TrackingResolution.RetentionOf(new TrackingConfig { Mode = "target" });
        Assert.Equal(1000, defaults.EffectiveEntries());
        Assert.Equal(7, defaults.EffectiveDays());
        Assert.Equal(1000, TrackingResolution.RetentionOf(null).EffectiveEntries());
        // one key given keeps the default of the other
        Assert.Equal(7, TrackingResolution.RetentionOf(new TrackingConfig { Mode = "target", Retention = new TrackingRetention { Entries = 5 } }).EffectiveDays());
    }

    [Fact]
    public void ARetentionWithoutATableOnTheTargetOrWithANonsenseNumberIsRefusedNamingTheKey()
    {
        var state = Assert.Throws<ConfigValidationException>(() => TrackingResolution.Validate(new TrackingConfig { Mode = "state", Retention = new TrackingRetention { Entries = 5 } }, "r"));
        Assert.Contains("tracking.retention", state.Message);
        var entries = Assert.Throws<ConfigValidationException>(() => TrackingResolution.Validate(new TrackingConfig { Mode = "target", Retention = new TrackingRetention { Entries = 0 } }, "r"));
        Assert.Contains("tracking.retention.entries", entries.Message);
        var days = Assert.Throws<ConfigValidationException>(() => TrackingResolution.Validate(new TrackingConfig { Mode = "both", Primary = "state", Retention = new TrackingRetention { Days = -1 } }, "r"));
        Assert.Contains("tracking.retention.days", days.Message);
        TrackingResolution.Validate(new TrackingConfig { Mode = "target", Retention = new TrackingRetention { Entries = 1, Days = 0 } }, "r", "MsSql");
    }

    [Fact]
    public void AMisspelledRetentionKeyIsRefusedByTheStrictLoader()
    {
        _config.SaveReplicationTask(Task(new TrackingConfig { Mode = "target", Retention = new TrackingRetention { Entries = 5 } }), Author);
        var path = TaskFile()!;
        var saved = File.ReadAllText(path);
        File.WriteAllText(path, saved.Replace("entries:", "entriez:", StringComparison.Ordinal));
        var ex = Assert.ThrowsAny<Exception>(() => _config.LoadReplicationTask("r"));
        Assert.Contains("entriez", ex.ToString());

        // plan 28c E17 (S8): the key was `units` before "unit" became the position log; a file that still says so is refused naming it, not read
        File.WriteAllText(path, saved.Replace("entries:", "units:", StringComparison.Ordinal));
        var old = Assert.ThrowsAny<Exception>(() => _config.LoadReplicationTask("r"));
        Assert.Contains("units", old.ToString());
    }

    [Fact]
    public void OnlyTheRelationalEnginesCouldHoldAStateTable()
    {
        Assert.Contains(DriverIds.MsSql, TrackingResolution.TargetsThatCanHoldState);
        Assert.Contains(DriverIds.Postgres, TrackingResolution.TargetsThatCanHoldState);
        Assert.DoesNotContain(DriverIds.DuckDb, TrackingResolution.TargetsThatCanHoldState);
    }
}
