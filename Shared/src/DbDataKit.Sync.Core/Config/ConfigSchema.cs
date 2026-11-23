namespace DbDataSync.Core.Config;

/// <summary>
/// The format version Sync writes into each kind of file that has one, and the range it reads (the current format and the one before it, plan 33 section 6d): one number per kind, named here and nowhere else.
/// A file in the range loads as it is (nothing checks that an older file lacks what a newer format added) and every save writes the current number, never the one that was read.
/// <para>
/// A replication's <c>task.yaml</c> is format 2 (plan 23a FQ04): it may hold a <c>checks:</c> section (the <c>check/1</c> entries of <c>DbDataKit.Quality</c>). Nothing else changed from 1, and a table-mapping or script-manifest
/// file did not change at all, so those stay 1. This release reads a task file of 1 or 2 and saves 2; a release that reads only 1 refuses a task file of 2 by name rather than reading half of it.
/// </para>
/// </summary>
internal static class ConfigSchema
{
    public const int TaskCurrent = 2;
    public const int MappingCurrent = 1;
    public const int ScriptCurrent = 1;
    /// <summary>The retired connection model (the file is <c>connection/1</c> now); it keeps the number it had.</summary>
    public const int ConnectionLegacy = 1;

    /// <summary>The oldest and the newest format of a kind Sync reads. The same shape as <c>DbDataKit.State.DefinitionFormats</c> (current and the one before); that type lives in a library
    /// that brings the state engines, which the configuration code does not need, so the two numbers are declared here.</summary>
    public readonly record struct Range(int Previous, int Current)
    {
        public static Range Of(int current) => new(Math.Max(1, current - 1), current);
        public bool Reads(int version) => version >= Previous && version <= Current;
    }

    /// <summary>The range of formats read for the file kind that binds to <paramref name="type"/>; its <c>Current</c> is <see cref="For"/>.</summary>
    public static Range ReadsOf(Type type) =>
        type == typeof(ReplicationTaskConfig) ? Range.Of(TaskCurrent)
        : type == typeof(TableMappingConfig) ? new(MappingCurrent, MappingCurrent)
        : type == typeof(ScriptConfig) ? new(ScriptCurrent, ScriptCurrent)
        : throw new ArgumentException($"{type.Name} is not a Sync file kind with a schemaVersion.", nameof(type));

    /// <summary>The version of the file kind that binds to <paramref name="type"/>.</summary>
    public static int For(Type type) =>
        type == typeof(ReplicationTaskConfig) ? TaskCurrent
        : type == typeof(TableMappingConfig) ? MappingCurrent
        : type == typeof(ScriptConfig) ? ScriptCurrent
        : throw new ArgumentException($"{type.Name} is not a Sync file kind with a schemaVersion.", nameof(type));

    /// <summary>What a file older than the range is told: what to do. Used only below <see cref="ReadsOf"/>'s <c>Previous</c>.</summary>
    public static string HowToMove(Type type, int from, int current) =>
        $"Review the file against the current format, then set schemaVersion: {current}.";
}
