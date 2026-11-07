namespace DbDataKit.State.Snapshots;

/// <summary>
/// The snapshot format (plan 29b decision M2): a directory holding <c>manifest.json</c>, one gzipped JSON-lines file per table (<c>tables/&lt;module&gt;.&lt;table&gt;.jsonl.gz</c>) and one binary file per payload
/// (<c>payloads/&lt;payload id&gt;.bin</c>). Written by <see cref="StateSnapshotWriter"/>. The manifest is written last, so a directory that has one is complete.
/// <para>
/// A table file has one JSON array per line, the values in the order of the manifest's column list, in the neutral form of the column's logical type (<see cref="SnapshotColumnTypes"/>).
/// Rows are in primary-key order of the exporting engine, read by keyset paging.
/// </para>
/// </summary>
public static class SnapshotFormat
{
    /// <summary>The format id, the first member of the manifest.</summary>
    public const string Id = "state-snapshot/1";

    public const string ManifestFile = "manifest.json";

    public const string TablesDirectory = "tables";

    public const string PayloadsDirectory = "payloads";

    /// <summary>The file of a table, relative to the snapshot directory (forward slashes in the manifest on every platform).</summary>
    public static string TableFile(string module, string table) => $"{TablesDirectory}/{module}.{table}.jsonl.gz";

    /// <summary>The file of a payload, relative to the snapshot directory.</summary>
    public static string PayloadFile(Guid id) => $"{PayloadsDirectory}/{id:D}.bin";
}

/// <summary>
/// The logical types of the conventions (plan 29 section 3.3) and the neutral form each has in a table file. A column's logical type is read from the module's migration templates, never
/// from the engine's own column type, so every engine gives one answer.
/// <list type="bullet">
/// <item><c>uuid</c>: lower-case hyphenated text.</item>
/// <item><c>time</c>: UTC ISO 8601 with seven fractional digits and a <c>Z</c> (<c>2026-01-02T03:04:05.1234567Z</c>), the fixed width that sorts as time does.</item>
/// <item><c>bool</c>: JSON <c>true</c> or <c>false</c>.</item>
/// <item><c>int</c>: a JSON integer (the <c>{{int}}</c> token and the older <c>{{identity:column}}</c>, which also sets the column's <c>identity</c> flag).</item>
/// <item><c>decimal</c>: invariant text without trailing zeros (<c>"12.5"</c>).</item>
/// <item><c>json</c>: a string holding the document in canonical form (object members sorted by name, no white space), so PostgreSQL's <c>jsonb</c> and the other engines' text agree.</item>
/// <item><c>text</c>: a string (the <c>{{text}}</c> and <c>{{key}}</c> tokens).</item>
/// <item><c>blob</c>: a base64 string; the chunk bytes of the payloads module are in payload files instead, and the column holds a marker object (<see cref="SnapshotColumnStorage.PayloadFile"/>).</item>
/// </list>
/// </summary>
public static class SnapshotColumnTypes
{
    public const string Uuid = "uuid";
    public const string Time = "time";
    public const string Bool = "bool";
    public const string Int = "int";
    public const string Decimal = "decimal";
    public const string Json = "json";
    public const string Text = "text";
    public const string Blob = "blob";
}

/// <summary>Where a column's values are, when not inline in the row.</summary>
public static class SnapshotColumnStorage
{
    /// <summary>The value is an object <c>{"payload":"&lt;id&gt;","offset":n,"length":n}</c>: the bytes are at that place in the payload's file.</summary>
    public const string PayloadFile = "payload-file";
}

/// <param name="Name">The tool that wrote the snapshot.</param>
/// <param name="Version">Its version.</param>
public sealed record SnapshotTool(string Name, string Version);

/// <param name="Group">The group the exporting process was a member of, where it had one.</param>
/// <param name="Service">The service, where it had one.</param>
public sealed record SnapshotScope(string? Group, string? Service);

/// <param name="Name">The column's name (the same on every engine).</param>
/// <param name="Type">One of <see cref="SnapshotColumnTypes"/>.</param>
/// <param name="Nullable">Whether the migrations declare it so.</param>
/// <param name="Identity">An auto-assigned integer key (the older <c>{{identity:column}}</c>): an import has to reseed the sequence.</param>
/// <param name="Storage">Null for a value inline in the row, else a <see cref="SnapshotColumnStorage"/>.</param>
public sealed record SnapshotColumn(string Name, string Type, bool Nullable, bool Identity = false, string? Storage = null);

/// <param name="Name">The table's name within its module.</param>
/// <param name="PhysicalName">The table's real name as it was exported (schema and prefix as the layout rendered them).</param>
/// <param name="File">The table file, relative to the snapshot.</param>
/// <param name="Columns">In the order of the values of every row.</param>
/// <param name="PrimaryKey">The key columns, in key order.</param>
/// <param name="Rows">How many rows the file has.</param>
/// <param name="Sha256">Lower-case hex SHA-256 of the file as written (the gzip bytes).</param>
/// <param name="RowsDigest">
/// Lower-case hex of the sum, modulo 2^256, of the SHA-256 of every row's line (the UTF-8 JSON array without its line break), each hash read as a big-endian number. It does not depend on the order
/// rows were read in, so it is the same on every engine for the same logical data (engines order a UUID or text key differently, which changes <see cref="Sha256"/> and nothing else).
/// </param>
public sealed record SnapshotTable(string Name, string PhysicalName, string File, IReadOnlyList<SnapshotColumn> Columns, IReadOnlyList<string> PrimaryKey, long Rows, string Sha256, string RowsDigest);

/// <param name="Name">The module's name.</param>
/// <param name="ScopeKind"><c>install</c>, <c>group</c> or <c>service</c>.</param>
/// <param name="ScopeName">The group's or service's normalised name, null for a module kept per install.</param>
/// <param name="Version">How many migrations the module had applied.</param>
/// <param name="Checksum">The registry's checksum of those migrations (the same on every engine).</param>
/// <param name="Tables">In an order that has a table after the tables it refers to.</param>
public sealed record SnapshotModule(string Name, string ScopeKind, string? ScopeName, int Version, string Checksum, IReadOnlyList<SnapshotTable> Tables);

/// <param name="Id">The payload's id.</param>
/// <param name="File">Relative to the snapshot.</param>
/// <param name="Size">Bytes.</param>
/// <param name="Sha256">Lower-case hex SHA-256 of the file.</param>
/// <param name="Chunks">How many chunk rows it came from.</param>
public sealed record SnapshotPayloadFile(Guid Id, string File, long Size, string Sha256, int Chunks);

/// <summary>What <c>manifest.json</c> says. Modules are in dependency order (a module after the modules it depends on); the registry table is not a table of the snapshot, the module list is its content.</summary>
public sealed record SnapshotManifest(
    string Format,
    DateTimeOffset CreatedAt,
    string Engine,
    SnapshotTool Tool,
    SnapshotScope Scope,
    IReadOnlyList<SnapshotModule> Modules,
    IReadOnlyList<SnapshotPayloadFile> Payloads);

/// <summary>What <see cref="StateSnapshotWriter"/> needs beyond the database.</summary>
public sealed record SnapshotOptions
{
    public static SnapshotOptions Default { get; } = new();

    /// <summary>
    /// Export although a live instance (or another process on a SQLite file) uses the database. Named loudly because it can write a snapshot that misses what the live instance writes next: for tests and
    /// recovery of a state whose instance row is a leftover.
    /// </summary>
    public bool ExportEvenIfInUse { get; init; }

    /// <summary>
    /// Keys of instance rows that do not count as a live user of the database: the process that exports is itself one of them when it runs a move on its own state (the management API's move, plan 29b C06).
    /// Other instances still refuse the export.
    /// </summary>
    public IReadOnlySet<string>? IgnoredInstances { get; init; }

    /// <summary>Rows asked for in one keyset query.</summary>
    public int BatchSize { get; init; } = 1000;

    /// <summary>How recent a heartbeat has to be for an instance row to count as live where the row has no lease column of its own (the Proxy's lease is 30 seconds).</summary>
    public TimeSpan InstanceLease { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long to wait for the exclusive lock of a SQLite file before saying another process holds it (rounded up to whole seconds).</summary>
    public TimeSpan SqliteLockTimeout { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>A progress line each time this many payload bytes were written.</summary>
    public long ProgressBytes { get; init; } = 8 * 1024 * 1024;
}

/// <summary>A progress report: <paramref name="Phase"/> is <c>table-start</c>, <c>rows</c>, <c>payload</c>, <c>table-done</c> or <c>done</c>.</summary>
public sealed record SnapshotProgress(string Phase, string? Module, string? Table, long Rows, long PayloadBytes);

/// <summary>What an export did.</summary>
public sealed record SnapshotResult(SnapshotManifest Manifest, string Directory, long Rows, int Tables, int PayloadFiles, long PayloadBytes, TimeSpan Elapsed);

/// <summary>Why an export was refused before it wrote anything.</summary>
public enum SnapshotRefusal
{
    DirectoryNotEmpty,
    InstanceInUse,
    FileHeld,
    UnknownModule,
    Unsupported,
}

/// <summary>An export that was refused; the message names what, and nothing was written (or what was written is removed).</summary>
public sealed class SnapshotRefusedException(SnapshotRefusal reason, string message) : InvalidOperationException(message)
{
    public SnapshotRefusal Reason { get; } = reason;
}
