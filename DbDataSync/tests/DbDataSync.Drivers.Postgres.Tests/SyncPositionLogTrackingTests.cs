using System.Data.Common;
using DbDataKit.State.Tracking;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.MsSql;
using DbDataSync.Tracking;
using SyncStateDatabase = DbDataSync.State.StateDatabase;

namespace DbDataSync.Drivers.Postgres.Tests;

/// <summary>
/// Plan 28c E17: the entries a target records in <c>dbdatasync.position_log</c> are copied into Sync's state by the shared follower, a target that parts from its copy (a restore) or from the state's position is a
/// persisted divergence an administrator resolves, and the table is pruned by the setting. On SQL Server and PostgreSQL, against the real table; the copy is a SQLite state database.
/// </summary>
[Trait("Category", "Integration")]
public abstract class SyncPositionLogTrackingTestsBase : IAsyncLifetime
{
    private readonly string _stateFile = Path.Combine(Path.GetTempPath(), $"syncposition-{Guid.NewGuid():N}.db");
    private SyncStateDatabase _state = null!;
    private DbConnection _connection = null!;
    private TargetPositionLog _entries = null!;

    protected abstract DbConnection OpenConnection();
    protected abstract PositionLogEngine Engine { get; }

    public async Task InitializeAsync()
    {
        _connection = await Task.Run(OpenConnection);
        _entries = new TargetPositionLog(Engine);
        await _entries.EnsureAsync(_connection, CancellationToken.None);
        _state = new SyncStateDatabase(_stateFile);
    }

    public async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_stateFile) + "*")) { try { File.Delete(file); } catch (IOException) { } }
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private SyncPositionLogFollower FollowerFor(string replication, TimeProvider? time = null, TrackingFollowerOptions? options = null)
    {
        var identity = SyncPositionLogStream.Identity(Engine, _connection.DataSource, _connection.Database, "target", replication);
        return new SyncPositionLogFollower(_state, _entries, _ => Task.FromResult(OpenConnection()), identity, replication, options, time);
    }

    private async Task<List<PositionLogDraft>> WriteEntriesAsync(string replication, int count, string mapping = "m", string key = "src/key", int from = 1)
    {
        var drafts = new List<PositionLogDraft>();
        for (var i = from; i < from + count; i++)
        {
            var draft = new PositionLogDraft(Guid.NewGuid(), replication, mapping, key, i == 1 ? null : $"pos-{i - 1}", $"pos-{i}", DateTimeOffset.UtcNow, i, Guid.NewGuid());
            await _entries.WriteAsync(_connection, null, draft, CancellationToken.None);
            drafts.Add(draft);
        }

        return drafts;
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task The_entries_of_a_replication_are_copied_into_the_state_and_a_second_run_copies_nothing()
    {
        var replication = $"rep-{Guid.NewGuid():N}";
        var written = await WriteEntriesAsync(replication, 7);
        await WriteEntriesAsync($"other-{Guid.NewGuid():N}", 3);   // another replication's entries are not this follower's

        var follower = FollowerFor(replication, options: new TrackingFollowerOptions { BatchSize = 3 });
        var first = await follower.CatchUpAsync(TimeSpan.FromSeconds(30));
        Assert.True(first.CaughtUp, first.Error);
        Assert.Equal(7, first.Copied);

        var store = new StateTrackingStore(_state);
        var copied = await store.ReadAfterAsync(follower.Identity, SyncPositionLogStream.Stream, null, 100, CancellationToken.None);
        Assert.Equal(written.Select(d => d.EntryId.ToString("D")), copied.Select(r => r.Key));
        Assert.Equal(copied.Select(r => r.Order).OrderBy(o => o), copied.Select(r => r.Order));
        Assert.Contains("\"watermark_after\":\"pos-7\"", copied[^1].Document);

        Assert.Equal(0, (await follower.CatchUpAsync(TimeSpan.FromSeconds(30))).Copied);
        await WriteEntriesAsync(replication, 2, from: 8);
        Assert.Equal(2, (await follower.CatchUpAsync(TimeSpan.FromSeconds(30))).Copied);
        Assert.Equal(9, store.Count(follower.Identity, SyncPositionLogStream.Name));
    }

    [Fact]
    public async Task A_target_restored_to_an_earlier_point_is_a_persisted_divergence_that_an_administrator_resolves()
    {
        var replication = $"rep-{Guid.NewGuid():N}";
        await WriteEntriesAsync(replication, 6);
        var follower = FollowerFor(replication);
        Assert.Equal(6, (await follower.CatchUpAsync(TimeSpan.FromSeconds(30))).Copied);

        // the "restore": the target loses its last three entries (a backup taken after the third)
        await ExecuteAsync($"DELETE FROM dbdatasync.position_log WHERE replication_name = '{replication}' AND watermark_after IN ('pos-4','pos-5','pos-6')");

        var diverged = await follower.CatchUpAsync(TimeSpan.FromSeconds(30));
        Assert.True(diverged.Diverged);
        Assert.NotNull(diverged.Divergence);
        Assert.Contains("restored", diverged.Divergence!.Reason);
        Assert.NotNull(diverged.Divergence.Common);   // the third entry is the last both sides hold

        // persisted: a second process opening the same state sees it, and the document says so
        {
            var other = new SyncStateDatabase(_stateFile);
            var listed = new StateTrackingDivergences(other).List().Single(d => d.Identity == follower.Identity);
            Assert.True(listed.IsOpen);
            var document = SyncTrackingDocument.Build(other, _ => ("both", "target"));
            var copy = document["copies"]!.AsArray().Single(c => (string?)c!["identity"] == follower.Identity)!;
            Assert.True((bool)copy["streams"]![0]!["diverged"]!);
            Assert.Equal("both", (string?)copy["mode"]);
            Assert.Single(document["divergences"]!.AsArray());
            Assert.NotNull((string?)document["divergences"]![0]!["common"]!["time"]);   // the point where the sides part, with when that entry was committed
        }

        // while it is open nothing is copied
        await WriteEntriesAsync(replication, 2, from: 4);
        Assert.Equal(0, (await follower.CatchUpAsync(TimeSpan.FromSeconds(30))).Copied);

        var result = await SyncTrackingDocument.ResolveAsync(_state, diverged.Divergence.Id, TrackingResolution.Archive, "ada", CancellationToken.None);
        Assert.Equal(SyncResolveOutcome.Resolved, result.Outcome);
        Assert.Equal("archived", result.Divergence!.Status);
        Assert.Equal("ada", result.Divergence.ResolvedBy);
        Assert.Equal(SyncResolveOutcome.AlreadyClosed, (await SyncTrackingDocument.ResolveAsync(_state, diverged.Divergence.Id, TrackingResolution.Archive, "ada", CancellationToken.None)).Outcome);

        var store = new StateTrackingStore(_state);
        Assert.Equal(3, store.ArchivedCount(follower.Identity, SyncPositionLogStream.Stream, $"divergence-{diverged.Divergence.Id}"));
        var after = await follower.CatchUpAsync(TimeSpan.FromSeconds(30));
        Assert.True(after.CaughtUp, after.Error);
        Assert.Equal(2, after.Copied);   // the entries the target holds now, from the common point on
        Assert.Equal(5, store.Count(follower.Identity, SyncPositionLogStream.Name));
    }

    [Fact]
    public async Task A_reset_resolution_deletes_the_copys_later_rows()
    {
        var replication = $"rep-{Guid.NewGuid():N}";
        await WriteEntriesAsync(replication, 4);
        var follower = FollowerFor(replication);
        await follower.CatchUpAsync(TimeSpan.FromSeconds(30));
        await ExecuteAsync($"DELETE FROM dbdatasync.position_log WHERE replication_name = '{replication}' AND watermark_after IN ('pos-3','pos-4')");
        var diverged = await follower.CatchUpAsync(TimeSpan.FromSeconds(30));
        Assert.True(diverged.Diverged);

        Assert.Equal(SyncResolveOutcome.Resolved, (await SyncTrackingDocument.ResolveAsync(_state, diverged.Divergence!.Id, TrackingResolution.ResetCopy, "ada", CancellationToken.None)).Outcome);
        var store = new StateTrackingStore(_state);
        Assert.Equal(0, store.ArchivedCount(follower.Identity, SyncPositionLogStream.Stream, $"divergence-{diverged.Divergence.Id}"));
        Assert.Equal(2, store.Count(follower.Identity, SyncPositionLogStream.Name));
    }

    [Fact]
    public async Task A_target_without_the_table_is_a_primary_with_no_rows()
    {
        // an engine whose table is gone is what a restore from before its creation looks like; the source answers empty rather than failing
        var replication = $"rep-{Guid.NewGuid():N}";
        var source = new SyncPositionLogSource(new TargetPositionLog(Engine), _ => Task.FromResult(OpenConnection()), "x", replication);
        Assert.Null(await source.HighWaterMarkAsync("x", SyncPositionLogStream.Stream, CancellationToken.None));
        Assert.Empty(await source.ReadAfterAsync("x", SyncPositionLogStream.Stream, null, 10, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.HighWaterMarkAsync("another identity", SyncPositionLogStream.Stream, CancellationToken.None));
    }

    [Fact]
    public async Task A_position_the_target_does_not_hold_is_marked_after_the_grace_and_not_before()
    {
        var replication = $"rep-{Guid.NewGuid():N}";
        await WriteEntriesAsync(replication, 5);
        var clock = new Clock(DateTimeOffset.UtcNow);
        var follower = FollowerFor(replication, clock);
        await follower.CatchUpAsync(TimeSpan.FromSeconds(30));

        // the state agrees with the target: nothing to say
        Assert.All(await follower.CheckPositionsAsync((_, _) => "pos-5"), f => Assert.Null(f.Reason));

        // the state holds a position that is not one of the target's entries, but the entry is a moment old: left alone
        Assert.All(await follower.CheckPositionsAsync((_, _) => "pos-from-another-life"), f => Assert.Null(f.Reason));
        Assert.Null(follower.Follower.Divergence);

        clock.Now = DateTimeOffset.UtcNow.AddMinutes(10);
        var parted = Assert.Single(await follower.CheckPositionsAsync((_, _) => "pos-from-another-life"));
        Assert.Contains("restored from a backup", parted.Reason);
        var open = follower.Follower.Divergence;
        Assert.NotNull(open);
        Assert.Null(open!.Common);
        Assert.NotNull(open.PrimaryHigh);
        Assert.Contains("pos-from-another-life", open.Reason);

        // the same finding again does not open a second divergence
        await follower.CheckPositionsAsync((_, _) => "pos-from-another-life");
        Assert.Single(new StateTrackingDivergences(_state).List(follower.Identity));
    }

    [Fact]
    public async Task A_target_ahead_of_the_state_names_the_entry_the_state_stands_at()
    {
        var replication = $"rep-{Guid.NewGuid():N}";
        await WriteEntriesAsync(replication, 5);
        var clock = new Clock(DateTimeOffset.UtcNow.AddMinutes(10));
        var follower = FollowerFor(replication, clock);
        await follower.CatchUpAsync(TimeSpan.FromSeconds(30));

        var finding = Assert.Single(await follower.CheckPositionsAsync((_, _) => "pos-3"));
        Assert.Contains("ahead of the state", finding.Reason);
        var open = follower.Follower.Divergence;
        Assert.NotNull(open!.Common);
        var commonRow = (await new StateTrackingStore(_state).ReadAfterAsync(follower.Identity, SyncPositionLogStream.Stream, new TrackingPosition(open.Common!.Order, ""), 1, CancellationToken.None))[0];
        Assert.True(commonRow.Document.Contains("\"watermark_after\":\"pos-3\""), commonRow.Document);

        // a state with no position at all (lost or reset) is a divergence of its own wording
        var other = $"rep-{Guid.NewGuid():N}";
        await WriteEntriesAsync(other, 2);
        var lost = FollowerFor(other, clock);
        var finding2 = Assert.Single(await lost.CheckPositionsAsync((_, _) => null));
        Assert.Contains("reset or lost", finding2.Reason);
    }

    [Fact]
    public async Task Retention_keeps_the_newest_entries_and_what_is_recent_and_always_the_newest_of_each_source()
    {
        var replication = $"rep-{Guid.NewGuid():N}";
        await WriteEntriesAsync(replication, 30);
        await WriteEntriesAsync(replication, 4, key: "src/other", from: 1);
        await WriteEntriesAsync(replication, 4, mapping: "second");

        // everything is minutes old: a week of retention removes nothing, however few entries are kept
        Assert.Equal(0, await _entries.PruneAsync(_connection, null, replication, "m", 5, 7, DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Equal(34, await CountAsync(replication, "m"));

        // a month on: only the newest 5 of the mapping remain (and the newest of its other source, which is among the newest 5 here or kept for being the newest of its source)
        var removed = await _entries.PruneAsync(_connection, null, replication, "m", 5, 7, DateTimeOffset.UtcNow.AddDays(30), CancellationToken.None);
        Assert.True(removed >= 26, $"removed {removed}");
        var left = await TailOfAsync(replication, "m");
        Assert.True(left.Count >= 5 && left.Count <= 6, $"left {left.Count}");
        Assert.Contains("pos-4", left.Where(u => u.WatermarkKey == "src/other").Select(u => u.WatermarkAfter));   // the newest entry of that source is never pruned
        Assert.Contains("pos-30", left.Select(u => u.WatermarkAfter));

        // another mapping of the same replication is untouched
        Assert.Equal(4, await CountAsync(replication, "second"));

        // inside a transaction that rolls back, the prune is undone
        await using (var transaction = await _connection.BeginTransactionAsync())
        {
            await _entries.PruneAsync(_connection, transaction, replication, "second", 1, 0, DateTimeOffset.UtcNow.AddDays(1), CancellationToken.None);
            await transaction.RollbackAsync();
        }

        Assert.Equal(4, await CountAsync(replication, "second"));
        Assert.Equal(1, await _entries.PruneAsync(_connection, null, replication, "second", 3, 0, DateTimeOffset.UtcNow.AddDays(1), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _entries.PruneAsync(_connection, null, replication, "second", 0, 0, DateTimeOffset.UtcNow, CancellationToken.None));
    }

    private async Task<int> CountAsync(string replication, string mapping)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM dbdatasync.position_log WHERE replication_name = '{replication}' AND mapping = '{mapping}'";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task<IReadOnlyList<PositionLogEntry>> TailOfAsync(string replication, string mapping) =>
        [.. (await _entries.TailAsync(_connection, replication, 1000, CancellationToken.None)).Where(u => u.Mapping == mapping)];
}

public sealed class SqlServerSyncPositionLogTrackingTests(MsSqlScratchDatabase db) : SyncPositionLogTrackingTestsBase, IClassFixture<MsSqlScratchDatabase>
{
    protected override DbConnection OpenConnection() => db.OpenConnection();
    protected override PositionLogEngine Engine => PositionLogEngine.SqlServer;
}

public sealed class PostgresSyncPositionLogTrackingTests(PostgresTestDatabase db) : SyncPositionLogTrackingTestsBase, IClassFixture<PostgresTestDatabase>
{
    protected override DbConnection OpenConnection() => db.OpenConnection();
    protected override PositionLogEngine Engine => PositionLogEngine.Postgres;
}
