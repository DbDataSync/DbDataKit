using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DbDataKit.State.Snapshots;

/// <summary>
/// Is something using the database? (plan 29b decision M1: moving is offline, and the exporter refuses a database a live instance writes to.) Two checks, each only where it can apply:
/// <list type="bullet">
/// <item>The instance rows of the modules the database has: a <c>serving_instance</c> row of the <c>serving</c> module (the Proxy's) or an <c>instance</c> row of the <c>runner</c> module that is still
/// inside its lease. A row has a lease when the table has a <c>lease_expires_at</c> (or <c>lease_until</c>) column, else its <c>heartbeat_at</c> counts for <see cref="SnapshotOptions.InstanceLease"/>, else
/// (a table with neither) every row counts. Only modules in the database's layout are looked at, so a database without the Proxy's module is not asked about it.</item>
/// <item>SQLite: another process holding the file, found by taking an exclusive lock for a moment (waiting up to <see cref="SnapshotOptions.SqliteLockTimeout"/>).</item>
/// </list>
/// </summary>
internal static class SnapshotUseCheck
{
    private sealed record Rule(string Module, string Table);

    private static readonly Rule[] Rules = [new("serving", "serving_instance"), new("runner", "instance")];

    private static readonly string[] LeaseColumns = ["lease_expires_at", "lease_until"];

    public static async Task<IReadOnlyList<string>> FindInstancesAsync(StateDatabase database, SnapshotOptions options, CancellationToken ct)
    {
        var layout = database.Layout!;
        var found = new List<string>();
        var now = database.Time.GetUtcNow();
        foreach (var rule in Rules)
        {
            if (!layout.Has(rule.Module)) continue;
            var ns = layout.Namespace(rule.Module);
            if (!ns.Tables.ContainsKey(rule.Table)) continue;
            var table = StateTableDefinitions.ReadFor(ns).First(t => t.Name == rule.Table);
            var names = table.Columns.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
            var key = table.PrimaryKey[0];
            var host = names.Contains("host") ? "host" : null;
            var process = names.Contains("process_id") ? "process_id" : null;
            var lease = LeaseColumns.FirstOrDefault(names.Contains);
            var heartbeat = names.Contains("heartbeat_at") ? "heartbeat_at" : null;
            var clock = lease ?? heartbeat;

            var columns = new List<string> { key };
            if (host is not null) columns.Add(host);
            if (process is not null) columns.Add(process);
            if (clock is not null) columns.Add(clock);

            await using var connection = database.OpenConnection();
            await using var command = database.Command(connection, $"SELECT {string.Join(", ", columns)} FROM {{table:{rule.Module}.{rule.Table}}};");
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var at = 1;
                var hostName = host is null ? null : Convert.ToString(reader.GetValue(at++), CultureInfo.InvariantCulture);
                var processId = process is null ? (long?)null : Convert.ToInt64(reader.GetValue(at++), CultureInfo.InvariantCulture);
                DateTimeOffset? time = clock is null || reader.IsDBNull(at) ? null : StateValues.ToTime(reader.GetValue(at));
                var live = lease is not null ? time is { } until && until > now
                    : heartbeat is not null ? time is { } beat && now - beat < options.InstanceLease
                    : true;
                if (!live) continue;

                var who = $"the instance '{Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)}' ({ns.Module.Name}.{rule.Table}";
                if (processId is not null) who += $", process {processId}";
                if (hostName is not null) who += $" on {hostName}";
                if (time is { } t) who += lease is not null ? $", lease until {StateValues.FormatTime(t)}" : $", last heartbeat {StateValues.FormatTime(t)}";
                found.Add(who + ")");
            }
        }

        return found;
    }

    /// <summary>Null when the SQLite file is free (or the database is another engine), else a sentence saying it is held.</summary>
    public static string? FindHeldFile(StateDatabase database, SnapshotOptions options)
    {
        if (database.Dialect.Engine != StateEngineIds.Sqlite) return null;
        string? file = null;
        try
        {
            using var connection = database.OpenConnection();
            file = connection.DataSource;
            using var begin = connection.CreateCommand();
            begin.CommandText = "BEGIN EXCLUSIVE;";
            begin.CommandTimeout = Math.Max(1, (int)Math.Ceiling(options.SqliteLockTimeout.TotalSeconds));
            begin.ExecuteNonQuery();
            using var end = connection.CreateCommand();
            end.CommandText = "ROLLBACK;";
            end.ExecuteNonQuery();
            return null;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
        {
            return $"The SQLite file {(file is null ? "" : $"'{file}' ")}is held by another process (an exclusive lock could not be taken within {options.SqliteLockTimeout.TotalSeconds:0.#} s).";
        }
    }
}
