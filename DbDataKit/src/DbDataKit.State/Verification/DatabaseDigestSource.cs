using System.Data;
using System.Data.Common;
using System.Globalization;
using DbDataKit.State.Snapshots;

namespace DbDataKit.State.Verification;

/// <summary>
/// The digests of a live <see cref="StateDatabase"/>: every table of every module in its layout, read through the layout (so schemas, prefixes and SQLite files are resolved), in pages of
/// <see cref="VerifyOptions.BatchSize"/> rows by keyset (<c>WHERE key &gt; last ORDER BY key</c>), never a whole table. The rows' logical values and the order are the same on every engine
/// (see <c>SnapshotValues</c>), so a SQLite, a PostgreSQL and a SQL Server database holding the same data have the same digests.
/// <para>
/// Payload chunks (<c>payloads.chunk</c>) are compared by payload id, sequence and size here, and by the payload's stored SHA-256 in <c>payloads.payload</c> (a column of that table's rows); the bytes
/// themselves are read and hashed only with <see cref="VerifyOptions.RehashPayloads"/>.
/// </para>
/// </summary>
public sealed class DatabaseDigestSource : IRowDigestSource
{
    private readonly StateDatabase _database;
    private readonly string? _description;

    public DatabaseDigestSource(StateDatabase database, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (database.Layout is null)
            throw new InvalidOperationException("Only a state database built from modules can be verified: the older structure has no registry of module versions.");
        _database = database;
        _description = description;
    }

    public string Description => _description ?? $"the {_database.Dialect.Engine} state database";

    public IReadOnlyList<DigestModule> Modules() =>
        [.. _database.Layout!.Namespaces.Select(ns => new DigestModule(ns.Key, ns.Module.Name, ns.Module.Version, StateMigrator.Checksum(ns.Module, ns.Module.Version), [.. ns.Tables.Keys]))];

    public TableDigest Digest(string moduleKey, string table, VerifyOptions options, CancellationToken cancellationToken, Action<long>? onRows = null)
    {
        var query = Query(moduleKey, table, options);
        using var connection = _database.OpenConnection();
        var digest = new RowsDigest();
        long rows = 0;
        IReadOnlyList<object>? after = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = ReadPage(connection, query, after, query.Batch);
            foreach (var row in page) digest.Add(row.Hash);
            rows += page.Count;
            onRows?.Invoke(rows);
            if (page.Count < query.Batch) break;
            after = page[^1].Key;
        }

        return new TableDigest(rows, digest.Hex());
    }

    public IReadOnlyList<RowDigest> Rows(string moduleKey, string table, IReadOnlyList<object>? after, int take, VerifyOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var query = Query(moduleKey, table, options);
        using var connection = _database.OpenConnection();
        return ReadPage(connection, query, after, Math.Min(take, query.Batch));
    }

    // ---- queries

    private sealed record TableQuery(string Module, VerifiedTable Table, IReadOnlyList<string> ReadTypes, int[] KeyOrdinals, string First, string Next, int Batch, int TimeTicks);

    private TableQuery Query(string moduleKey, string table, VerifyOptions options)
    {
        var ns = _database.Layout!.Namespaces.FirstOrDefault(n => n.Key == moduleKey) ?? throw new InvalidOperationException($"{Description} has no module '{moduleKey}'.");
        if (!TableKeys.For(ns).TryGetValue(table, out var logical))
            throw new InvalidOperationException($"The module '{ns.Module.Name}' declares no table '{table}'.");
        var dialect = _database.Dialect;

        var types = new List<string>();
        var selects = new List<string>();
        var heavy = false;
        foreach (var column in logical.Columns)
        {
            var lengthOnly = !options.RehashPayloads && column.Type == SnapshotColumnTypes.Blob && ns.Module.Name == PayloadStore.ModuleName && table == "chunk"
                && TableKeys.BlobLength(dialect, column.Name) is not null;
            if (lengthOnly)
            {
                // the size stands for the bytes; the payload's own stored hash is a column of the payload table
                selects.Add($"{TableKeys.BlobLength(dialect, column.Name)} AS {column.Name}");
                types.Add(SnapshotColumnTypes.Int);
            }
            else
            {
                selects.Add(column.Name);
                types.Add(column.Type);
                heavy |= column.Type == SnapshotColumnTypes.Blob;
            }
        }

        var expressions = logical.Key.Select(k => TableKeys.OrderExpression(dialect, k)).ToList();
        var order = string.Join(", ", expressions);
        var ordinals = logical.Key.Select(k => logical.Columns.ToList().IndexOf(k)).ToArray();

        // (a > $k0) OR (a = $k0 AND b > $k1) OR ...
        var terms = new List<string>();
        for (var i = 0; i < expressions.Count; i++)
        {
            var equal = Enumerable.Range(0, i).Select(j => $"{expressions[j]} = $k{j}");
            terms.Add("(" + string.Join(" AND ", equal.Append($"{expressions[i]} > $k{i}")) + ")");
        }

        var select = $"SELECT {string.Join(", ", selects)} FROM {{table:{table}}}";
        var limit = _database.Limit("take");
        return new TableQuery(
            ns.Module.Name, logical, types, ordinals,
            $"{select} ORDER BY {order} {limit};",
            $"{select} WHERE {string.Join(" OR ", terms)} ORDER BY {order} {limit};",
            heavy ? Math.Min(options.BatchSize, options.BlobBatchSize) : options.BatchSize, options.TimeTicks);
    }

    private List<RowDigest> ReadPage(DbConnection connection, TableQuery query, IReadOnlyList<object>? after, int take)
    {
        using var command = _database.Command(query.Module, connection, after is null ? query.First : query.Next);
        if (after is not null)
        {
            for (var i = 0; i < query.Table.Key.Count; i++)
                BindKey(command, i, query.Table.Key[i], after[i]);
        }

        command.Bind(_database, "take", (long)take);

        var rows = new List<RowDigest>();
        using var reader = command.ExecuteReader();
        var width = query.ReadTypes.Count;
        while (reader.Read())
        {
            var raw = new object?[width];
            for (var i = 0; i < width; i++)
            {
                raw[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                if (query.TimeTicks > 0 && raw[i] is not null && query.ReadTypes[i] == SnapshotColumnTypes.Time)
                {
                    var time = StateValues.ToTime(raw[i]!);
                    raw[i] = new DateTimeOffset(time.UtcTicks - time.UtcTicks % query.TimeTicks, TimeSpan.Zero);
                }
            }

            var key = TableKeys.KeyOf(query.Table.Key, [.. query.KeyOrdinals.Select(o => raw[o])]);
            rows.Add(new RowDigest(key, TableKeys.KeyText(key), SnapshotValues.HashRow(query.ReadTypes, raw)));
        }

        return rows;
    }

    /// <summary>Binds the last key's value the way the order expression compares it: uuids and text as text, ints as numbers.</summary>
    private void BindKey(DbCommand command, int index, SnapshotColumn column, object value)
    {
        switch (column.Type)
        {
            case SnapshotColumnTypes.Int:
                command.Bind(_database, $"k{index}", (long)value, DbType.Int64);
                break;
            case SnapshotColumnTypes.Time:
                command.Bind(_database, $"k{index}", DateTimeOffset.ParseExact((string)value, "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal));
                break;
            case SnapshotColumnTypes.Bool:
                command.Bind(_database, $"k{index}", (string)value == "true");
                break;
            default:
                command.Bind(_database, $"k{index}", (string)value, DbType.String);
                break;
        }
    }
}
