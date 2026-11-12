using System.Security.Cryptography;
using System.Text;

namespace DbDataKit.State;

/// <summary>One thing a task reported it read or wrote, as kept.</summary>
/// <param name="Direction"><c>reads</c> or <c>writes</c>.</param>
/// <param name="Name">A lineage identity for a dataset, or <c>external://&lt;kind&gt;/&lt;name&gt;</c> for an outside system; free text otherwise (the task's own name for a table).</param>
public sealed record TaskLineageRow(string Package, string Channel, string Task, string Direction, string Name, Guid? LastRun, DateTimeOffset LastSeenAt, int Seen)
{
    public const string Reads = "reads";
    public const string Writes = "writes";
}

/// <summary>
/// What tasks reported they read and wrote (plan 27a IX12), in the <c>runner</c> module's <c>task_lineage</c> table: <see cref="Record"/> after a run, <see cref="Read"/> for the lineage route. One row per package, channel, task, direction and name, so a
/// rerun adds nothing but a newer time and a count. The names are what the Kit's <c>run.Lineage(reads, writes)</c> was given; nothing else of a run is kept here.
/// </summary>
public sealed class TaskLineageStore(StateDatabase database)
{
    private const string Module = RunnerModule.Name;

    /// <summary>The longest name kept; a longer one is cut (a name is an identity, not a statement).</summary>
    public const int MaxNameLength = 512;

    /// <summary>The most distinct names one run adds (a task that reports a name per row would otherwise fill the table).</summary>
    public const int MaxNamesPerRun = 200;

    internal static string HashOf(string name) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name)));

    /// <summary>Records what one run reported: the reads and the writes of every line it sent, each name once.</summary>
    public void Record(string package, string channel, string task, Guid run, IEnumerable<string> reads, IEnumerable<string> writes, DateTimeOffset? at = null)
    {
        var now = at ?? database.Time.GetUtcNow();
        var rows = reads.Select(n => (TaskLineageRow.Reads, n)).Concat(writes.Select(n => (TaskLineageRow.Writes, n)))
            .Select(r => (r.Item1, Name: Clean(r.n)))
            .Where(r => r.Name.Length > 0)
            .Distinct()
            .Take(MaxNamesPerRun)
            .ToList();
        if (rows.Count == 0) return;
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var transaction = database.BeginTransaction(connection, Module);
            foreach (var (direction, name) in rows)
            {
                using var cmd = database.Command(Module, connection, transaction, database.Dialect.Upsert("{table:task_lineage}", "package, channel, task, direction, name_hash, name, last_run, last_seen_at, seen",
                    "$package, $channel, $task, $direction, $hash, $name, $run, $at, 1", "package, channel, task, direction, name_hash",
                    "last_run = EXCLUDED.last_run, last_seen_at = EXCLUDED.last_seen_at, seen = EXISTING.seen + 1"));
                cmd.Bind(database, "package", package).Bind(database, "channel", channel).Bind(database, "task", task).Bind(database, "direction", direction)
                    .Bind(database, "hash", HashOf(name)).Bind(database, "name", name).Bind(database, "run", run).Bind(database, "at", now);
                cmd.ExecuteNonQuery();
            }

            transaction.Commit();
        });
    }

    private static string Clean(string? name)
    {
        var text = (name ?? "").Trim();
        if (text.Any(char.IsControl)) text = new string(text.Where(c => !char.IsControl(c)).ToArray());
        return text.Length > MaxNameLength ? text[..MaxNameLength] : text;
    }

    /// <summary>Everything kept, oldest package first.</summary>
    public IReadOnlyList<TaskLineageRow> Read() =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "SELECT package, channel, task, direction, name, last_run, last_seen_at, seen FROM {table:task_lineage} ORDER BY package, task, direction, name;");
            using var reader = cmd.ExecuteReader();
            var rows = new List<TaskLineageRow>();
            while (reader.Read())
                rows.Add(new TaskLineageRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    reader.NullableUuid(5), reader.Time(6), (int)reader.Int64(7)));
            return (IReadOnlyList<TaskLineageRow>)rows;
        });
}
