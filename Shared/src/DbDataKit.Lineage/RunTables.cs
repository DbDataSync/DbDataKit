using DbDataKit.Connections;

namespace DbDataKit.Lineage;

/// <summary>A table a run read or wrote, by the connection definition it was reached through (with the database the run named, when it is not the connection's own).</summary>
public sealed record RunTable(ConnectionDefinition Connection, string? Schema, string Table);

/// <summary>
/// The tables a run of any product touched, as the lines the Runner Kit's <c>run.Lineage(reads, writes)</c> takes (plan 24a UC13, plan 18): each is the dataset identity <see cref="LineageIdentity.Dataset"/> gives, the name a Gateway's
/// graph holds, so the Runner's <c>data.changed</c> producer sends it and the Runner's lineage keeps it. Shared by Sync's drain and Build's runtime, so a table one writes and the other reads is one line.
/// </summary>
public static class RunTables
{
    /// <summary>The dataset lines of <paramref name="tables"/>, each once, in the order first named. A table with no name is left out.</summary>
    public static IReadOnlyList<string> Datasets(IEnumerable<RunTable> tables)
    {
        ArgumentNullException.ThrowIfNull(tables);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var lines = new List<string>();
        foreach (var table in tables)
        {
            if (table is null || string.IsNullOrWhiteSpace(table.Table)) continue;
            var line = LineageIdentity.Dataset(table.Connection, table.Schema, table.Table).ToString();
            if (seen.Add(line)) lines.Add(line);
        }

        return lines;
    }
}
