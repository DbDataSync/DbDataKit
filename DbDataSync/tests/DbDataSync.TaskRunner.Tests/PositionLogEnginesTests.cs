using System.Data.Common;
using DbDataSync.Drivers.Abstractions;
using Xunit;

namespace DbDataSync.TaskRunner.Tests;

/// <summary>
/// Plan 28c E17: <see cref="TargetPositionLog"/> on every engine a replication can keep its state on (SQL Server, PostgreSQL, MySQL, MariaDB, Oracle): the table is made once and again safely, a row written in a transaction
/// is there after the commit and not after a rollback, the newest entry of a mapping and source, the entries after a sequence, the tail, the position lookup, the newest entry of every source, the identity of an entry and its
/// times in UTC come back as written, and the prune keeps what it must. Needs the engines of <c>testenv.sh env dbdatasync</c> (an engine that is not up fails its row, it is not skipped).
/// </summary>
[Trait("Category", "Integration")]
public sealed class PositionLogEnginesTests
{
    [Theory]
    [InlineData("sqlserver")]
    [InlineData("postgres")]
    [InlineData("mysql")]
    [InlineData("mariadb")]
    [InlineData("oracle")]
    public async Task The_state_table_keeps_entries_in_order_atomically_with_its_transaction_and_prunes_what_is_old(string name)
    {
        var target = ChangeApplyTargets.Make(name);
        await target.PrepareAsync();
        var state = new TargetPositionLog(TargetPositionLog.EngineOf(target.DriverType)!.Value);
        await using var connection = target.Open();

        await state.EnsureAsync(connection, CancellationToken.None);
        await state.EnsureAsync(connection, CancellationToken.None);   // a second pass finds it there
        Assert.True(await state.ExistsAsync(connection, CancellationToken.None));

        var replication = $"rep-{Guid.NewGuid():N}";
        Assert.False(await state.HoldsAnyAsync(connection, replication, CancellationToken.None));
        Assert.Null(await state.HighAsync(connection, replication, CancellationToken.None));

        var drafts = new List<PositionLogDraft>();
        for (var i = 1; i <= 5; i++)
        {
            var draft = new PositionLogDraft(Guid.NewGuid(), replication, "m", "src/k", i == 1 ? null : $"pos-{i - 1}", $"pos-{i}", i == 5 ? null : DateTimeOffset.UtcNow.AddSeconds(-i), i * 10, i % 2 == 0 ? Guid.NewGuid() : null);
            await state.WriteAsync(connection, null, draft, CancellationToken.None);
            drafts.Add(draft);
        }

        var other = new PositionLogDraft(Guid.NewGuid(), replication, "m2", "src/k2", null, "other-1", null, 1, null);
        await state.WriteAsync(connection, null, other, CancellationToken.None);
        // another source of the same mapping is another key
        var upper = new PositionLogDraft(Guid.NewGuid(), replication, "m", "src/other", null, "upper-1", null, 1, null);
        await state.WriteAsync(connection, null, upper, CancellationToken.None);

        // a transaction: its entry is there after the commit and not after a rollback
        var rolledBack = new PositionLogDraft(Guid.NewGuid(), replication, "m", "src/k", "pos-5", "pos-rollback", null, 1, null);
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await state.WriteAsync(connection, transaction, rolledBack, CancellationToken.None);
            await transaction.RollbackAsync();
        }

        var committed = new PositionLogDraft(Guid.NewGuid(), replication, "m", "src/k", "pos-5", "pos-6", null, 1, null);
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await state.WriteAsync(connection, transaction, committed, CancellationToken.None);
            await transaction.CommitAsync();
        }

        var latest = await state.LatestAsync(connection, replication, "m", "src/k", CancellationToken.None);
        Assert.Equal("pos-6", latest!.WatermarkAfter);
        Assert.Equal("pos-5", latest.WatermarkBefore);
        Assert.Equal(committed.EntryId, latest.EntryId);
        Assert.Equal("upper-1", (await state.LatestAsync(connection, replication, "m", "src/other", CancellationToken.None))!.WatermarkAfter);
        Assert.Null(await state.FindByPositionAsync(connection, replication, "m", "src/k", "pos-rollback", CancellationToken.None));

        // what an entry carries comes back as written: the id, the nulls, the time in UTC, the engine's own time of commit in UTC
        var first = (await state.ReadAfterAsync(connection, replication, 0, 100, CancellationToken.None))[0];
        Assert.Equal(drafts[0].EntryId, first.EntryId);
        Assert.Null(first.WatermarkBefore);
        Assert.Null(first.RunId);
        Assert.InRange((first.WatermarkTime!.Value - drafts[0].WatermarkTime!.Value).Duration(), TimeSpan.Zero, TimeSpan.FromSeconds(1));
        Assert.Equal(10, first.RowsStaged);
        Assert.InRange((DateTimeOffset.UtcNow - first.CommittedAt).Duration(), TimeSpan.Zero, TimeSpan.FromMinutes(5));
        var second = (await state.ReadAfterAsync(connection, replication, first.Sequence, 100, CancellationToken.None))[0];
        Assert.Equal(drafts[1].RunId, second.RunId);

        // order, the limit, the tail and the high-water mark
        var all = await state.ReadAfterAsync(connection, replication, 0, 100, CancellationToken.None);
        Assert.Equal(8, all.Count);   // five, the other mapping's, the other source's and the committed one
        Assert.Equal(all.Select(u => u.Sequence).Order(), all.Select(u => u.Sequence));
        Assert.Equal(3, (await state.ReadAfterAsync(connection, replication, 0, 3, CancellationToken.None)).Count);
        var tail = await state.TailAsync(connection, replication, 3, CancellationToken.None);
        Assert.Equal(all.TakeLast(3).Select(u => u.Sequence), tail.Select(u => u.Sequence));
        Assert.Equal(all[^1].Sequence, (await state.HighAsync(connection, replication, CancellationToken.None))!.Sequence);
        Assert.True(await state.HoldsAnyAsync(connection, replication, CancellationToken.None));
        Assert.Equal(3, (await state.LatestPerMappingAsync(connection, replication, CancellationToken.None)).Count);   // (m, src/k), (m, src/other), (m2, src/k2)

        // the position lookup finds the entry that ended there
        var found = await state.FindByPositionAsync(connection, replication, "m", "src/k", "pos-3", CancellationToken.None);
        Assert.Equal(drafts[2].EntryId, found!.EntryId);

        // the prune keeps the newest two entries of the mapping and the newest of each source whatever their age, and touches no other mapping
        var removed = await ((Func<Task<int>>)(async () =>
        {
            await using var transaction = await connection.BeginTransactionAsync();
            var count = await state.PruneAsync(connection, transaction, replication, "m", keepEntries: 2, keepDays: 0, DateTimeOffset.UtcNow.AddMinutes(10), CancellationToken.None);
            await transaction.CommitAsync();
            return count;
        }))();
        // "m" held pos-1..pos-5, pos-6 and the other source's entry: the newest two are pos-6 and that one, which are also the newest of their sources; the five oldest of src/k go
        Assert.Equal(5, removed);
        var left = await state.ReadAfterAsync(connection, replication, 0, 100, CancellationToken.None);
        Assert.Equal(3, left.Count);
        Assert.Contains(left, u => u.Mapping == "m2");
        Assert.Contains(left, u => u.WatermarkAfter == "pos-6");

        // the oversized name is refused in words, not by the engine
        var tooLong = new PositionLogDraft(Guid.NewGuid(), new string('x', TargetPositionLog.MaxNameLength + 1), "m", "k", null, "p", null, 0, null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => state.WriteAsync(connection, null, tooLong, CancellationToken.None));
    }
}
