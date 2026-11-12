using System.Data.Common;
using System.Globalization;
using System.Text;

namespace DbDataKit.State;

/// <summary>One embedding model's index in a channel (a row of <c>vector_index</c>).</summary>
/// <param name="State"><c>building</c> or <c>ready</c>.</param>
public sealed record VectorIndexInfo(string Channel, string ModelId, int Dimension, string State, DateTimeOffset CreatedAt, DateTimeOffset? ReadyAt)
{
    public bool IsReady => State == KnowledgeVectorStore.Ready;
}

/// <summary>A chunk that has no current vector for a model: what to embed.</summary>
public sealed record PendingChunk(string ItemId, int Seq, string HeadingPath, string Text, string Hash)
{
    /// <summary>What is embedded for the chunk: the headings it sits under and its text, as the search by words reads it.</summary>
    public string ToEmbed => HeadingPath.Length == 0 ? Text : HeadingPath + "\n" + Text;
}

/// <summary>A vector to store for a chunk, made from the chunk text whose hash is <see cref="Hash"/>.</summary>
public sealed record ChunkVector(string ItemId, int Seq, string Hash, float[] Vector);

/// <summary>A chunk whose embedding failed (the message never holds a key or a request).</summary>
public sealed record VectorFailure(string ItemId, int Seq, string ModelId, int Attempts, string Message, DateTimeOffset FailedAt);

/// <summary>How far a channel's vector index is.</summary>
/// <param name="Ready">The index searches use, or null when there is none yet.</param>
/// <param name="Building">The index being built beside it (a model was bound that is not the ready one), or null.</param>
/// <param name="Chunks">How many chunks the channel has.</param>
/// <param name="Embedded">How many of them have a current vector in the ready index.</param>
/// <param name="BuildingEmbedded">How many have one in the building index.</param>
/// <param name="Failed">Chunks with a recorded failure for the model being built (or the ready one when nothing is building).</param>
public sealed record VectorProgress(VectorIndexInfo? Ready, VectorIndexInfo? Building, int Chunks, int Embedded, int BuildingEmbedded, int Failed);

/// <summary>
/// Writes and reads the <c>knowledge_vectors</c> module (<see cref="KnowledgeVectorsModule"/>): which model a channel's index is of, the vectors of the chunks (<c>chunk_vector_&lt;n&gt;</c>, a table per dimension made on first use: <c>vector(n)</c> on PostgreSQL
/// with an HNSW index, <c>vector(n)</c> on SQL Server 2025 searched by <c>VECTOR_DISTANCE</c>) and the nearest chunks to a question. Like <see cref="KnowledgeStore"/> it knows rows, not knowledge: the library above it applies the access filter to what a search returns.
/// </summary>
public sealed class KnowledgeVectorStore(StateDatabase database)
{
    public const string Building = "building";
    public const string Ready = "ready";

    /// <summary>How often a chunk is tried before the embedder leaves it to the search by words.</summary>
    public const int MaximumAttempts = 3;

    /// <summary>The largest dimension a vector index takes: SQL Server 2025's <c>vector</c> type holds 1998 floats at most.</summary>
    public const int MaximumDimension = 1998;

    private const string Module = KnowledgeVectorsModule.Name;
    private const string Words = KnowledgeModule.Name;
    private readonly HashSet<int> _ensured = [];
    private readonly object _ensure = new();

    private bool IsMsSql => database.Dialect.Engine == StateEngineIds.MsSql;

    private bool IsPostgres => database.Dialect.Engine == StateEngineIds.Postgres;

    /// <summary>The rendered name of the table that holds the vectors of <paramref name="dimension"/> floats (beside <c>vector_index</c>, with the same schema and prefix).</summary>
    private string VectorTable(int dimension)
    {
        var index = database.Layout!.Table(Module, "vector_index");
        return index[..^"vector_index".Length] + "chunk_vector_" + dimension.ToString(CultureInfo.InvariantCulture);
    }

    private static string Literal(float[] vector)
    {
        var text = new StringBuilder("[");
        for (var i = 0; i < vector.Length; i++)
        {
            if (i > 0) text.Append(',');
            text.Append(vector[i].ToString("R", CultureInfo.InvariantCulture));
        }
        return text.Append(']').ToString();
    }

    /// <summary>The expression that reads a text parameter as a vector of <paramref name="dimension"/> floats.</summary>
    private string AsVector(string parameter, int dimension) => IsMsSql ? $"CAST({parameter} AS VECTOR({dimension}))" : $"CAST({parameter} AS vector({dimension}))";

    private void Run(DbConnection connection, string sql)
    {
        using var command = database.Command(Module, connection, sql);
        command.ExecuteNonQuery();
    }

    /// <summary>Makes the table for a dimension (and PostgreSQL's HNSW index over it) the first time it is needed. Derived data: it can always be made again by embedding the chunks.</summary>
    private void EnsureTable(DbConnection connection, int dimension)
    {
        if (dimension is < 1 or > MaximumDimension)
            throw new ArgumentOutOfRangeException(nameof(dimension), $"A vector index holds between 1 and {MaximumDimension} dimensions (SQL Server 2025's vector type takes at most {MaximumDimension}); the model gives {dimension}.");
        lock (_ensure)
        {
            if (_ensured.Contains(dimension)) return;
            var table = VectorTable(dimension);
            var bare = table[(table.LastIndexOf('.') + 1)..];
            if (IsMsSql)
                Run(connection, $"IF OBJECT_ID(N'{table}', N'U') IS NULL CREATE TABLE {table} (channel {{{{key}}}} NOT NULL, model_id {{{{key}}}} NOT NULL, item_id {{{{key}}}} NOT NULL, seq {{{{int}}}} NOT NULL, chunk_hash {{{{key}}}} NOT NULL, embedding VECTOR({dimension}) NOT NULL, embedded_at {{{{time}}}} NOT NULL, PRIMARY KEY (channel, model_id, item_id, seq));");
            else
            {
                Run(connection, $"CREATE TABLE IF NOT EXISTS {table} (channel {{{{key}}}} NOT NULL, model_id {{{{key}}}} NOT NULL, item_id {{{{key}}}} NOT NULL, seq {{{{int}}}} NOT NULL, chunk_hash {{{{key}}}} NOT NULL, embedding vector({dimension}) NOT NULL, embedded_at {{{{time}}}} NOT NULL, PRIMARY KEY (channel, model_id, item_id, seq));");
                // pgvector indexes at most 2000 dimensions (this module at most 1998): HNSW, cosine
                if (IsPostgres) Run(connection, $"CREATE INDEX IF NOT EXISTS {bare}_hnsw ON {table} USING hnsw (embedding vector_cosine_ops);");
            }
            _ensured.Add(dimension);
        }
    }

    // ---- the indexes of a channel ----

    private static VectorIndexInfo ReadIndex(DbDataReader r) => new(r.GetString(0), r.GetString(1), Convert.ToInt32(r.GetValue(2), CultureInfo.InvariantCulture), r.GetString(3), r.Time(4), r.NullableTime(5));

    /// <summary>The indexes of a channel: the ready one and the one being built, when there are any.</summary>
    public IReadOnlyList<VectorIndexInfo> Indexes(string channel) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            return (IReadOnlyList<VectorIndexInfo>)Indexes(connection, channel);
        });

    private List<VectorIndexInfo> Indexes(DbConnection connection, string channel)
    {
        using var command = database.Command(Module, connection, "SELECT channel, model_id, dimension, state, created_at, ready_at FROM {table:vector_index} WHERE channel = $channel ORDER BY created_at, model_id;");
        command.Bind(database, "channel", channel);
        using var reader = command.ExecuteReader();
        var list = new List<VectorIndexInfo>();
        while (reader.Read()) list.Add(ReadIndex(reader));
        return list;
    }

    /// <summary>The index searches use in a channel, or null when none is ready yet.</summary>
    public VectorIndexInfo? ReadyIndex(string channel) => Indexes(channel).FirstOrDefault(i => i.IsReady);

    /// <summary>
    /// Says which model the channel is to be embedded with and how many dimensions it gives. The first model of a channel becomes its index (building until every chunk has a vector, then ready); a <b>different</b> model starts a second index beside
    /// the ready one, which keeps answering until <see cref="Complete"/> switches. The same model with another number of dimensions is refused: the dimension is recorded with the model's id and a model does not change its size, so a changed size means
    /// the id names something else (<see cref="DimensionMismatch"/>). Returns the index being filled.
    /// </summary>
    public VectorIndexInfo Begin(string channel, string modelId, int dimension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        return database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            var have = Indexes(connection, channel);
            if (have.FirstOrDefault(i => i.ModelId == modelId) is { } same)
            {
                if (same.Dimension != dimension) throw new VectorDimensionException(DimensionMismatch(channel, modelId, same.Dimension, dimension));
                return same;
            }
            EnsureTable(connection, dimension);
            foreach (var stale in have.Where(i => !i.IsReady)) EnsureTable(connection, stale.Dimension);
            using var transaction = database.BeginTransaction(connection, Module);
            // a build that was started for another model and never finished is replaced by this one
            foreach (var stale in have.Where(i => !i.IsReady))
                DeleteIndex(connection, transaction, channel, stale);
            var now = database.Time.GetUtcNow();
            using (var insert = database.Command(Module, connection, transaction, "INSERT INTO {table:vector_index} (channel, model_id, dimension, state, created_at) VALUES ($channel, $model, $dimension, $state, $at);"))
            {
                insert.Bind(database, "channel", channel).Bind(database, "model", modelId).Bind(database, "dimension", dimension).Bind(database, "state", Building).Bind(database, "at", now);
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
            return new VectorIndexInfo(channel, modelId, dimension, Building, now, null);
        });
    }

    /// <summary>The words of the refusal when a model's vectors do not have the size its index was made for.</summary>
    public static string DimensionMismatch(string channel, string modelId, int expected, int actual) =>
        $"The vector index of channel '{channel}' holds {expected}-dimension vectors of the model '{modelId}', and this model gave {actual}. One model per index: bind a model with another id to build a new index beside it, or rebuild the index for this one.";

    /// <summary>Removes an index and its vectors and failures inside the caller's transaction; the caller has made sure the table of its dimension exists (<see cref="EnsureTable"/> must not run under a pending transaction).</summary>
    private void DeleteIndex(DbConnection connection, DbTransaction transaction, string channel, VectorIndexInfo index)
    {
        using (var vectors = database.Command(Module, connection, transaction, $"DELETE FROM {VectorTable(index.Dimension)} WHERE channel = $channel AND model_id = $model;"))
        {
            vectors.Bind(database, "channel", channel).Bind(database, "model", index.ModelId);
            vectors.ExecuteNonQuery();
        }
        foreach (var table in new[] { "vector_failure", "vector_index" })
        {
            using var delete = database.Command(Module, connection, transaction, $"DELETE FROM {{table:{table}}} WHERE channel = $channel AND model_id = $model;");
            delete.Bind(database, "channel", channel).Bind(database, "model", index.ModelId);
            delete.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Gives the chunks of <paramref name="to"/> the vectors that chunks of <paramref name="from"/> already have for the same model, where the text is the same (the same item, place and chunk hash): a new version of a knowledge project reuses what it did not change, so
    /// only changed text is embedded (plan 27a KN11). <paramref name="to"/> must have an index for the model (<see cref="Begin"/>) and its chunks must be written. Returns how many vectors were copied.
    /// </summary>
    public int CopyUnchanged(string from, string to, string modelId, int dimension) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            EnsureTable(connection, dimension);
            var table = VectorTable(dimension);
            using var command = database.Command(Module, connection,
                $"INSERT INTO {table} (channel, model_id, item_id, seq, chunk_hash, embedding, embedded_at) " +
                $"SELECT $to, v.model_id, v.item_id, v.seq, v.chunk_hash, v.embedding, v.embedded_at FROM {table} v " +
                $"JOIN {{table:{Words}.chunk}} c ON c.channel = $to AND c.item_id = v.item_id AND c.seq = v.seq AND c.hash = v.chunk_hash " +
                $"WHERE v.channel = $from AND v.model_id = $model AND NOT EXISTS (SELECT 1 FROM {table} t WHERE t.channel = $to AND t.model_id = v.model_id AND t.item_id = v.item_id AND t.seq = v.seq);");
            command.Bind(database, "to", to).Bind(database, "from", from).Bind(database, "model", modelId);
            return command.ExecuteNonQuery();
        });

    /// <summary>Removes every vector index of a channel (a rebuild from nothing). Words are untouched.</summary>
    public void Drop(string channel) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            var all = Indexes(connection, channel);
            foreach (var index in all) EnsureTable(connection, index.Dimension);
            using var transaction = database.BeginTransaction(connection, Module);
            foreach (var index in all) DeleteIndex(connection, transaction, channel, index);
            transaction.Commit();
        });

    // ---- what to embed ----

    private string SameChunk(string vectors) => $"c.channel = {vectors}.channel AND c.item_id = {vectors}.item_id AND c.seq = {vectors}.seq AND c.hash = {vectors}.chunk_hash";

    /// <summary>
    /// The chunks of a channel that have no current vector in <paramref name="index"/> (no row, or a row made from other text) and have not failed <see cref="MaximumAttempts"/> times, in order, at most <paramref name="limit"/>. The index must exist
    /// (<see cref="Begin"/>).
    /// </summary>
    public IReadOnlyList<PendingChunk> Pending(string channel, VectorIndexInfo index, int limit = 32) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            EnsureTable(connection, index.Dimension);
            using var command = database.Command(Module, connection,
                $"SELECT c.item_id, c.seq, c.heading_path, c.content, c.hash FROM {{table:{Words}.chunk}} c " +
                $"WHERE c.channel = $channel AND NOT EXISTS (SELECT 1 FROM {VectorTable(index.Dimension)} v WHERE v.model_id = $model AND {SameChunk("v")}) " +
                $"AND NOT EXISTS (SELECT 1 FROM {{table:vector_failure}} f WHERE f.channel = c.channel AND f.model_id = $model AND f.item_id = c.item_id AND f.seq = c.seq AND f.attempts >= $attempts) " +
                $"ORDER BY c.item_id, c.seq {database.Limit("limit")};");
            command.Bind(database, "channel", channel).Bind(database, "model", index.ModelId).Bind(database, "attempts", MaximumAttempts).Bind(database, "limit", limit);
            using var reader = command.ExecuteReader();
            var list = new List<PendingChunk>();
            while (reader.Read()) list.Add(new PendingChunk(reader.GetString(0), Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
            return (IReadOnlyList<PendingChunk>)list;
        });

    /// <summary>
    /// Stores vectors for chunks. A vector is written only if the chunk still has the text it was made from (its hash): a chunk replaced while it was being embedded gets nothing, and the embedder finds it pending again. Returns how many were written.
    /// A vector of another size than the index's is refused, writing nothing (<see cref="VectorDimensionException"/>).
    /// </summary>
    public int Put(string channel, VectorIndexInfo index, IReadOnlyList<ChunkVector> vectors)
    {
        foreach (var v in vectors)
            if (v.Vector.Length != index.Dimension)
                throw new VectorDimensionException(DimensionMismatch(channel, index.ModelId, index.Dimension, v.Vector.Length));
        return database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            EnsureTable(connection, index.Dimension);
            using var transaction = database.BeginTransaction(connection, Module);
            var written = 0;
            var table = VectorTable(index.Dimension);
            var now = database.Time.GetUtcNow();
            foreach (var v in vectors)
            {
                using (var delete = database.Command(Module, connection, transaction, $"DELETE FROM {table} WHERE channel = $channel AND model_id = $model AND item_id = $item AND seq = $seq;"))
                {
                    delete.Bind(database, "channel", channel).Bind(database, "model", index.ModelId).Bind(database, "item", v.ItemId).Bind(database, "seq", v.Seq);
                    delete.ExecuteNonQuery();
                }
                using var insert = database.Command(Module, connection, transaction,
                    $"INSERT INTO {table} (channel, model_id, item_id, seq, chunk_hash, embedding, embedded_at) SELECT $channel, $model, c.item_id, c.seq, c.hash, {AsVector("$embedding", index.Dimension)}, $at " +
                    $"FROM {{table:{Words}.chunk}} c WHERE c.channel = $channel AND c.item_id = $item AND c.seq = $seq AND c.hash = $hash;");
                insert.Bind(database, "channel", channel).Bind(database, "model", index.ModelId).Bind(database, "embedding", Literal(v.Vector)).Bind(database, "at", now)
                    .Bind(database, "item", v.ItemId).Bind(database, "seq", v.Seq).Bind(database, "hash", v.Hash);
                written += insert.ExecuteNonQuery();
                ClearFailure(connection, transaction, channel, index.ModelId, v.ItemId, v.Seq);
            }
            transaction.Commit();
            return written;
        });
    }

    private void ClearFailure(DbConnection connection, DbTransaction transaction, string channel, string modelId, string itemId, int seq)
    {
        using var delete = database.Command(Module, connection, transaction, "DELETE FROM {table:vector_failure} WHERE channel = $channel AND model_id = $model AND item_id = $item AND seq = $seq;");
        delete.Bind(database, "channel", channel).Bind(database, "model", modelId).Bind(database, "item", itemId).Bind(database, "seq", seq);
        delete.ExecuteNonQuery();
    }

    /// <summary>
    /// Records that a chunk could not be embedded and why. The attempts count up when <paramref name="counts"/> is true (the chunk itself is the trouble); a failure that is the provider's (it was unreachable) is recorded so the screen can say why, and does not
    /// count, so an outage never leaves every chunk given up on.
    /// </summary>
    public void Fail(string channel, string modelId, string itemId, int seq, string message, bool counts = true) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var transaction = database.BeginTransaction(connection, Module);
            var attempts = 0;
            using (var read = database.Command(Module, connection, transaction, "SELECT attempts FROM {table:vector_failure} WHERE channel = $channel AND model_id = $model AND item_id = $item AND seq = $seq;"))
            {
                read.Bind(database, "channel", channel).Bind(database, "model", modelId).Bind(database, "item", itemId).Bind(database, "seq", seq);
                if (read.ExecuteScalar() is { } value and not DBNull) attempts = Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            ClearFailure(connection, transaction, channel, modelId, itemId, seq);
            using var insert = database.Command(Module, connection, transaction, "INSERT INTO {table:vector_failure} (channel, model_id, item_id, seq, attempts, message, failed_at) VALUES ($channel, $model, $item, $seq, $attempts, $message, $at);");
            insert.Bind(database, "channel", channel).Bind(database, "model", modelId).Bind(database, "item", itemId).Bind(database, "seq", seq).Bind(database, "attempts", counts ? attempts + 1 : attempts)
                .Bind(database, "message", message.Length > 1000 ? message[..1000] : message).Bind(database, "at", database.Time.GetUtcNow());
            insert.ExecuteNonQuery();
            transaction.Commit();
        });

    /// <summary>The recorded failures of a channel, for the model asked (every model when null), most recent first.</summary>
    public IReadOnlyList<VectorFailure> Failures(string channel, string? modelId = null) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var command = database.Command(Module, connection, $"SELECT item_id, seq, model_id, attempts, message, failed_at FROM {{table:vector_failure}} WHERE channel = $channel {(modelId is null ? "" : "AND model_id = $model")} ORDER BY failed_at DESC, item_id, seq;");
            command.Bind(database, "channel", channel);
            if (modelId is not null) command.Bind(database, "model", modelId);
            using var reader = command.ExecuteReader();
            var list = new List<VectorFailure>();
            while (reader.Read()) list.Add(new VectorFailure(reader.GetString(0), Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture), reader.GetString(2), Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture), reader.GetString(4), reader.Time(5)));
            return (IReadOnlyList<VectorFailure>)list;
        });

    /// <summary>Forgets the failures of a model so its chunks are tried again.</summary>
    public void RetryFailures(string channel, string modelId) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var delete = database.Command(Module, connection, "DELETE FROM {table:vector_failure} WHERE channel = $channel AND model_id = $model;");
            delete.Bind(database, "channel", channel).Bind(database, "model", modelId);
            delete.ExecuteNonQuery();
        });

    // ---- keeping the vectors as long as their chunks ----

    /// <summary>
    /// Removes the vectors (and failures) of chunks that no longer exist or whose text changed: a vector lives exactly as long as the text it was made from. A search never returns such a vector in any case (it joins on the hash); this frees the space.
    /// Returns how many vectors it removed.
    /// </summary>
    public int Sweep(string channel) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            var removed = 0;
            var all = Indexes(connection, channel);
            foreach (var index in all) EnsureTable(connection, index.Dimension);
            using var transaction = database.BeginTransaction(connection, Module);
            foreach (var index in all)
            {
                using var delete = database.Command(Module, connection, transaction,
                    $"DELETE FROM {VectorTable(index.Dimension)} WHERE channel = $channel AND NOT EXISTS (SELECT 1 FROM {{table:{Words}.chunk}} c WHERE {SameChunk(VectorTable(index.Dimension))});");
                delete.Bind(database, "channel", channel);
                removed += delete.ExecuteNonQuery();
            }
            using (var failures = database.Command(Module, connection, transaction,
                $"DELETE FROM {{table:vector_failure}} WHERE channel = $channel AND NOT EXISTS (SELECT 1 FROM {{table:{Words}.chunk}} c WHERE c.channel = {{table:vector_failure}}.channel AND c.item_id = {{table:vector_failure}}.item_id AND c.seq = {{table:vector_failure}}.seq);"))
            {
                failures.Bind(database, "channel", channel);
                failures.ExecuteNonQuery();
            }
            transaction.Commit();
            return removed;
        });

    /// <summary>
    /// Makes the building index the one searches use, when every chunk has a vector in it or has failed for good (<see cref="MaximumAttempts"/>): in one transaction it becomes ready and the earlier ready index, with its vectors, is removed, so a search answers from the old
    /// index until this moment and from the new one after it. Returns false when nothing is building or chunks are still to do.
    /// </summary>
    public bool Complete(string channel)
    {
        return database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            var indexes = Indexes(connection, channel);
            if (indexes.FirstOrDefault(i => !i.IsReady) is not { } building) return false;
            if (PendingCount(connection, channel, building) > 0) return false;
            foreach (var index in indexes) EnsureTable(connection, index.Dimension);
            using var transaction = database.BeginTransaction(connection, Module);
            foreach (var old in indexes.Where(i => i.IsReady)) DeleteIndex(connection, transaction, channel, old);
            using var ready = database.Command(Module, connection, transaction, "UPDATE {table:vector_index} SET state = $state, ready_at = $at WHERE channel = $channel AND model_id = $model;");
            ready.Bind(database, "state", Ready).Bind(database, "at", database.Time.GetUtcNow()).Bind(database, "channel", channel).Bind(database, "model", building.ModelId);
            ready.ExecuteNonQuery();
            transaction.Commit();
            return true;
        });
    }

    /// <summary>How many chunks still need a vector in <paramref name="index"/> (ones that failed for good are not counted).</summary>
    public int Remaining(string channel, VectorIndexInfo index) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            return PendingCount(connection, channel, index);
        });

    /// <summary>Gives up the index being built (the model that was being bound is not wanted after all), leaving the ready one.</summary>
    public void DropBuilding(string channel) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            var building = Indexes(connection, channel).Where(i => !i.IsReady).ToList();
            if (building.Count == 0) return;
            foreach (var index in building) EnsureTable(connection, index.Dimension);
            using var transaction = database.BeginTransaction(connection, Module);
            foreach (var index in building) DeleteIndex(connection, transaction, channel, index);
            transaction.Commit();
        });

    private int PendingCount(DbConnection connection, string channel, VectorIndexInfo index)
    {
        EnsureTable(connection, index.Dimension);
        using var command = database.Command(Module, connection,
            $"SELECT COUNT(*) FROM {{table:{Words}.chunk}} c WHERE c.channel = $channel AND NOT EXISTS (SELECT 1 FROM {VectorTable(index.Dimension)} v WHERE v.model_id = $model AND {SameChunk("v")}) " +
            "AND NOT EXISTS (SELECT 1 FROM {table:vector_failure} f WHERE f.channel = c.channel AND f.model_id = $model AND f.item_id = c.item_id AND f.seq = c.seq AND f.attempts >= $attempts);");
        command.Bind(database, "channel", channel).Bind(database, "model", index.ModelId).Bind(database, "attempts", MaximumAttempts);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>How far the channel's vector index is: the ready and the building index, how many chunks there are and how many have a current vector in each.</summary>
    public VectorProgress Progress(string channel) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            var indexes = Indexes(connection, channel);
            var ready = indexes.FirstOrDefault(i => i.IsReady);
            var building = indexes.FirstOrDefault(i => !i.IsReady);
            int Current(VectorIndexInfo? index)
            {
                if (index is null) return 0;
                EnsureTable(connection, index.Dimension);
                using var command = database.Command(Module, connection, $"SELECT COUNT(*) FROM {VectorTable(index.Dimension)} v JOIN {{table:{Words}.chunk}} c ON {SameChunk("v")} WHERE v.channel = $channel AND v.model_id = $model;");
                command.Bind(database, "channel", channel).Bind(database, "model", index.ModelId);
                return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
            using var chunks = database.Command(Module, connection, $"SELECT COUNT(*) FROM {{table:{Words}.chunk}} WHERE channel = $channel;");
            chunks.Bind(database, "channel", channel);
            var total = Convert.ToInt32(chunks.ExecuteScalar(), CultureInfo.InvariantCulture);
            var focus = building ?? ready;
            var failed = 0;
            if (focus is not null)
            {
                using var count = database.Command(Module, connection, "SELECT COUNT(*) FROM {table:vector_failure} WHERE channel = $channel AND model_id = $model;");
                count.Bind(database, "channel", channel).Bind(database, "model", focus.ModelId);
                failed = Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
            return new VectorProgress(ready, building, total, Current(ready), Current(building), failed);
        });

    // ---- searching ----

    /// <summary>
    /// The chunks of a channel nearest to <paramref name="query"/> in the ready index (cosine similarity, 1 is the same direction), nearest first, at most <paramref name="limit"/>. No access filter: who may see an item is the caller's to apply. A vector of another
    /// size than the index's is refused; a channel with no ready index finds nothing.
    /// </summary>
    public IReadOnlyList<TextMatch> Nearest(string channel, float[] query, int limit = 50)
    {
        if (ReadyIndex(channel) is not { } index) return [];
        if (query.Length != index.Dimension) throw new VectorDimensionException(DimensionMismatch(channel, index.ModelId, index.Dimension, query.Length));
        return database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            EnsureTable(connection, index.Dimension);
            if (IsPostgres)
            {
                // with a filter on the channel and model the HNSW index can run out of candidates before the limit: pgvector 0.8 keeps scanning when asked to (an older one ignores the setting)
                try { Run(connection, "SET hnsw.iterative_scan = relaxed_order;"); }
                catch (DbException) { }
            }
            var table = VectorTable(index.Dimension);
            var distance = IsMsSql ? $"VECTOR_DISTANCE('cosine', v.embedding, {AsVector("$query", index.Dimension)})" : $"(v.embedding <=> {AsVector("$query", index.Dimension)})";
            using var command = database.Command(Module, connection,
                $"SELECT v.item_id, v.seq, 1 - {distance} AS score FROM {table} v JOIN {{table:{Words}.chunk}} c ON {SameChunk("v")} WHERE v.channel = $channel AND v.model_id = $model ORDER BY {distance}, v.item_id, v.seq {database.Limit("limit")};");
            command.Bind(database, "query", Literal(query)).Bind(database, "channel", channel).Bind(database, "model", index.ModelId).Bind(database, "limit", limit);
            using var reader = command.ExecuteReader();
            var matches = new List<TextMatch>();
            while (reader.Read()) matches.Add(new TextMatch(reader.GetString(0), Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture), Convert.ToDouble(reader.GetValue(2), CultureInfo.InvariantCulture)));
            return (IReadOnlyList<TextMatch>)matches;
        });
    }
}

/// <summary>A vector does not have the number of dimensions the index was made for.</summary>
public sealed class VectorDimensionException(string message) : InvalidOperationException(message);
