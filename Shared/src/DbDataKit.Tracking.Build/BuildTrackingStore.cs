using System.Data.Common;
using DbDataKit.State;
using DbDataKit.State.Tracking;

namespace DbDataKit.Tracking.Build;

/// <summary>
/// The copy side of Build's tracking: <c>build_tracking.tracked_row</c> as a follower's sink (and as a source, for the comparison of the two sides). Every statement is narrowed by the identity. Row keys are compared in binary, so the order of rows that
/// share a time is the same here as on the target (<c>BuildTrackingSource</c> sorts them ordinally).
/// </summary>
public sealed class BuildTrackingStore(StateDatabase database) : IStateTrackingSink
{
    private const string Module = BuildTrackingModule.Name;
    private const string Live = "";

    public StateDatabase Database => database;

    private string Bin(string column) => database.Dialect.Engine switch
    {
        StateEngineIds.MsSql => $"{column} COLLATE Latin1_General_BIN2",
        StateEngineIds.Postgres => $"{column} COLLATE \"C\"",
        _ => column,
    };

    private static BuildStream Of(TrackingStream stream) => BuildStreams.Get(stream.Name);

    // ---------- writing ----------

    public void Write(DbConnection connection, DbTransaction transaction, string identity, TrackingStream stream, IReadOnlyList<TrackingRow> rows)
    {
        var kind = Of(stream);
        var now = database.Time.GetUtcNow();
        var sql = database.Dialect.Upsert(
            "{table:tracked_row}", "connection_identity, stream, row_key, archive_id, order_pos, occurred_at, row_hash, unsettled, document, recorded_at",
            "$identity, $stream, $key, $archive, $order, $occurred, $hash, $unsettled, $document, $now",
            "connection_identity, stream, row_key, archive_id", "order_pos = EXCLUDED.order_pos, occurred_at = EXCLUDED.occurred_at, row_hash = EXCLUDED.row_hash, unsettled = EXCLUDED.unsettled, document = EXCLUDED.document");
        foreach (var row in rows)
        {
            using var cmd = database.Command(Module, connection, transaction, sql);
            cmd.Bind(database, "identity", identity).Bind(database, "stream", stream.Name).Bind(database, "key", row.Key).Bind(database, "archive", Live)
                .Bind(database, "order", row.Order).Bind(database, "occurred", BuildStreams.OccurredAt(row.Order)).Bind(database, "hash", BuildStreams.RowHash(row.Document))
                .Bind(database, "unsettled", BuildStreams.IsUnsettled(kind, row.Document)).Bind(database, "document", row.Document).Bind(database, "now", now);
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

    private string After => $"AND (order_pos > $order OR (order_pos = $order AND {Bin("row_key")} > $key))";

    public Task<IReadOnlyList<TrackingRow>> ReadAfterAsync(string identity, TrackingStream stream, TrackingPosition? after, int limit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(database.Retry<IReadOnlyList<TrackingRow>>(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection,
                $"SELECT row_key, order_pos, document FROM {{table:tracked_row}} WHERE connection_identity = $identity AND stream = $stream AND archive_id = $archive {(after is null ? "" : After)} ORDER BY order_pos, {Bin("row_key")} {database.Limit("limit")};");
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
                $"SELECT order_pos, row_key FROM {{table:tracked_row}} WHERE connection_identity = $identity AND stream = $stream AND archive_id = $archive ORDER BY order_pos DESC, {Bin("row_key")} DESC {database.Limit("limit")};");
            cmd.Bind(database, "identity", identity).Bind(database, "stream", stream.Name).Bind(database, "archive", Live).Bind(database, "limit", count);
            var rows = new List<TrackingPosition>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) rows.Add(new TrackingPosition(reader.Int64(0), reader.GetString(1)));
            rows.Reverse();
            return rows;
        }));
    }

    // ---------- the rows that may still change ----------

    /// <summary>The keys of the newest rows of an updated stream that the copy holds as unsettled, with the hash each had when copied.</summary>
    public IReadOnlyList<(string Key, string Hash)> Unsettled(string identity, string stream, int limit) =>
        database.Retry<IReadOnlyList<(string, string)>>(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection,
                $"SELECT row_key, row_hash FROM {{table:tracked_row}} WHERE connection_identity = $identity AND stream = $stream AND archive_id = $archive AND unsettled = $yes ORDER BY order_pos DESC {database.Limit("limit")};");
            cmd.Bind(database, "identity", identity).Bind(database, "stream", stream).Bind(database, "archive", Live).Bind(database, "yes", true).Bind(database, "limit", limit);
            var rows = new List<(string, string)>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1)));
            return rows;
        });

    // ---------- reading the copy as documents ----------

    /// <summary>All live rows of a stream, in order (what a test or a rebuild compares with the target).</summary>
    public IReadOnlyList<TrackingRow> Rows(string identity, string stream) =>
        ReadAfterAsync(identity, new TrackingStream(stream, ["k", "o"], "o", "k"), null, int.MaxValue / 2, CancellationToken.None).GetAwaiter().GetResult();

    public int Count(string identity, string stream) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "SELECT COUNT(*) FROM {table:tracked_row} WHERE connection_identity = $identity AND stream = $stream AND archive_id = $archive;");
            cmd.Bind(database, "identity", identity).Bind(database, "stream", stream).Bind(database, "archive", Live);
            return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        });

    /// <summary>Replaces every live row of a snapshot (<c>object_info</c>, <c>metadata_dependency</c>) of this identity with <paramref name="rows"/>, in one transaction.</summary>
    public void ReplaceSnapshot(string identity, string table, IReadOnlyList<(string Key, long Order, string Document)> rows)
    {
        var stream = new TrackingStream(table, ["k", "o"], "o", "k");
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var transaction = database.BeginTransaction(connection, Module);
            using (var delete = database.Command(Module, connection, transaction, "DELETE FROM {table:tracked_row} WHERE connection_identity = $identity AND stream = $stream AND archive_id = $archive;"))
            {
                delete.Bind(database, "identity", identity).Bind(database, "stream", table).Bind(database, "archive", Live);
                delete.ExecuteNonQuery();
            }

            var now = database.Time.GetUtcNow();
            var sql = "INSERT INTO {table:tracked_row} (connection_identity, stream, row_key, archive_id, order_pos, occurred_at, row_hash, unsettled, document, recorded_at) VALUES ($identity, $stream, $key, $archive, $order, $occurred, $hash, $unsettled, $document, $now)";
            foreach (var (key, order, document) in rows)
            {
                using var cmd = database.Command(Module, connection, transaction, sql);
                cmd.Bind(database, "identity", identity).Bind(database, "stream", table).Bind(database, "key", key).Bind(database, "archive", Live).Bind(database, "order", order)
                    .Bind(database, "occurred", BuildStreams.OccurredAt(order)).Bind(database, "hash", BuildStreams.RowHash(document)).Bind(database, "unsettled", false).Bind(database, "document", document).Bind(database, "now", now);
                cmd.ExecuteNonQuery();
            }

            transaction.Commit();
        });
    }

    // ---------- putting rows aside ----------

    public Task<int> ArchiveAfterAsync(string identity, TrackingStream stream, TrackingPosition? common, string marker, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(marker);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Change($"UPDATE {{table:tracked_row}} SET archive_id = $marker WHERE connection_identity = $identity AND stream = $stream AND archive_id = $archive {(common is null ? "" : After)};", identity, stream, common, marker));
    }

    public Task<int> DeleteAfterAsync(string identity, TrackingStream stream, TrackingPosition? common, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Change($"DELETE FROM {{table:tracked_row}} WHERE connection_identity = $identity AND stream = $stream AND archive_id = $archive {(common is null ? "" : After)};", identity, stream, common, null));
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
}
