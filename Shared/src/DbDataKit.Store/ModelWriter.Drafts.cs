using System.Data.Common;
using System.Text.Json.Nodes;
using DbDataKit.DataModel;
using DbDataKit.DataModel.Storage;

namespace DbDataKit.Store;

/// <summary>
/// One accepted write of a draft: when, by whom, the changes, and whether they still apply on top of the real data now. <see cref="Applies"/> is false (a <see cref="Problem"/> says why) when the real data moved
/// under the draft: a version the change read is gone, a row it names was removed, a rule now breaks. Such an entry is left out of what the draft shows.
/// </summary>
public sealed record DraftEntry(long Seq, DateTimeOffset At, WriteActor Actor, IReadOnlyList<RowChange> Changes, bool Applies, string? Problem);

/// <summary>A draft of a model's copy: its id, how many writes it holds and when the first and the last were made.</summary>
public sealed record DraftSummary(string Id, int Writes, DateTimeOffset FirstAt, DateTimeOffset LastAt);

/// <summary>
/// The draft layer of a stored model (plan 21 CH01). A writer made with a draft id writes into that draft and reads the real data with the draft on top: the writes of a preview or channel are kept in <c>_draft</c>
/// (<see cref="DraftStorage"/>), never in the entity tables or <c>_audit</c>, and every one is checked against the model's rules and versions on top of the draft before it is kept. A draft is read by replaying its writes in order
/// inside a transaction that is always rolled back, so what it shows is exactly what the writes would give, every rule included, and the real data is not touched. Another draft and the real data do not see it. Discarding
/// deletes the rows; submitting (the server's part) turns the writes into one ordinary change request.
/// </summary>
public sealed partial class ModelWriter
{
    /// <summary>The draft this writer works in, or null for the real data.</summary>
    public string? Draft => _draft;

    private string DraftTableName => Table(DraftStorage.Table);

    /// <summary>Runs a read against the real data, or against the draft on top of it: the draft's writes are replayed in a transaction that is rolled back after the read.</summary>
    private async Task<T> ViewAsync<T>(Func<DbTransaction?, Task<T>> read, CancellationToken cancellationToken)
    {
        if (_draft is null || !await DraftStorage.ExistsAsync(_connection, _schema, cancellationToken).ConfigureAwait(false)) return await read(null).ConfigureAwait(false);
        await using var transaction = await _connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReplayAsync(transaction, cancellationToken).ConfigureAwait(false);
            return await read(transaction).ConfigureAwait(false);
        }
        finally { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
    }

    /// <summary>The writes of the draft, oldest first, each with whether it applies on top of the real data now. A writer without a draft has none.</summary>
    public async Task<IReadOnlyList<DraftEntry>> DraftEntriesAsync(CancellationToken cancellationToken = default)
    {
        if (_draft is null || !await DraftStorage.ExistsAsync(_connection, _schema, cancellationToken).ConfigureAwait(false)) return [];
        await using var transaction = await _connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try { return await ReplayAsync(transaction, cancellationToken).ConfigureAwait(false); }
        finally { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
    }

    /// <summary>The same copy of the model without the draft: the real data.</summary>
    public ModelWriter WithoutDraft() => new(_model, _connection, _engine, _clock, _schema, _instance, draft: null);

    /// <summary>Every change of the draft in order (the entries that still apply and the ones that do not): what submitting proposes.</summary>
    public async Task<IReadOnlyList<RowChange>> DraftChangesAsync(CancellationToken cancellationToken = default) =>
        [.. (await DraftEntriesAsync(cancellationToken).ConfigureAwait(false)).SelectMany(e => e.Changes)];

    /// <summary>Removes the draft's writes; returns how many writes there were.</summary>
    public async Task<int> DiscardDraftAsync(CancellationToken cancellationToken = default)
    {
        if (_draft is null) throw new StoreNotSupportedException("This writer has no draft to discard.");
        if (!await DraftStorage.ExistsAsync(_connection, _schema, cancellationToken).ConfigureAwait(false)) return 0;
        return await ExecuteAsync(null, $"DELETE FROM {DraftTableName} WHERE {Col("draft_id")} = @p0{Mine()}", [_draft], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The drafts this copy of the model holds, by id (what the expiry of previews walks).</summary>
    public async Task<IReadOnlyList<DraftSummary>> DraftsAsync(CancellationToken cancellationToken = default)
    {
        if (!await DraftStorage.ExistsAsync(_connection, _schema, cancellationToken).ConfigureAwait(false)) return [];
        await using var command = Command(null, $"SELECT {Col("draft_id")}, COUNT(*), MIN({Col("at")}), MAX({Col("at")}) FROM {DraftTableName} WHERE 1 = 1{Mine()} GROUP BY {Col("draft_id")} ORDER BY {Col("draft_id")}", []);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var found = new List<DraftSummary>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) found.Add(new DraftSummary(reader.GetString(0), Convert.ToInt32(reader.GetValue(1)), Instant(reader.GetValue(2)), Instant(reader.GetValue(3))));
        return found;
    }

    /// <summary>A write into the draft: checked on top of the draft as <see cref="ApplyAsync"/> checks on the real data, then kept. With <paramref name="commit"/> false only the check is made.</summary>
    private async Task DraftApplyAsync(IReadOnlyList<RowChange> changes, WriteActor actor, bool commit, CancellationToken cancellationToken)
    {
        var exists = await DraftStorage.ExistsAsync(_connection, _schema, cancellationToken).ConfigureAwait(false);
        await using (var transaction = await _connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                if (exists) await ReplayAsync(transaction, cancellationToken).ConfigureAwait(false);
                await ApplyInnerAsync(transaction, changes, actor, cancellationToken).ConfigureAwait(false);
            }
            finally { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
        }
        if (!commit) return;

        await DraftStorage.EnsureAsync(_connection, _engine, _schema, _instance is not null, cancellationToken).ConfigureAwait(false);
        var at = _clock.GetUtcNow();
        // a row that starts or ends "now" keeps the moment of the write, so the draft shows the same thing whenever it is read
        var kept = changes.Select(c => c.EffectiveFrom is null && c.Action != RowAction.Update && Dated(RequireEntity(c.Entity)) ? c with { EffectiveFrom = at } : c).ToList();
        var json = new JsonArray([.. kept.Select(c => (JsonNode)RowJson.Change(c))]).ToJsonString();
        await using var write = await _connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var next = Convert.ToInt64(await ScalarAsync(write, $"SELECT COALESCE(MAX({Col("seq")}), 0) + 1 FROM {DraftTableName} WHERE {Col("draft_id")} = @p0{Mine()}", [_draft], cancellationToken).ConfigureAwait(false));
            var columns = new List<string> { "draft_id", "seq", "at", "actor_id", "actor_name", "change_json" };
            var values = new List<object?> { _draft, next, at, actor.Id, actor.Name, json };
            if (_instance is not null) { columns.Insert(0, ModelStorage.InstanceColumn); values.Insert(0, _instance); }
            await ExecuteAsync(write, $"INSERT INTO {DraftTableName} ({string.Join(", ", columns.Select(Col))}) VALUES ({string.Join(", ", columns.Select((_, i) => "@p" + i))})", values, cancellationToken).ConfigureAwait(false);
            await write.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await write.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Applies the draft's writes in order inside the transaction (which the caller rolls back): each write in a savepoint, so one that no longer applies is undone as a whole, noted with its reason and left out, and the
    /// ones after it still show.
    /// </summary>
    private async Task<IReadOnlyList<DraftEntry>> ReplayAsync(DbTransaction transaction, CancellationToken cancellationToken)
    {
        var stored = new List<(long Seq, DateTimeOffset At, WriteActor Actor, string Json)>();
        await using (var command = Command(transaction, $"SELECT {Col("seq")}, {Col("at")}, {Col("actor_id")}, {Col("actor_name")}, {Col("change_json")} FROM {DraftTableName} WHERE {Col("draft_id")} = @p0{Mine()} ORDER BY {Col("seq")}", [_draft]))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                stored.Add((Convert.ToInt64(reader.GetValue(0)), Instant(reader.GetValue(1)), new WriteActor(reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)), reader.GetString(4)));

        var entries = new List<DraftEntry>();
        foreach (var (seq, at, actor, json) in stored)
        {
            var (changes, bad) = RowJson.Changes((JsonArray)JsonNode.Parse(json)!);
            if (changes is null) { entries.Add(new DraftEntry(seq, at, actor, [], false, bad)); continue; }
            await ExecuteRawAsync(transaction, _engine == "sqlserver" ? "SAVE TRANSACTION draft_write" : "SAVEPOINT draft_write", cancellationToken).ConfigureAwait(false);
            try
            {
                await ApplyInnerAsync(transaction, changes, actor, cancellationToken).ConfigureAwait(false);
                if (_engine == "postgres") await ExecuteRawAsync(transaction, "RELEASE SAVEPOINT draft_write", cancellationToken).ConfigureAwait(false);
                entries.Add(new DraftEntry(seq, at, actor, changes, true, null));
            }
            catch (Exception ex) when (ex is WriteRefusedException or VersionConflictException or RowNotFoundException)
            {
                await ExecuteRawAsync(transaction, _engine == "sqlserver" ? "ROLLBACK TRANSACTION draft_write" : "ROLLBACK TO SAVEPOINT draft_write", cancellationToken).ConfigureAwait(false);
                entries.Add(new DraftEntry(seq, at, actor, changes, false, ex.Message));
            }
        }
        return entries;
    }

    private async Task ExecuteRawAsync(DbTransaction transaction, string text, CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = text;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
