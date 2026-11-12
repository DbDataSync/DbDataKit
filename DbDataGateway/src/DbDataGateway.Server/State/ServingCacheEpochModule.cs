using System.Data.Common;
using DbDataKit.Serving;
using DbDataKit.State;

namespace DbDataGateway.Server.State;

/// <summary>
/// The <c>serving_cache_epoch</c> module (plan 24a UC14; QU6 as settled by the owner default of 2026-11-12): the group's shared record of <em>which datasets changed and when</em>, and nothing else. One row per cache dataset holds a number, its
/// <c>epoch</c>, that only grows; every instance of the group keeps its result cache in memory and compares the epochs it reads here with the ones it has seen, so a <c>data.changed</c> message that reached one instance clears all of them
/// within one poll. <b>No row data and no result is ever stored here</b> (a cache entry is a copy of rows the caller may have been allowed to see; the group's state database is not where those belong), so a lost or unreachable module costs
/// nothing but the sharing: each instance falls back to its time-to-live. Kept per group. A row that was not touched for <see cref="RetentionDays"/> days is removed by the shared retention job; epochs are microseconds since 1970
/// (<see cref="ServingCacheEpochs.Next"/>), so a row made again never repeats a number an instance has seen. Migrations are appended, never edited.
/// </summary>
public static class ServingCacheEpochModule
{
    public const string Name = "serving_cache_epoch";

    /// <summary>Longer than the longest time-to-live a definition may ask for (24 hours), so an epoch outlives every entry it could clear.</summary>
    public const double RetentionDays = 3;

    public static StateModule Module { get; } = new(
        Name,
        StateScopeKind.Group,
        [
            """
            CREATE TABLE {table:cache_epoch} (
                dataset      {{key}} NOT NULL,
                epoch        {{int}} NOT NULL,
                changed_at   {{time}} NOT NULL,
                product      {{key}} NOT NULL,
                reason       {{key}} NOT NULL,
                PRIMARY KEY (dataset)
            );
            CREATE INDEX {index:cache_epoch_by_time} ON {table:cache_epoch}(changed_at);
            """,
        ],
        retention: [new RetentionRule("cache_epoch", "changed_at", RetentionDays)]);
}

/// <summary>The epochs of <see cref="ServingCacheEpochModule"/>, on any engine of the state. A name longer than a key column is cut (a dataset identity is at most 512 characters).</summary>
public sealed class ServingCacheEpochStore(StateDatabase database, TimeProvider? time = null) : IServingCacheEpochs
{
    private const string Module = ServingCacheEpochModule.Name;
    private const int KeyLength = 400;   // under what SQL Server can index (450 characters)
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public long Bump(string dataset, string product, string reason) =>
        database.Retry(() =>
        {
            dataset = Cut(dataset);
            using var connection = database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            var now = _time.GetUtcNow();
            var epoch = ServingCacheEpochs.Next(Current(connection, transaction, dataset) ?? 0, now);
            if (!Update(connection, transaction, dataset, epoch, now, product, reason))
            {
                try
                {
                    using var insert = database.Command(Module, connection, transaction,
                        "INSERT INTO {table:cache_epoch} (dataset, epoch, changed_at, product, reason) VALUES ($dataset, $epoch, $at, $product, $reason);");
                    insert.Bind(database, "dataset", dataset).Bind(database, "epoch", epoch).Bind(database, "at", now).Bind(database, "product", Cut(product)).Bind(database, "reason", Cut(reason));
                    insert.ExecuteNonQuery();
                }
                catch (DbException)
                {
                    // another instance wrote the row between the read and the insert: move its epoch forward
                    transaction.Rollback();
                    using var again = connection.BeginTransaction();
                    epoch = ServingCacheEpochs.Next(Current(connection, again, dataset) ?? 0, now);
                    if (!Update(connection, again, dataset, epoch, now, product, reason)) throw;
                    again.Commit();
                    return epoch;
                }
            }

            transaction.Commit();
            return epoch;
        });

    private long? Current(DbConnection connection, DbTransaction transaction, string dataset)
    {
        using var command = database.Command(Module, connection, transaction, "SELECT epoch FROM {table:cache_epoch} WHERE dataset = $dataset;");
        command.Bind(database, "dataset", dataset);
        using var reader = command.ExecuteReader();
        return reader.Read() ? reader.Int64(0) : null;
    }

    private bool Update(DbConnection connection, DbTransaction transaction, string dataset, long epoch, DateTimeOffset at, string product, string reason)
    {
        using var update = database.Command(Module, connection, transaction,
            "UPDATE {table:cache_epoch} SET epoch = $epoch, changed_at = $at, product = $product, reason = $reason WHERE dataset = $dataset AND epoch < $epoch;");
        update.Bind(database, "dataset", dataset).Bind(database, "epoch", epoch).Bind(database, "at", at).Bind(database, "product", Cut(product)).Bind(database, "reason", Cut(reason));
        return update.ExecuteNonQuery() > 0;
    }

    public IReadOnlyDictionary<string, long> Read() =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var command = database.Command(Module, connection, "SELECT dataset, epoch FROM {table:cache_epoch};");
            using var reader = command.ExecuteReader();
            var rows = new Dictionary<string, long>(StringComparer.Ordinal);
            while (reader.Read()) rows[reader.GetString(0)] = reader.Int64(1);
            return (IReadOnlyDictionary<string, long>)rows;
        });

    /// <summary>The last change of each dataset, for the console: when, who and why.</summary>
    public IReadOnlyList<(string Dataset, long Epoch, DateTimeOffset At, string Product, string Reason)> Recent(int limit = 50) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var command = database.Command(Module, connection, "SELECT dataset, epoch, changed_at, product, reason FROM {table:cache_epoch} ORDER BY changed_at DESC;");
            using var reader = command.ExecuteReader();
            var rows = new List<(string, long, DateTimeOffset, string, string)>();
            while (reader.Read() && rows.Count < limit) rows.Add((reader.GetString(0), reader.Int64(1), reader.Time(2), reader.GetString(3), reader.GetString(4)));
            return (IReadOnlyList<(string, long, DateTimeOffset, string, string)>)rows;
        });

    private static string Cut(string text) => text.Length <= KeyLength ? text : text[..KeyLength];
}
