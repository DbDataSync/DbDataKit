using System.Data.Common;
using System.Security.Cryptography;

namespace DbDataKit.State;

/// <summary>What a payload says about itself.</summary>
/// <param name="Size">Bytes, known when the writer completed.</param>
/// <param name="Sha256">Lower-case hex, known when the writer completed.</param>
/// <param name="Complete">False while a writer has not finished (or died): such a payload cannot be read and is removed by retention.</param>
public sealed record PayloadInfo(Guid Id, long Size, string? Sha256, string? MediaType, string? OwnerModule, string? OwnerRow, bool Complete, DateTimeOffset CreatedAt, string? CreatedBy);

/// <summary>Who a payload belongs to and what it is, given when it is opened for writing.</summary>
/// <param name="OwnerModule">The module whose row refers to the payload; with <paramref name="OwnerRow"/>, how retention knows the owner is gone.</param>
/// <param name="OwnerRow">The key of the owning row, as text (a UUID in lower-case <c>D</c> format).</param>
public sealed record PayloadOptions(string? MediaType = null, string? OwnerModule = null, string? OwnerRow = null, string? CreatedBy = null);

/// <summary>
/// The suite's large values (plan 29 section 3.6): kept in chunks in the <c>payloads</c> module, written and read as streams, and never a column of another module's table. Memory
/// use is one chunk (1 MiB by default) on every engine, and the same code runs on all three. A writer sends chunks as they arrive and completes the payload with its size and SHA-256;
/// a reader reads chunk by chunk and checks the size and hash when it reaches the end. Another module stores the payload's id.
/// </summary>
public sealed class PayloadStore
{
    /// <summary>1 MiB, the owner's default (plan 29 section 3.6).</summary>
    public const int DefaultChunkSize = 1024 * 1024;

    public const string ModuleName = "payloads";

    private const string Writing = "writing";
    private const string Done = "complete";

    /// <summary>The module; a service that stores payloads includes it, and a module that refers to payloads depends on it.</summary>
    public static StateModule Module { get; } = new(
        ModuleName,
        StateScopeKind.Install,
        [
            """
            -- One row per large value; the bytes are in chunk. state is 'writing' until the writer completes it, which sets size and sha256.
            -- owner_module and owner_row say which row refers to the payload, so retention can remove it when that row goes.
            CREATE TABLE {table:payload} (
                id            {{uuid}} PRIMARY KEY,
                size          {{int}} NOT NULL,
                sha256        {{key}} NULL,
                media_type    {{key}} NULL,
                owner_module  {{key}} NULL,
                owner_row     {{key}} NULL,
                state         {{key}} NOT NULL,
                chunk_size    {{int}} NOT NULL,
                created_at    {{time}} NOT NULL,
                created_by    {{key}} NULL,
                completed_at  {{time}} NULL
            );

            -- The chunks of one payload, numbered from 0. Only ever read through PayloadStore.
            CREATE TABLE {table:chunk} (
                payload_id  {{uuid}} NOT NULL REFERENCES {table:payload}(id),
                sequence    {{int}} NOT NULL,
                data        {{blob}} NOT NULL,
                PRIMARY KEY (payload_id, sequence)
            );

            CREATE INDEX {index:payload_by_state} ON {table:payload}(state, created_at);
            CREATE INDEX {index:payload_by_owner} ON {table:payload}(owner_module, owner_row);
            """,
        ],
        // a payload whose writer never completed it (the writer died) goes after the retention age; owned payloads go with their owner row (see RetentionJob)
        retention: [new RetentionRule("payload", "created_at", RetentionRule.StandardDays, Where: "state = 'writing'", Children: [new RetentionChild("chunk", "payload_id")])]);

    private readonly StateDatabase _database;

    public PayloadStore(StateDatabase database, int chunkSize = DefaultChunkSize)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSize, 1);
        if (database.Layout?.Has(ModuleName) != true)
            throw new InvalidOperationException($"This state database does not include the module '{ModuleName}'; add PayloadStore.Module to the modules the service opens.");
        _database = database;
        ChunkSize = chunkSize;
    }

    /// <summary>Bytes in each chunk this store writes (a payload remembers its own, so a reader never depends on this).</summary>
    public int ChunkSize { get; }

    /// <summary>Starts a payload. Write to the stream, then call <see cref="PayloadWriter.Complete"/>; a writer disposed without completing leaves a payload that cannot be read and that retention removes.</summary>
    public PayloadWriter OpenWrite(PayloadOptions? options = null)
    {
        options ??= new PayloadOptions();
        var id = _database.NewId();
        var connection = _database.OpenConnection();
        try
        {
            using var insert = _database.Command(ModuleName, connection,
                "INSERT INTO {table:payload} (id, size, media_type, owner_module, owner_row, state, chunk_size, created_at, created_by) VALUES ($id, 0, $media, $owner_module, $owner_row, $state, $chunk, $now, $by);");
            insert.Bind(_database, "id", id)
                .Bind(_database, "media", options.MediaType, System.Data.DbType.String)
                .Bind(_database, "owner_module", options.OwnerModule, System.Data.DbType.String)
                .Bind(_database, "owner_row", options.OwnerRow, System.Data.DbType.String)
                .Bind(_database, "state", Writing)
                .Bind(_database, "chunk", (long)ChunkSize)
                .Bind(_database, "now", _database.Time.GetUtcNow())
                .Bind(_database, "by", options.CreatedBy, System.Data.DbType.String);
            insert.ExecuteNonQuery();
            return new PayloadWriter(_database, connection, id, ChunkSize);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Reads a completed payload as a stream; the size and hash are checked when the stream is read to its end. Throws for a payload that does not exist or is not complete.</summary>
    public PayloadReader OpenRead(Guid id)
    {
        var info = Get(id) ?? throw new FileNotFoundException($"There is no payload {id}.");
        if (!info.Complete)
            throw new InvalidOperationException($"The payload {id} was not completed by its writer, so it cannot be read.");
        return new PayloadReader(_database, _database.OpenConnection(), info, ChunkSizeOf(id));
    }

    public PayloadInfo? Get(Guid id)
    {
        using var connection = _database.OpenConnection();
        using var select = _database.Command(ModuleName, connection, "SELECT size, sha256, media_type, owner_module, owner_row, state, created_at, created_by FROM {table:payload} WHERE id = $id;");
        select.Bind(_database, "id", id);
        using var reader = select.ExecuteReader();
        if (!reader.Read()) return null;
        return new PayloadInfo(
            id, reader.Int64(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5) == Done, reader.Time(6), reader.IsDBNull(7) ? null : reader.GetString(7));
    }

    /// <summary>Removes a payload and its chunks; false if there was none.</summary>
    public bool Delete(Guid id)
    {
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var chunks = _database.Command(ModuleName, connection, transaction, "DELETE FROM {table:chunk} WHERE payload_id = $id;"))
        {
            chunks.Bind(_database, "id", id);
            chunks.ExecuteNonQuery();
        }

        int removed;
        using (var payload = _database.Command(ModuleName, connection, transaction, "DELETE FROM {table:payload} WHERE id = $id;"))
        {
            payload.Bind(_database, "id", id);
            removed = payload.ExecuteNonQuery();
        }

        transaction.Commit();
        return removed > 0;
    }

    private long ChunkSizeOf(Guid id)
    {
        using var connection = _database.OpenConnection();
        using var select = _database.Command(ModuleName, connection, "SELECT chunk_size FROM {table:payload} WHERE id = $id;");
        select.Bind(_database, "id", id);
        return Convert.ToInt64(select.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    internal static string Hex(byte[] hash) => Convert.ToHexStringLower(hash);

    internal static IncrementalHash NewHash() => IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    internal const string CompleteState = Done;
}

/// <summary>A payload being written: a write-only stream that sends a chunk each time one fills (memory: one chunk) and is finished by <see cref="Complete"/>.</summary>
public sealed class PayloadWriter : Stream
{
    private readonly StateDatabase _database;
    private readonly DbConnection _connection;
    private readonly byte[] _buffer;
    private readonly System.Security.Cryptography.IncrementalHash _hash = PayloadStore.NewHash();
    private int _filled;
    private long _sequence;
    private long _length;
    private bool _completed;
    private bool _disposed;

    internal PayloadWriter(StateDatabase database, DbConnection connection, Guid id, int chunkSize)
    {
        _database = database;
        _connection = connection;
        _buffer = new byte[chunkSize];
        Id = id;
    }

    /// <summary>The payload's id, for the row that refers to it.</summary>
    public Guid Id { get; }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => !_completed && !_disposed;

    public override long Length => _length;

    public override long Position
    {
        get => _length;
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        EnsureWritable();
        while (!buffer.IsEmpty)
        {
            var take = Math.Min(buffer.Length, _buffer.Length - _filled);
            buffer[..take].CopyTo(_buffer.AsSpan(_filled));
            _filled += take;
            _length += take;
            buffer = buffer[take..];
            if (_filled == _buffer.Length) FlushChunk();
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        while (!buffer.IsEmpty)
        {
            var take = Math.Min(buffer.Length, _buffer.Length - _filled);
            buffer[..take].Span.CopyTo(_buffer.AsSpan(_filled));
            _filled += take;
            _length += take;
            buffer = buffer[take..];
            if (_filled == _buffer.Length) await FlushChunkAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
        // a chunk is sent when it fills, or at Complete; a partial chunk is not sent early, so a chunk is always the payload's chunk size except the last
    }

    /// <summary>Sends the last chunk and marks the payload complete with its size and SHA-256. After this the payload can be read.</summary>
    public void Complete()
    {
        EnsureWritable();
        if (_filled > 0) FlushChunk();
        _completed = true;
        using var update = _database.Command(PayloadStore.ModuleName, _connection,
            "UPDATE {table:payload} SET size = $size, sha256 = $sha, state = $state, completed_at = $now WHERE id = $id;");
        update.Bind(_database, "size", _length)
            .Bind(_database, "sha", PayloadStore.Hex(_hash.GetHashAndReset()))
            .Bind(_database, "state", PayloadStore.CompleteState)
            .Bind(_database, "now", _database.Time.GetUtcNow())
            .Bind(_database, "id", Id);
        update.ExecuteNonQuery();
    }

    private void FlushChunk() => FlushChunkAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();

    private async ValueTask FlushChunkAsync(CancellationToken cancellationToken)
    {
        var data = _filled == _buffer.Length ? _buffer : _buffer.AsSpan(0, _filled).ToArray();
        _hash.AppendData(data, 0, _filled);
        using var insert = _database.Command(PayloadStore.ModuleName, _connection, "INSERT INTO {table:chunk} (payload_id, sequence, data) VALUES ($id, $sequence, $data);");
        insert.Bind(_database, "id", Id).Bind(_database, "sequence", _sequence++).Bind(_database, "data", data);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        _filled = 0;
    }

    private void EnsureWritable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed) throw new InvalidOperationException($"The payload {Id} is already complete.");
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _hash.Dispose();
            _connection.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>A completed payload being read: a forward-only stream that holds one chunk at a time and, at its end, checks the size and SHA-256 the writer recorded.</summary>
public sealed class PayloadReader : Stream
{
    private readonly StateDatabase _database;
    private readonly DbConnection _connection;
    private readonly PayloadInfo _info;
    private readonly long _chunkSize;
    private readonly System.Security.Cryptography.IncrementalHash _hash = PayloadStore.NewHash();
    private byte[] _chunk = [];
    private int _offset;
    private long _sequence;
    private long _position;
    private bool _verified;

    internal PayloadReader(StateDatabase database, DbConnection connection, PayloadInfo info, long chunkSize)
    {
        _database = database;
        _connection = connection;
        _info = info;
        _chunkSize = chunkSize;
    }

    public PayloadInfo Info => _info;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => _info.Size;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty) return 0;
        if (_offset >= _chunk.Length && !NextChunk(CancellationToken.None).AsTask().GetAwaiter().GetResult()) return 0;
        return Take(buffer);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty) return 0;
        if (_offset >= _chunk.Length && !await NextChunk(cancellationToken).ConfigureAwait(false)) return 0;
        return Take(buffer.Span);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private int Take(Span<byte> buffer)
    {
        var n = Math.Min(buffer.Length, _chunk.Length - _offset);
        _chunk.AsSpan(_offset, n).CopyTo(buffer);
        _offset += n;
        _position += n;
        return n;
    }

    /// <summary>Loads the next chunk, or at the end of the payload checks size and hash and returns false.</summary>
    private async ValueTask<bool> NextChunk(CancellationToken cancellationToken)
    {
        if (_position >= _info.Size)
        {
            Verify();
            return false;
        }

        using var select = _database.Command(PayloadStore.ModuleName, _connection, "SELECT data FROM {table:chunk} WHERE payload_id = $id AND sequence = $sequence;");
        select.Bind(_database, "id", _info.Id).Bind(_database, "sequence", _sequence);
        var value = await select.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is not byte[] data)
            throw new InvalidDataException($"The payload {_info.Id} is missing its chunk {_sequence}.");

        var expected = Math.Min(_chunkSize, _info.Size - _sequence * _chunkSize);
        if (data.Length != expected)
            throw new InvalidDataException($"The chunk {_sequence} of the payload {_info.Id} is {data.Length} bytes, not the {expected} the payload's size calls for.");

        _hash.AppendData(data);
        _chunk = data;
        _offset = 0;
        _sequence++;
        return true;
    }

    private void Verify()
    {
        if (_verified) return;
        _verified = true;
        var actual = PayloadStore.Hex(_hash.GetHashAndReset());
        if (!string.Equals(actual, _info.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException($"The payload {_info.Id} does not match the SHA-256 recorded when it was written (recorded {_info.Sha256}, read {actual}): its stored bytes have changed.");
    }

    public override void Flush()
    {
    }

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hash.Dispose();
            _connection.Dispose();
        }

        base.Dispose(disposing);
    }
}
