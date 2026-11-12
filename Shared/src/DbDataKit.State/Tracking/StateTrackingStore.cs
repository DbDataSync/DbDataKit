using System.Data;
using System.Data.Common;

namespace DbDataKit.State.Tracking;

/// <summary>A copy side that lives in a state database and can take rows inside a transaction the caller owns: what lets the follower move its checkpoint and write the rows as one step.</summary>
public interface IStateTrackingSink : ITrackingSink
{
    StateDatabase Database { get; }

    /// <summary>Writes the rows on the caller's connection and transaction (the same idempotent write as <see cref="ITrackingSink.WriteAsync"/>).</summary>
    void Write(DbConnection connection, DbTransaction transaction, string identity, TrackingStream stream, IReadOnlyList<TrackingRow> rows);
}

/// <summary>
/// The <c>tracking</c> module as a source and a sink (and so as a primary or a copy): rows of every stream and connection identity in one <c>tracking_row</c> table, every statement narrowed by the identity so
/// that a development copy carrying production's rows under production's name, but another identity, is a separate set of rows.
/// </summary>
public sealed class StateTrackingStore(StateDatabase database) : IStateTrackingSink
{
    private const string Module = TrackingModule.Name;
    private const string Live = "";

    public StateDatabase Database => database;

    // ---------- writing ----------

    public void Write(DbConnection connection, DbTransaction transaction, string identity, TrackingStream stream, IReadOnlyList<TrackingRow> rows)
    {
        var now = database.Time.GetUtcNow();
        var sql = database.Dialect.Upsert(
            "{table:tracking_row}", "connection_identity, stream, row_key, archive_id, order_pos, document, recorded_at", "$identity, $stream, $key, $archive, $order, $document, $now",
            "connection_identity, stream, row_key, archive_id", "order_pos = EXCLUDED.order_pos, document = EXCLUDED.document");
        foreach (var row in rows)
        {
            using var cmd = database.Command(Module, connection, transaction, sql);
            cmd.Bind(database, "identity", identity).Bind(database, "stream", stream.Name).Bind(database, "key", row.Key).Bind(database, "archive", Live)
                .Bind(database, "order", row.Order).Bind(database, "document", row.Document).Bind(database, "now", now);
            cmd.ExecuteNonQuery();
        }
    }

    public Task WriteAsync(string identity, TrackingStream stream, IReadOnlyList<TrackingRow> rows, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var transaction = database.BeginTransaction(connection, Module);
            Write(connection, transaction, identity, stream, rows);
            transaction.Commit();
        });
        return Task.CompletedTask;
    }

    // ---------- reading ----------

    private const string After = "AND (order_pos > $order OR (order_pos = $order AND row_key > $key))";

    public Task<IReadOnlyList<TrackingRow>> ReadAfterAsync(string identity, TrackingStream stream, TrackingPosition? after, int limit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(database.Retry<IReadOnlyList<TrackingRow>>(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection,
                $"SELECT row_key, order_pos, document FROM {{table:tracking_row}} WHERE connection_identity = $identity AND stream = $stream AND archive_id = $archive {(after is null ? "" : After)} ORDER BY order_pos, row_key {database.Limit("limit")};");
            cmd.Bind(database, "identity", identity).Bind(database, "stream", stream.Name).Bind(database, "archive", Live).Bind(database, "limit", limit);
            if (after is not null) cmd.Bind(database, "order", after.Order).Bind(database, "key", after.Key);
            var rows = new List<TrackingRow>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) rows.Add(new TrackingRow(reader.GetString(0), reader.Int64(1), reader.GetString(2)));
            return rows;
        }));
    }

    public async Task<TrackingPosition?> HighWaterMarkAsync(string identity, TrackingStream stream, CancellationToken cancellationToken) =>
        (await TailAsync(identity, stream, 1, cancellationToken)).LastOrDefault();

    public Task<IReadOnlyList<TrackingPosition>> TailAsync(string identity, TrackingStream stream, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(database.Retry<IReadOnlyList<TrackingPosition>>(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection,
                $"SELECT order_pos, row_key FROM {{table:tracking_row}} WHERE connection_identity = $identity AND stream = $stream AND archive_id = $archive ORDER BY order_pos DESC, row_key DESC {database.Limit("limit")};");
            cmd.Bind(database, "identity", identity).Bind(database, "stream", stream.Name).Bind(database, "archive", Live).Bind(database, "limit", count);
            var rows = new List<TrackingPosition>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) rows.Add(new TrackingPosition(reader.Int64(0), reader.GetString(1)));
            rows.Reverse();
            return rows;
        }));
    }

    // ---------- putting rows aside ----------

    public Task<int> ArchiveAfterAsync(string identity, TrackingStream stream, TrackingPosition? common, string marker, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(marker);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Change($"UPDATE {{table:tracking_row}} SET archive_id = $marker WHERE connection_identity = $identity AND stream = $stream AND archive_id = $archive {(common is null ? "" : After)};", identity, stream, common, marker));
    }

    public Task<int> DeleteAfterAsync(string identity, TrackingStream stream, TrackingPosition? common, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Change($"DELETE FROM {{table:tracking_row}} WHERE connection_identity = $identity AND stream = $stream AND archive_id = $archive {(common is null ? "" : After)};", identity, stream, common, null));
    }

    private int Change(string sql, string identity, TrackingStream stream, TrackingPosition? common, string? marker) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, sql);
            cmd.Bind(database, "identity", identity).Bind(database, "stream", stream.Name).Bind(database, "archive", Live);
            if (marker is not null) cmd.Bind(database, "marker", marker);
            if (common is not null) cmd.Bind(database, "order", common.Order).Bind(database, "key", common.Key);
            return cmd.ExecuteNonQuery();
        });

    /// <summary>Every connection identity that has a live row in the copy.</summary>
    public IReadOnlyList<string> Identities() =>
        database.Retry<IReadOnlyList<string>>(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "SELECT DISTINCT connection_identity FROM {table:tracking_row} WHERE archive_id = $archive ORDER BY connection_identity;");
            cmd.Bind(database, "archive", Live);
            var identities = new List<string>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) identities.Add(reader.GetString(0));
            return identities;
        });

    /// <summary>How many live rows the copy holds of this identity and stream.</summary>
    public int Count(string identity, string stream) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "SELECT COUNT(*) FROM {table:tracking_row} WHERE connection_identity = $identity AND stream = $stream AND archive_id = $archive;");
            cmd.Bind(database, "identity", identity).Bind(database, "stream", stream).Bind(database, "archive", Live);
            return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        });

    /// <summary>How many rows of this identity and stream were put aside under a marker (a count for the console and for tests).</summary>
    public int ArchivedCount(string identity, TrackingStream stream, string marker) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "SELECT COUNT(*) FROM {table:tracking_row} WHERE connection_identity = $identity AND stream = $stream AND archive_id = $marker;");
            cmd.Bind(database, "identity", identity).Bind(database, "stream", stream.Name).Bind(database, "marker", marker);
            return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        });
}

/// <summary>The divergences of the <c>tracking</c> module (<c>tracking_divergence</c>). A resolved divergence stays as a row; only the open one stops copying.</summary>
public sealed class StateTrackingDivergences(StateDatabase database) : ITrackingDivergenceStore
{
    private const string Module = TrackingModule.Name;
    private const string Columns = "id, connection_identity, stream, status, reason, common_order_pos, common_row_key, primary_order_pos, primary_row_key, copy_order_pos, copy_row_key, diverged_at, resolved_at, resolved_by";

    public TrackingDivergence? Open(string identity, string stream) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            return ReadOpen(connection, null, identity, stream);
        });

    private TrackingDivergence? ReadOpen(DbConnection connection, DbTransaction? transaction, string identity, string stream)
    {
        using var cmd = database.Command(Module, connection, transaction,
            $"SELECT {Columns} FROM {{table:tracking_divergence}} WHERE connection_identity = $identity AND stream = $stream AND status = $status ORDER BY id DESC {database.Limit("one")};");
        cmd.Bind(database, "identity", identity).Bind(database, "stream", stream).Bind(database, "status", TrackingDivergence.Diverged).Bind(database, "one", 1);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public TrackingDivergence Mark(string identity, string stream, string reason, TrackingPosition? common, TrackingPosition? primaryHigh, TrackingPosition? copyHigh) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var transaction = database.BeginTransaction(connection, Module);
            if (ReadOpen(connection, transaction, identity, stream) is { } open)
            {
                transaction.Rollback();
                return open;
            }

            var now = database.Time.GetUtcNow();
            var sql = "INSERT INTO {table:tracking_divergence} (connection_identity, stream, status, reason, common_order_pos, common_row_key, primary_order_pos, primary_row_key, copy_order_pos, copy_row_key, diverged_at) " +
                      "VALUES ($identity, $stream, $status, $reason, $c_order, $c_key, $p_order, $p_key, $s_order, $s_key, $now)";
            sql += database.Dialect.Engine == StateEngineIds.MsSql ? "; SELECT CAST(SCOPE_IDENTITY() AS BIGINT);" : " RETURNING id;";
            using var cmd = database.Command(Module, connection, transaction, sql);
            cmd.Bind(database, "identity", identity).Bind(database, "stream", stream).Bind(database, "status", TrackingDivergence.Diverged).Bind(database, "reason", reason).Bind(database, "now", now);
            BindPosition(cmd, "c", common);
            BindPosition(cmd, "p", primaryHigh);
            BindPosition(cmd, "s", copyHigh);
            var id = Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            transaction.Commit();
            return new TrackingDivergence(id, identity, stream, TrackingDivergence.Diverged, reason, common, primaryHigh, copyHigh, now, null, null);
        });

    private void BindPosition(DbCommand cmd, string prefix, TrackingPosition? position) =>
        cmd.Bind(database, $"{prefix}_order", position?.Order, DbType.Int64).Bind(database, $"{prefix}_key", position?.Key, DbType.String);

    public bool Resolve(long id, TrackingResolution resolution, string actor) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection,
                "UPDATE {table:tracking_divergence} SET status = $status, resolved_at = $now, resolved_by = $actor WHERE id = $id AND status = $open;");
            cmd.Bind(database, "status", resolution == TrackingResolution.Archive ? TrackingDivergence.Archived : TrackingDivergence.Reset).Bind(database, "now", database.Time.GetUtcNow())
                .Bind(database, "actor", actor).Bind(database, "id", id).Bind(database, "open", TrackingDivergence.Diverged);
            return cmd.ExecuteNonQuery() == 1;
        });

    public IReadOnlyList<TrackingDivergence> List(string? identity = null) =>
        database.Retry<IReadOnlyList<TrackingDivergence>>(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, $"SELECT {Columns} FROM {{table:tracking_divergence}} {(identity is null ? "" : "WHERE connection_identity = $identity")} ORDER BY id;");
            if (identity is not null) cmd.Bind(database, "identity", identity);
            var all = new List<TrackingDivergence>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) all.Add(Read(reader));
            return all;
        });

    private static TrackingDivergence Read(DbDataReader reader)
    {
        static TrackingPosition? Position(DbDataReader r, int order, int key) => r.IsDBNull(order) ? null : new TrackingPosition(r.Int64(order), r.IsDBNull(key) ? "" : r.GetString(key));
        return new TrackingDivergence(reader.Int64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            Position(reader, 5, 6), Position(reader, 7, 8), Position(reader, 9, 10), reader.Time(11), reader.NullableTime(12), reader.IsDBNull(13) ? null : reader.GetString(13));
    }
}
