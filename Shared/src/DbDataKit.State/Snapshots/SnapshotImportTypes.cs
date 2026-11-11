namespace DbDataKit.State.Snapshots;

/// <summary>What <see cref="StateSnapshotImporter"/> may be asked to do differently.</summary>
public sealed record ImportOptions
{
    public static ImportOptions Default { get; } = new();

    /// <summary>
    /// After every module of the snapshot is loaded at the version the snapshot had, bring all of the target layout's modules up to this binary's versions with the normal migrator (an older snapshot
    /// then ends at the current version, and a module the snapshot did not have is created). Default true.
    /// </summary>
    public bool MigrateAfterImport { get; init; } = true;

    /// <summary>
    /// When an import fails (or is cancelled), drop what it created (the tables of the modules that had completed, and their registry rows) so a retry starts from an empty target. Default false: the
    /// completed modules stay, <see cref="SnapshotImportException.CompletedModules"/> says which, and <see cref="StateSnapshotImporter.ResetAsync"/> removes them on request.
    /// </summary>
    public bool ResetOnFailure { get; init; }

    /// <summary>Most rows in one INSERT statement (also bounded by the engine's parameter limit).</summary>
    public int BatchRows { get; init; } = 1000;

    /// <summary>An INSERT is sent once the binary values it holds reach this many bytes (the chunks of a payload are up to a mebibyte each), which keeps memory bounded whatever the row count.</summary>
    public long BatchBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>Overrides the engine's parameter limit per statement (SQL Server 2100, SQLite 999 to be safe, PostgreSQL 65535): for tests of the batching.</summary>
    public int? MaxParameters { get; init; }

    /// <summary>Who the registry says applied the migrations; the target's own value by default.</summary>
    public string? AppliedBy { get; init; }

    /// <summary>A progress line each time this many payload bytes were loaded.</summary>
    public long ProgressBytes { get; init; } = 8 * 1024 * 1024;

    /// <summary>
    /// An optimised way of loading rows for engines that have one (PostgreSQL <c>COPY</c>, SQL Server <c>SqlBulkCopy</c>), supplied by code that references the provider; null loads every table with the batched
    /// parameterised INSERT, which is correct everywhere. A loader says per table whether it takes it; the rows are then handed over in batches of <see cref="BatchRows"/> (not bounded by the parameter limit).
    /// </summary>
    public ISnapshotBulkLoader? BulkLoader { get; init; }
}

/// <summary>
/// Loads a batch of rows into a table faster than a parameterised INSERT can. Values arrive already converted for the target engine (<see cref="StateValues.ForParameter"/>): <see cref="Guid"/> (SQL Server,
/// PostgreSQL) or text (SQLite) for a uuid, <see cref="DateTimeOffset"/> in UTC or text for a time, <see cref="bool"/>, <see cref="long"/>, <see cref="decimal"/> or text, <c>byte[]</c>, and
/// <see cref="string"/> for text and json; null is null. The loader runs inside the importer's transaction on the importer's connection and must not commit it.
/// </summary>
public interface ISnapshotBulkLoader
{
    /// <summary>Whether this loader loads the table on this target (by engine, usually).</summary>
    bool CanLoad(StateDatabase target, SnapshotTable table);

    /// <param name="qualifiedTable">The target's real name for the table.</param>
    /// <param name="hasIdentity">The table has an identity-style key whose values are in the rows and have to be kept.</param>
    Task LoadAsync(System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction transaction, StateDatabase target, string qualifiedTable, IReadOnlyList<SnapshotColumn> columns, bool hasIdentity, IReadOnlyList<object?[]> rows, CancellationToken ct);
}

/// <summary>Why an import was refused or stopped.</summary>
public enum SnapshotImportRefusal
{
    /// <summary><c>manifest.json</c> is not there.</summary>
    ManifestMissing,

    /// <summary><c>manifest.json</c> is not a manifest of this format (not JSON, a member missing, a value of the wrong kind).</summary>
    ManifestCorrupt,

    /// <summary>A format other than <c>state-snapshot/1</c>.</summary>
    FormatUnsupported,

    /// <summary>A table or payload file the manifest lists is not there, or lies outside the snapshot directory.</summary>
    FileMissing,

    /// <summary>A file whose hash, size, row count or content is not what the manifest says (tampered or damaged).</summary>
    FileCorrupt,

    /// <summary>The snapshot has a module this binary does not know, or the target's layout does not have it.</summary>
    ModuleUnknown,

    /// <summary>The snapshot has a module at a version newer than the binary's.</summary>
    ModuleNewer,

    /// <summary>The first migrations of a module are not the ones the snapshot was made with (a migration that shipped was edited).</summary>
    ChecksumMismatch,

    /// <summary>The tables or columns the manifest lists are not what the module's migrations declare at that version.</summary>
    SchemaMismatch,

    /// <summary>The target already has a module in its registry: an import goes into an empty database only.</summary>
    TargetNotEmpty,

    /// <summary>The target's table, read back after the load, does not have the manifest's <c>rows_digest</c>.</summary>
    TableDigestMismatch,

    /// <summary>A failure while loading a module (the module was rolled back).</summary>
    LoadFailed,

    /// <summary>Applying the migrations after the import failed (every module of the snapshot had been loaded).</summary>
    MigrationFailed,

    /// <summary>The target is not a database built from modules.</summary>
    Unsupported,

    /// <summary>A module of the snapshot needs an engine feature the target engine lacks (plan 29b U01); nothing was created.</summary>
    FeatureMissing,
}

/// <summary>An import that was refused before it wrote anything, or that stopped; the message names the file, module or table.</summary>
public sealed class SnapshotImportException(SnapshotImportRefusal reason, string message, IReadOnlyList<string>? completedModules = null, Exception? inner = null) : InvalidOperationException(message, inner)
{
    public SnapshotImportRefusal Reason { get; } = reason;

    /// <summary>The modules that were fully loaded and committed before the failure (empty for a refusal before the first); they are still in the target unless the import was asked to reset.</summary>
    public IReadOnlyList<string> CompletedModules { get; } = completedModules ?? [];

    /// <summary>True when <see cref="ImportOptions.ResetOnFailure"/> removed the completed modules again.</summary>
    public bool WasReset { get; internal set; }
}

/// <summary>What an import did.</summary>
/// <param name="Manifest">The snapshot's manifest.</param>
/// <param name="Modules">The modules loaded, in order.</param>
/// <param name="Migrated">Whether the normal migrator ran afterwards.</param>
/// <param name="Warnings">What differed without being wrong, for example time values the engine stores at a coarser precision (PostgreSQL keeps microseconds).</param>
public sealed record SnapshotImportResult(SnapshotManifest Manifest, IReadOnlyList<string> Modules, long Rows, int Tables, int PayloadFiles, long PayloadBytes, bool Migrated, IReadOnlyList<string> Warnings, TimeSpan Elapsed)
{
    /// <summary>The tables (<c>module.table</c>) whose digest differs from the manifest's only because the engine stores times at a coarser precision (see <see cref="Warnings"/>): a later comparison with the snapshot or the source database has to expect those.</summary>
    public IReadOnlyList<string> RoundedTables { get; init; } = [];
}
