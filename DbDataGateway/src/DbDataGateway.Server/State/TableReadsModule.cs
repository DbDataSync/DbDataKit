using System.Collections.Concurrent;
using System.Data.Common;
using DbDataKit.State;
using DbDataGateway.Server.Data;

namespace DbDataGateway.Server.State;

/// <summary>
/// The <c>table_reads</c> module (plan 18, G08): what the Gateway's lineage says about the use of a table, as a <em>summary</em> and never a record per query: for each data source, table and reader, how many statements named the table and
/// when the last one did. A per-query record belongs to the activity of the Gateway (plan 13a P09) and its own retention. Kept per group, like the data sources a statement runs against; a row whose last read is older than
/// <see cref="RetentionDays"/> days is removed by the shared retention job (the retention of run detail, decision 1 of the lineage doc), so a table nobody reads any more drops out of the summary. Migrations are appended, never edited.
/// </summary>
public static class TableReadsModule
{
    public const string Name = "table_reads";

    public const double RetentionDays = SemanticRunsModule.RetentionDays;

    public static StateModule Module { get; } = new(
        Name,
        StateScopeKind.Group,
        [
            """
            CREATE TABLE {table:table_read} (
                data_source    {{key}} NOT NULL,
                schema_name    {{key}} NOT NULL,
                table_name     {{key}} NOT NULL,
                reader         {{key}} NOT NULL,
                read_count     {{int}} NOT NULL,
                last_read_at   {{time}} NOT NULL,
                PRIMARY KEY (data_source, schema_name, table_name, reader)
            );
            CREATE INDEX {index:table_read_by_time} ON {table:table_read}(last_read_at);
            """,
        ],
        retention: [new RetentionRule("table_read", "last_read_at", RetentionDays)]);
}

/// <summary>What the Gateway has counted of one table on one data source: how many statements read it, when the last one did, and who (<see cref="Readers"/>, the names as the Gateway knows its callers).</summary>
public sealed record TableReadSummary(string DataSource, string? Schema, string Table, long Reads, DateTimeOffset LastRead, IReadOnlyList<string> Readers);

/// <summary>The summaries of <see cref="TableReadsModule"/>, on any engine of the state.</summary>
public sealed class TableReadStore(StateDatabase database)
{
    private const string Module = TableReadsModule.Name;

    /// <summary>Adds <paramref name="reads"/> statements to a table's count for one reader and moves the last read forward (never back).</summary>
    public void Add(string dataSource, string? schema, string table, string reader, long reads, DateTimeOffset at) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            if (!Update(connection, transaction, dataSource, schema ?? "", table, reader, reads, at))
            {
                try
                {
                    using var insert = database.Command(Module, connection, transaction,
                        "INSERT INTO {table:table_read} (data_source, schema_name, table_name, reader, read_count, last_read_at) VALUES ($source, $schema, $table, $reader, $count, $at);");
                    insert.Bind(database, "source", dataSource).Bind(database, "schema", schema ?? "").Bind(database, "table", table).Bind(database, "reader", reader).Bind(database, "count", reads).Bind(database, "at", at);
                    insert.ExecuteNonQuery();
                }
                catch (DbException)
                {
                    // another instance of the group wrote the row between the update and the insert: count on top of it
                    transaction.Rollback();
                    using var again = connection.BeginTransaction();
                    if (!Update(connection, again, dataSource, schema ?? "", table, reader, reads, at)) throw;
                    again.Commit();
                    return;
                }
            }
            transaction.Commit();
        });

    private bool Update(DbConnection connection, DbTransaction transaction, string dataSource, string schema, string table, string reader, long reads, DateTimeOffset at)
    {
        using var update = database.Command(Module, connection, transaction,
            "UPDATE {table:table_read} SET read_count = read_count + $count, last_read_at = CASE WHEN last_read_at < $at THEN $at ELSE last_read_at END WHERE data_source = $source AND schema_name = $schema AND table_name = $table AND reader = $reader;");
        update.Bind(database, "source", dataSource).Bind(database, "schema", schema).Bind(database, "table", table).Bind(database, "reader", reader).Bind(database, "count", reads).Bind(database, "at", at);
        return update.ExecuteNonQuery() > 0;
    }

    /// <summary>One summary per table: the sum over its readers, the newest read and the readers by name.</summary>
    public IReadOnlyList<TableReadSummary> Summaries() =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var command = database.Command(Module, connection, "SELECT data_source, schema_name, table_name, reader, read_count, last_read_at FROM {table:table_read} ORDER BY data_source, schema_name, table_name, reader;");
            using var reader = command.ExecuteReader();
            var rows = new List<(string Source, string Schema, string Table, string Reader, long Count, DateTimeOffset At)>();
            while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.Int64(4), reader.Time(5)));
            return (IReadOnlyList<TableReadSummary>)[.. rows.GroupBy(r => (r.Source, r.Schema, r.Table))
                .Select(g => new TableReadSummary(g.Key.Source, g.Key.Schema.Length == 0 ? null : g.Key.Schema, g.Key.Table, g.Sum(r => r.Count), g.Max(r => r.At), [.. g.Select(r => r.Reader).Order(StringComparer.Ordinal)]))];
        });
}

/// <summary>
/// Counts the tables statements read (plan 18, G08) in memory and writes the sums to <see cref="TableReadStore"/> when <see cref="Flush"/> is called (a timer of the host does, and when the lineage is asked), so a statement costs no
/// write of its own. A statement that names no table the scanner can find counts nowhere. Never throws into a query: a failed write keeps the sums for the next flush.
/// </summary>
public sealed class TableReadRecorder(TableReadStore store, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private Dictionary<(string Source, string Schema, string Table, string Reader), (long Count, DateTimeOffset At)> _pending = [];

    /// <summary>Counts the tables <paramref name="sql"/> reads on <paramref name="dataSource"/> for <paramref name="reader"/>.</summary>
    public void Record(string dataSource, string? sql, string reader)
    {
        foreach (var table in SqlTableReferences.Find(sql)) Record(dataSource, table.Schema, table.Table, reader);
    }

    public void Record(string dataSource, string? schema, string table, string reader)
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            var key = (dataSource, schema ?? "", table, reader);
            _pending[key] = _pending.TryGetValue(key, out var known) ? (known.Count + 1, now) : (1, now);
        }
    }

    /// <summary>Writes what was counted since the last flush. False when a write failed (what was not written stays counted).</summary>
    public bool Flush()
    {
        Dictionary<(string Source, string Schema, string Table, string Reader), (long Count, DateTimeOffset At)> taken;
        lock (_gate) { taken = _pending; _pending = []; }
        var ok = true;
        foreach (var (key, value) in taken)
        {
            try { store.Add(key.Source, key.Schema.Length == 0 ? null : key.Schema, key.Table, key.Reader, value.Count, value.At); }
            catch (Exception ex) when (ex is DbException or InvalidOperationException or IOException)
            {
                ok = false;
                lock (_gate) _pending[key] = _pending.TryGetValue(key, out var later) ? (later.Count + value.Count, later.At) : value;
            }
        }
        return ok;
    }
}

/// <summary>Writes the counted reads every few seconds, and once more when the Gateway stops.</summary>
public sealed class TableReadFlushService(TableReadRecorder recorder) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try { while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) recorder.Flush(); }
        catch (OperationCanceledException) { /* stopping */ }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        recorder.Flush();
    }
}
