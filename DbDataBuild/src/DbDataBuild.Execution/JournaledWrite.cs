namespace DbDataBuild.Execution;

/// <summary>
/// A gate for one tracking write: <c>ExecuteAsync</c> records the row with the scope's journal first (when it has one), then runs the statement. The writers in <see cref="AuditLog"/> name the table (and for an update the key columns) once, at the
/// head of the write, and the rest of the method stays as it was.
/// </summary>
internal readonly struct JournaledGate(MutationGate gate, TrackingScope scope, string table, string[]? keyColumns = null, IReadOnlyDictionary<string, string>? renames = null, IReadOnlyDictionary<string, object?>? literals = null)
{
    public Task ExecuteAsync(GateStatement statement, CancellationToken ct = default) =>
        keyColumns is null ? JournaledWrite.InsertAsync(gate, scope, table, statement, renames, literals, ct) : JournaledWrite.UpdateAsync(gate, scope, table, statement, keyColumns, renames, ct);
}

/// <summary>
/// A tracking write that goes to the state first (plan 28c E16): when the scope has a journal (the project's tracking has the state as its primary), the row is handed to the journal, which writes it to the state, and only then
/// does the statement run on the target, the second copy. A dry run writes nothing anywhere. A journal that fails stops the write before the target is touched, so the target never holds a row the state lacks.
/// </summary>
internal static class JournaledWrite
{
    /// <param name="renames">Parameter name to column name, where they differ.</param>
    /// <param name="literals">Columns the statement writes as SQL literals rather than parameters.</param>
    public static async Task InsertAsync(MutationGate gate, TrackingScope scope, string table, GateStatement statement, IReadOnlyDictionary<string, string>? renames = null, IReadOnlyDictionary<string, object?>? literals = null, CancellationToken ct = default)
    {
        if (scope.Journal is { } journal && !gate.DryRun)
        {
            var columns = Columns(statement, renames);
            if (literals is not null) foreach (var (name, value) in literals) columns[name] = value;
            await journal.InsertAsync(table, columns, ct).ConfigureAwait(false);
        }

        await gate.ExecuteAsync(statement, ct).ConfigureAwait(false);
    }

    /// <param name="keyColumns">The columns that find the row; every other parameter is a change.</param>
    public static async Task UpdateAsync(MutationGate gate, TrackingScope scope, string table, GateStatement statement, string[] keyColumns, IReadOnlyDictionary<string, string>? renames = null, CancellationToken ct = default)
    {
        if (scope.Journal is { } journal && !gate.DryRun)
        {
            var columns = Columns(statement, renames);
            var key = columns.Where(c => keyColumns.Contains(c.Key)).ToDictionary(c => c.Key, c => c.Value);
            var changes = columns.Where(c => !keyColumns.Contains(c.Key)).ToDictionary(c => c.Key, c => c.Value);
            await journal.UpdateAsync(table, key, changes, ct).ConfigureAwait(false);
        }

        await gate.ExecuteAsync(statement, ct).ConfigureAwait(false);
    }

    private static Dictionary<string, object?> Columns(GateStatement statement, IReadOnlyDictionary<string, string>? renames)
    {
        var columns = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var p in statement.Parameters) columns[renames is not null && renames.TryGetValue(p.Name, out var column) ? column : p.Name] = p.Value;
        columns.Remove("connection");
        return columns;
    }
}
