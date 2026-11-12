using System.Security.Cryptography;
using System.Text;
using DbDataKit.Runner.Kit.Changes;

namespace DbDataKit.Runner.Kit.Outbound;

/// <summary>One thing to send out: the key that identifies it at the other end, and its values. The hash of the content (what makes "changed") is of the values unless given.</summary>
public sealed record OutboundRecord(string Key, IReadOnlyDictionary<string, object?> Values, string? Hash = null)
{
    /// <summary>The hash of the content that is compared with the ledger: <see cref="RowHash"/> of the values in the order of their names.</summary>
    public string ContentHash => Hash ?? RowHash.Of(Values.OrderBy(v => v.Key, StringComparer.Ordinal).SelectMany(v => new object?[] { v.Key, v.Value }));
}

/// <summary>A batch to send and the idempotency key to send it under: the same for the same records with the same content, whichever run sends it, so the other end can recognise a repeat after a crash between the send and the save.</summary>
public sealed record PushBatch(IReadOnlyList<OutboundRecord> Records, string IdempotencyKey);

/// <summary>A batch of deletions to send.</summary>
public sealed record PushDeletion(IReadOnlyList<string> Keys, string IdempotencyKey);

/// <summary>What a push did.</summary>
/// <param name="Sent">Records sent (new or changed).</param>
/// <param name="Skipped">Records the ledger said were already out with this content.</param>
/// <param name="Deleted">Keys deleted at the other end.</param>
/// <param name="Batches">Calls made to send (records and deletions).</param>
public sealed record PushReport(long Sent, long Skipped, long Deleted, long Batches)
{
    public bool NothingToDo => Sent + Deleted == 0;
}

public sealed record PushOptions
{
    /// <summary>Records in one send. A smaller batch means less to repeat after a failure and a finer ledger.</summary>
    public int BatchSize { get; init; } = 100;

    /// <summary>Called after every batch has been sent and written to the ledger: pass the job's <c>SaveProgressAsync</c>, so a run that fails later keeps what it sent. Without it the ledger is saved with the run's outcome, and a run that fails halfway sends the sent part again.</summary>
    public Func<CancellationToken, Task>? Commit { get; init; }

    /// <summary>Prefix of every idempotency key (the target's name), so two targets fed by one task never share a key.</summary>
    public string KeyPrefix { get; init; } = "";

    /// <summary>A source that gave no records when the ledger holds some is refused before any deletion is sent (<see cref="PushException"/>): almost always a broken read. Set to accept it.</summary>
    public bool AllowEmptySource { get; init; }
}

/// <summary>A push that cannot go on and should not be retried as it is.</summary>
public sealed class PushException(string message) : Exception(message)
{
    public bool IsTemporary => false;
}

/// <summary>The idempotency keys the outbound patterns make. Deterministic: derived from what is sent, never random.</summary>
public static class IdempotencyKeys
{
    /// <summary>The key of one record's content: the same record with the same content always has it.</summary>
    public static string ForRecord(string key, string hash) => Hash("record", key, hash);

    /// <summary>The key of a batch: of the records' keys and hashes in order. A batch made of the same records and content has the same key whenever it is made, so a crash between sending and saving, and the retry that sends the same batch again, are one request to the other end.</summary>
    public static string ForBatch(string prefix, IEnumerable<(string Key, string Hash)> records) => prefix + Hash("batch", [.. records.SelectMany(r => new[] { r.Key, r.Hash })]);

    public static string ForDeletion(string prefix, IEnumerable<string> keys) => prefix + Hash("delete", [.. keys]);

    private static string Hash(string kind, params string[] parts)
    {
        var text = new StringBuilder(kind);
        foreach (var part in parts) text.Append('\u001f').Append(part.Length).Append(':').Append(part);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())).AsSpan(0, 16));
    }
}

/// <summary>
/// Sends suite data out to an API (plan 27a IX22): the records of a source, in batches, each exactly when its content is new or changed, with an idempotency key, and with a ledger of what went out so that
/// <list type="bullet">
/// <item><b>a push that fails halfway resumes without repeating the sent part:</b> after each batch the ledger is written and <see cref="PushOptions.Commit"/> saves it, so the next run starts at the first batch that was not sent; a crash between a send and the save repeats one batch, under the same idempotency key (<see cref="IdempotencyKeys.ForBatch"/>), which the other end recognises;</item>
/// <item><b>a second run with no change sends nothing:</b> a record whose key and content hash are in the ledger is skipped;</item>
/// <item><b>a deletion is sent only when asked for and only after the whole source was read:</b> a key in the ledger that the source no longer gives goes to the delete callback, never when the source is empty by accident.</item>
/// </list>
/// The send callback is the job's own call (<c>OutsideHttp.SendJsonAsync(HttpMethod.Post, "orders/batch", batch.Records, batch.IdempotencyKey)</c>): a failure propagates as it is classified, the ledger holds exactly the batches that were sent, and the run's retry continues.
/// </summary>
public sealed class OutboundPush(ISentLedger ledger, PushOptions? options = null)
{
    private readonly PushOptions _o = options ?? new PushOptions();

    public async Task<PushReport> PushAsync(
        IAsyncEnumerable<OutboundRecord> records, Func<PushBatch, CancellationToken, Task> send, Func<PushDeletion, CancellationToken, Task>? delete = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(send);
        if (_o.BatchSize < 1) throw new ArgumentOutOfRangeException(nameof(options), "A batch holds at least one record.");
        if (delete is not null && !ledger.CanListKeys) throw new PushException("This ledger cannot list the keys it holds, so it cannot find what disappeared; deletions need a SnapshotLedger.");
        long sent = 0, skipped = 0, deleted = 0, batches = 0, seen = 0;
        var keys = delete is null ? null : new HashSet<string>(StringComparer.Ordinal);
        var batch = new List<OutboundRecord>(_o.BatchSize);

        await foreach (var record in records.WithCancellation(ct).ConfigureAwait(false))
        {
            seen++;
            if (string.IsNullOrEmpty(record.Key)) throw new PushException($"Record {seen} has no key.");
            if (keys is not null && !keys.Add(record.Key)) throw new PushException($"A key appeared twice in the source (record {seen}); the key is not shown.");
            if (ledger.IsCurrent(record.Key, record.ContentHash))
            {
                skipped++;
                continue;
            }

            batch.Add(record);
            if (batch.Count >= _o.BatchSize) await FlushAsync().ConfigureAwait(false);
        }

        await FlushAsync().ConfigureAwait(false);

        if (delete is not null)
        {
            var gone = ledger.Keys.Where(k => !keys!.Contains(k)).Order(StringComparer.Ordinal).ToList();
            if (seen == 0 && ledger.Keys.Count > 0 && !_o.AllowEmptySource)
                throw new PushException($"The source gave no records and {ledger.Keys.Count} were sent before; refused as a broken read (PushOptions.AllowEmptySource accepts it).");
            for (var i = 0; i < gone.Count; i += _o.BatchSize)
            {
                var part = gone.Skip(i).Take(_o.BatchSize).ToList();
                await delete(new PushDeletion(part, IdempotencyKeys.ForDeletion(_o.KeyPrefix, part)), ct).ConfigureAwait(false);
                ledger.Forget(part);
                deleted += part.Count;
                batches++;
                if (_o.Commit is not null) await _o.Commit(ct).ConfigureAwait(false);
            }
        }

        return new PushReport(sent, skipped, deleted, batches);

        async Task FlushAsync()
        {
            if (batch.Count == 0) return;
            var key = IdempotencyKeys.ForBatch(_o.KeyPrefix, batch.Select(r => (r.Key, r.ContentHash)));
            await send(new PushBatch([.. batch], key), ct).ConfigureAwait(false);
            ledger.Record(batch.Select(r => (r.Key, r.ContentHash)));
            sent += batch.Count;
            batches++;
            batch.Clear();
            if (_o.Commit is not null) await _o.Commit(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sends a file once per content: <paramref name="write"/> renders it into a stream, its SHA-256 is the content hash, and <paramref name="send"/> gets the bytes and the idempotency key (<c>name</c> and hash) only when the ledger does not hold this name with this content.
    /// A file with the same content as last time is not sent; one that changed is sent under a new key. Returns whether it was sent.
    /// </summary>
    public async Task<bool> PushFileAsync(string name, Func<Stream, CancellationToken, Task> write, Func<Stream, string, CancellationToken, Task> send, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var file = Path.GetTempFileName();
        try
        {
            string hash;
            await using (var output = new FileStream(file, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await write(output, ct).ConfigureAwait(false);
                await output.FlushAsync(ct).ConfigureAwait(false);
                output.Position = 0;
                hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(output, ct).ConfigureAwait(false));
            }

            if (ledger.IsCurrent(name, hash)) return false;
            await using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous))
                await send(input, _o.KeyPrefix + IdempotencyKeys.ForRecord(name, hash), ct).ConfigureAwait(false);
            ledger.Record([(name, hash)]);
            if (_o.Commit is not null) await _o.Commit(ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { }
        }
    }
}
