namespace DbDataKit.Sync.Drivers.Abstractions;

/// <summary>
/// A pass found its mapping's target table missing, the mapping has no target column cache, and the replication may not create the table itself (create-if-missing is off). Plan 28c E21, owner decision
/// 2026-11-15 (option 1): such a mapping is published to a runner all the same, and its drain refuses to run until the table exists; the table comes to exist when a person applies the replication's
/// database setup (the Setup card's Apply, which runs the set's <c>provision</c> task on the Runner). Creating a table stays an explicit action.
/// </summary>
/// <param name="mappingName">The table mapping, as an operator named it.</param>
/// <param name="table">The target table, as the mapping names it.</param>
public sealed class TargetTableMissingException(string mappingName, string table)
    : Exception($"The target table {table} of table mapping '{mappingName}' does not exist yet, so this pass cannot run. Apply the replication's database setup (Apply on the mapping's Setup card, which runs the setup on the Runner) to create it, then run again.")
{
    public string MappingName { get; } = mappingName;
    public string Table { get; } = table;
}
