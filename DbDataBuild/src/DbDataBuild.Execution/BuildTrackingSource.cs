using System.Data;
using DbDataBuild.State;
using DbDataKit.State.Tracking;

namespace DbDataBuild.Execution;

/// <summary>
/// Build's tracking tables on the target as the primary side of a copy (plan 28c E16): each <see cref="BuildStream"/> is read after a position <c>(ticks of its time column, row key)</c>, for one identity (the data connection's records in one tracking
/// store). Reads go through the read session only.
/// <para>
/// Rows are read in batches and ordered by time in SQL, then by key <b>ordinally</b> here, so the order does not depend on a collation; a batch never ends inside a group of rows that share a time (it is read again larger), because the position is a
/// pair and the rows of one time must come in the one order. Rows newer than <paramref name="settleLag"/> are not offered yet: two Build processes write with their own clocks, and a row that commits late with an older time than one already copied
/// would otherwise be passed by the checkpoint for good. Divergence checks (<see cref="TailAsync"/>) look at everything.
/// </para>
/// </summary>
public sealed class BuildTrackingSource(ReadSession read, TrackingScope scope, string identity, TimeSpan settleLag, TimeProvider? clock = null) : ITrackingSource
{
    public string Identity => identity;

    private string C(string name) => TrackingDdl.For(scope.Engine).Quote(name);

    private string Table(string table) => $"{C(scope.SchemaName)}.{C(table)}";

    /// <summary>The layout record (<c>tracking_version</c>) is the store's own and has no connection column: every identity of the store sees the same rows.</summary>
    private GateParameter[] ConnectionOf(BuildStream stream) => stream.Table == "tracking_version" ? [] : [new("connection", DbType.String, scope.Connection)];

    private string ConnectionWhere(BuildStream stream) => stream.Table == "tracking_version" ? "1 = 1" : $"t.{C("connection")} = @connection";

    private static DateTime Ticks(long order) => BuildStreams.TimeOf(order);

    /// <summary>The SELECT of a stream: the document columns, then the ordering time, with its FROM and WHERE (the connection, a time to start at, and not before the settle time).</summary>
    private (string Select, string From, string Where, string Order) Parts(BuildStream stream)
    {
        var columns = string.Join(", ", stream.DocumentColumns.Select(c => $"t.{C(c)}"));
        if (stream.Table == "refresh_plan_member")
        {
            // a plan's members are written after its first migration_log row, so the row's time is that row's
            var from = $"{Table(stream.Table)} t JOIN (SELECT {C("plan_id")}, MIN({C("applied_utc")}) AS ord FROM {Table("migration_log")} WHERE {C("connection")} = @connection GROUP BY {C("plan_id")}) m ON m.{C("plan_id")} = t.{C("plan_id")}";
            return ($"{columns}, m.ord", from, ConnectionWhere(stream), "m.ord");
        }

        var order = $"t.{C(stream.OrderColumn)}";
        return ($"{columns}, {order}", $"{Table(stream.Table)} t", $"{ConnectionWhere(stream)} AND {order} IS NOT NULL", order);
    }

    private (string Top, string Limit) RowLimit(int n) => scope.Engine == "postgres" ? ("", $" LIMIT {n}") : ($"TOP ({n}) ", "");

    private async Task<List<(long Order, string Key, string Document)>> FetchAsync(BuildStream stream, long? orderAtLeast, DateTime? orderAtMost, bool newestFirst, int n, CancellationToken ct)
    {
        var (select, from, where, order) = Parts(stream);
        var parameters = new List<GateParameter>(ConnectionOf(stream));
        if (orderAtLeast is { } least) { where += $" AND {order} >= @least"; parameters.Add(new("least", DbType.DateTime2, Ticks(least))); }
        if (orderAtMost is { } most) { where += $" AND {order} <= @most"; parameters.Add(new("most", DbType.DateTime2, most)); }
        var (top, limit) = RowLimit(n);
        var rows = await read.QueryAsync($"SELECT {top}{select} FROM {from} WHERE {where} ORDER BY {order}{(newestFirst ? " DESC" : "")}{limit}", parameters, ct).ConfigureAwait(false);
        var result = new List<(long, string, string)>(rows.Count);
        foreach (var row in rows)
        {
            var values = row.Take(stream.DocumentColumns.Count).ToList();
            result.Add((BuildStreams.Order(row[stream.DocumentColumns.Count]), KeyOf(stream, stream.DocumentColumns, values), BuildStreams.Document(stream, values)));
        }

        return result;
    }

    private static string KeyOf(BuildStream stream, IReadOnlyList<string> columns, IReadOnlyList<object?> values) =>
        string.Join('|', stream.KeyColumns.Select(k => BuildStreams.KeyPart(stream, k, values[columns.ToList().IndexOf(k)])));

    private static int Compare((long Order, string Key, string Document) a, (long Order, string Key, string Document) b) =>
        a.Order != b.Order ? a.Order.CompareTo(b.Order) : string.CompareOrdinal(a.Key, b.Key);

    public async Task<IReadOnlyList<TrackingRow>> ReadAfterAsync(string id, TrackingStream stream, TrackingPosition? after, int limit, CancellationToken cancellationToken)
    {
        if (id != identity) return [];
        var kind = BuildStreams.Get(stream.Name);
        var cutoff = DateTime.SpecifyKind((clock ?? TimeProvider.System).GetUtcNow().UtcDateTime - settleLag, DateTimeKind.Unspecified);     // the columns are zone-less: Npgsql refuses a UTC-kind value for them
        var ties = 0;
        if (after is not null)
        {
            var (_, from, where, order) = Parts(kind);
            var count = await read.QueryAsync($"SELECT COUNT(*) FROM {from} WHERE {where} AND {order} = @at", [.. ConnectionOf(kind), new GateParameter("at", DbType.DateTime2, Ticks(after.Order))], cancellationToken).ConfigureAwait(false);
            ties = Convert.ToInt32(count[0][0], System.Globalization.CultureInfo.InvariantCulture);
        }

        for (var n = limit + ties + 1; ; n = checked(n * 2))
        {
            var fetched = await FetchAsync(kind, after?.Order, cutoff, newestFirst: false, n, cancellationToken).ConfigureAwait(false);
            var complete = fetched.Count < n;
            if (!complete)
            {
                var last = fetched[^1].Order;
                fetched = [.. fetched.Where(r => r.Order != last)];      // the group at the cut may be missing rows: it is read whole next time
            }

            var later = fetched.Where(r => after is null || r.Order > after.Order || (r.Order == after.Order && string.CompareOrdinal(r.Key, after.Key) > 0)).ToList();
            later.Sort(Compare);
            if (complete || later.Count >= limit) return [.. later.Take(limit).Select(r => new TrackingRow(r.Key, r.Order, r.Document))];
        }
    }

    public async Task<TrackingPosition?> HighWaterMarkAsync(string id, TrackingStream stream, CancellationToken cancellationToken) =>
        (await TailAsync(id, stream, 1, cancellationToken).ConfigureAwait(false)).LastOrDefault();

    public async Task<IReadOnlyList<TrackingPosition>> TailAsync(string id, TrackingStream stream, int count, CancellationToken cancellationToken)
    {
        if (id != identity) return [];
        var kind = BuildStreams.Get(stream.Name);
        for (var n = count * 2 + 8; ; n = checked(n * 2))
        {
            var fetched = await FetchAsync(kind, null, null, newestFirst: true, n, cancellationToken).ConfigureAwait(false);
            fetched.Sort(Compare);
            var tail = fetched.Count > count ? fetched[^count..] : fetched;
            // the oldest group fetched may be cut: if the tail reaches into it, read more
            if (fetched.Count < n || tail[0].Order > fetched[0].Order)
                return [.. tail.Select(r => new TrackingPosition(r.Order, r.Key))];
        }
    }

    /// <summary>The row of an updated stream with this key as a document, or null when the target no longer has it.</summary>
    public async Task<string?> ReadDocumentAsync(string stream, string key, CancellationToken cancellationToken)
    {
        var kind = BuildStreams.Get(stream);
        if (kind.Kind != BuildStreamKind.Updated) throw new ArgumentException($"'{stream}' is an append stream: its rows do not change.", nameof(stream));
        var parts = key.Split('|', kind.KeyColumns.Count);
        if (!Guid.TryParse(parts[0], out var first)) return null;
        var parameters = new List<GateParameter>(ConnectionOf(kind)) { new("first", DbType.Guid, first) };
        var where = $"t.{C("connection")} = @connection AND t.{C(kind.KeyColumns[0])} = @first";
        if (kind.KeyColumns.Count > 1) { where += $" AND t.{C(kind.KeyColumns[1])} = @second"; parameters.Add(new("second", DbType.AnsiString, parts[1])); }
        var rows = await read.QueryAsync($"SELECT {string.Join(", ", kind.DocumentColumns.Select(c => $"t.{C(c)}"))} FROM {Table(kind.Table)} t WHERE {where}", parameters, cancellationToken).ConfigureAwait(false);
        return rows.Count == 0 ? null : BuildStreams.Document(kind, [.. rows[0]]);
    }
}
