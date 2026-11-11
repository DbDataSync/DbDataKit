using DbDataKit.State.Engines.Snapshots;

namespace DbDataKit.State.Tests;

/// <summary>Plan 29b U03: a scheduled snapshot is written, verified, pruned, and a restore from it holds the state, on SQLite, PostgreSQL and SQL Server.</summary>
public sealed class StateBackupJobTests : IDisposable
{
    private readonly ServiceScratch _scratch = new();
    private static readonly StateModule[] Modules = ServiceData.Modules;
    private static readonly StateScope Scope = StateScope.Standalone;

    public void Dispose() => _scratch.Dispose();

    private (StateEndpoint Endpoint, StateDatabase Database, Guid Payload, byte[] Bytes) Seed(string engine)
    {
        var endpoint = _scratch.Provision(engine);
        var database = StateOpener.Open(endpoint.Settings, endpoint.SqliteFile!, Modules, Scope, _scratch.Resolver);
        var (payload, bytes) = ServiceData.Fill(database);
        return (endpoint, database, payload, bytes);
    }

    private string BackupFolder() => Path.Combine(_scratch.Directory, "backups");

    [Theory]
    [MemberData(nameof(ServiceScratch.Engines), MemberType = typeof(ServiceScratch))]
    public async Task A_backup_is_written_verified_and_a_restore_from_it_holds_the_state(string engine)
    {
        var (_, database, payload, bytes) = Seed(engine);
        var job = new StateBackupJob(database, Modules, Scope, new StateBackupSettings(BackupFolder(), Keep: 3));

        var result = await job.RunAsync(CancellationToken.None);

        Assert.True(result.Verified);
        Assert.True(result.Rows > 0);
        Assert.Matches(@"state-\d{8}T\d{6}Z$", result.Directory);
        Assert.True(File.Exists(Path.Combine(result.Directory, "manifest.json")));
        Assert.Equal([result.Directory], StateBackupJob.List(BackupFolder()));
        Assert.Empty(Directory.GetDirectories(BackupFolder(), "*.partial"));

        // restore is `state import` into an empty database: the stores read what the source held, the payload bytes are the same
        var target = _scratch.Provision(engine);
        var imported = await new StateSnapshotService(_scratch.Resolver).ImportAsync(result.Directory, target, Modules, Scope, null, null, CancellationToken.None);
        Assert.Equal(result.Rows, imported.Import.Rows);
        var restored = StateOpener.Open(target.Settings, target.SqliteFile ?? "", Modules, Scope, _scratch.Resolver);
        Assert.Equal(2, new IdentityUserStore(restored).List().Count);
        using var stream = new PayloadStore(restored).OpenRead(payload);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        Assert.Equal(bytes, copy.ToArray());
    }

    [Fact]
    public async Task Only_the_newest_backups_stay_and_nothing_else_in_the_folder_is_touched()
    {
        var (_, database, _, _) = Seed("sqlite");
        var clock = new Clock(DateTimeOffset.Parse("2026-11-11T08:00:00Z"));
        Directory.CreateDirectory(BackupFolder());
        var mine = Directory.CreateDirectory(Path.Combine(BackupFolder(), "photos")).FullName;          // not a backup of this job
        File.WriteAllText(Path.Combine(mine, "keep.txt"), "x");
        var lookAlike = Directory.CreateDirectory(Path.Combine(BackupFolder(), "state-20200101T000000Z")).FullName;   // named like one, holds no manifest
        var job = new StateBackupJob(database, Modules, Scope, new StateBackupSettings(BackupFolder(), Keep: 2), clock);

        var first = await job.RunAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(24));
        var second = await job.RunAsync(CancellationToken.None);
        Assert.Empty(second.Pruned);
        clock.Advance(TimeSpan.FromHours(24));
        var third = await job.RunAsync(CancellationToken.None);

        Assert.Equal([first.Directory], third.Pruned);
        Assert.Equal([third.Directory, second.Directory], StateBackupJob.List(BackupFolder()));
        Assert.True(File.Exists(Path.Combine(mine, "keep.txt")));
        Assert.True(Directory.Exists(lookAlike));
    }

    [Fact]
    public async Task A_backup_that_fails_leaves_no_partial_folder_and_prunes_nothing()
    {
        var (_, database, _, _) = Seed("sqlite");
        var clock = new Clock(DateTimeOffset.Parse("2026-11-11T08:00:00Z"));
        var good = await new StateBackupJob(database, Modules, Scope, new StateBackupSettings(BackupFolder(), Keep: 1), clock).RunAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(1));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new StateBackupJob(database, Modules, Scope, new StateBackupSettings(BackupFolder(), Keep: 1), clock).RunAsync(cancelled.Token));

        Assert.Equal([good.Directory], StateBackupJob.List(BackupFolder()));
        Assert.Empty(Directory.GetDirectories(BackupFolder(), "*.partial"));
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }
}
