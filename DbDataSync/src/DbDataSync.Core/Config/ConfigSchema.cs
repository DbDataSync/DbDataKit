namespace DbDataSync.Core.Config;

/// <summary>
/// The format version Sync writes into each kind of file that has one, and the only one it reads: one number per kind, named here and nowhere else.
/// <para>
/// A replication's <c>task.yaml</c> is format 2 (plan 23a FQ04): it may hold a <c>checks:</c> section (the <c>check/1</c> entries of <c>DbDataKit.Quality</c>). Nothing else changed from 1, and a table-mapping or script-manifest
/// file did not change at all, so those stay 1: only a replication that wants checks needs editing. A release that reads 1 refuses a task file of 2 (and this one refuses a task file of 1) by name rather than reading half of it.
/// </para>
/// </summary>
internal static class ConfigSchema
{
    public const int TaskCurrent = 2;
    public const int MappingCurrent = 1;
    public const int ScriptCurrent = 1;
    /// <summary>The retired connection model (the file is <c>connection/1</c> now); it keeps the number it had.</summary>
    public const int ConnectionLegacy = 1;

    /// <summary>The version of the file kind that binds to <paramref name="type"/>.</summary>
    public static int For(Type type) =>
        type == typeof(ReplicationTaskConfig) ? TaskCurrent
        : type == typeof(TableMappingConfig) ? MappingCurrent
        : type == typeof(ScriptConfig) ? ScriptCurrent
        : throw new ArgumentException($"{type.Name} is not a Sync file kind with a schemaVersion.", nameof(type));

    /// <summary>What a file of an earlier format is told: what changed in its kind, and what to do.</summary>
    public static string HowToMove(Type type, int from, int current) =>
        type == typeof(ReplicationTaskConfig) && from == 1
            ? $"Format {current} only adds the optional checks: section to a replication's task.yaml; nothing else in the files changed. Review the file, then set schemaVersion: {current}."
            : $"Review the file against the current format, then set schemaVersion: {current}.";
}
