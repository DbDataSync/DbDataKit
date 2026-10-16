using System.Data.Common;
using DbDataKit.Dialect;
using DuckDB.NET.Data;

namespace DbDataKit.Connections.DuckDb;

/// <summary>
/// DuckDB composer: <c>address.database</c> is the file path (or <c>:memory:</c>); there is no host, port, TLS or login. A login that
/// carries credentials is refused rather than ignored. A read-only context adds <c>access_mode=READ_ONLY</c>.
/// </summary>
public sealed class DuckDbComposer(ReadOnlySession? recipe = null) : BuilderComposer(EngineId.DuckDb)
{
    private readonly ReadOnlySession _recipe = recipe ?? DuckDbProvider.ReadOnlyRecipe;

    protected override DbConnectionStringBuilder NewBuilder() => new DuckDBConnectionStringBuilder();
    protected override string UserKey => "user";
    protected override string PasswordKey => "password";
    protected override bool UsesCredentials => false;
    protected override void SetIntegrated(IKeyBag bag) => throw new ConnectionPolicyException("duckdb has no integrated authentication");

    protected override void ApplyAddress(IKeyBag bag, AddressSpec a, ConnectionContext ctx)
    {
        if (a.Host is not null || a.Port is not null) throw new ConnectionPolicyException("duckdb is in-process: use address.database for the file, not host or port");
        if (a.Database is not null) bag.Set("Data Source", a.Database);
    }

    /// <summary>
    /// <c>access_mode=READ_ONLY</c> needs a file: DuckDB refuses it for an in-memory database, which has nothing to protect anyway
    /// (it is empty and private to the connection), so the key is left out there and the SET statements still run.
    /// </summary>
    protected override IEnumerable<KeyValuePair<string, string>> ReadOnlyKeys(IKeyBag bag, ConnectionContext ctx)
    {
        if (!bag.TryGet("Data Source", out var path) || DuckDbProvider.IsInMemory(path)) return [];
        return _recipe.ConnectionStringKeys;
    }
}

/// <summary>
/// Per source exceptions to the sandbox. <see cref="AllowedDirectories"/> keeps external access off but lets queries read (and, for a
/// read-write source, write) files below the listed directories, for example a source that deliberately reads a Parquet folder.
/// Everything else (other paths, <c>ATTACH</c> of an arbitrary file, extension install and load, changing a setting) stays blocked.
/// </summary>
public sealed record DuckDbSandboxOptions
{
    /// <summary>Absolute directories. Each is set through <c>allowed_directories</c>; relative paths, control characters and empty entries are refused.</summary>
    public IReadOnlyList<string> AllowedDirectories { get; init; } = [];
}

/// <summary>Maps DuckDB error classes (the text before the first colon of the message, from a fixed vocabulary) to kinds. The message itself is never kept.</summary>
public sealed class DuckDbFailureMapper : IFailureMapper
{
    private static readonly Dictionary<string, ConnectionFailureKind> Classes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Permission Error"] = ConnectionFailureKind.Permission,
        ["Parser Error"] = ConnectionFailureKind.Syntax,
        ["Binder Error"] = ConnectionFailureKind.Syntax,
        ["Catalog Error"] = ConnectionFailureKind.Syntax,
        ["Not implemented Error"] = ConnectionFailureKind.Unsupported,
        ["Interrupt Error"] = ConnectionFailureKind.Timeout,
        ["IO Error"] = ConnectionFailureKind.Network,
        ["Connection Error"] = ConnectionFailureKind.Network,
    };

    public ConnectionFailure? Map(Exception exception, EngineId engine)
    {
        if (exception is not DuckDBException) return null;
        string cls = ClassOf(exception.Message);
        return new ConnectionFailure(Classes.GetValueOrDefault(cls, ConnectionFailureKind.Other), cls.Replace(' ', '-'), engine);
    }

    /// <summary>The error class named at the start of a message, or <c>other</c>.</summary>
    public static string ClassOf(string message)
    {
        int colon = message.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0) return "other";
        var head = message[..colon].Trim();
        return Classes.ContainsKey(head) ? head : "other";
    }
}

/// <summary>DuckDB provider over DuckDB.NET.</summary>
public sealed class DuckDbProvider : DbProviderBase
{
    /// <summary>
    /// The sandbox (Build's set, closes Proxy S-1 for DuckDB): the file is opened <c>READ_ONLY</c>; then, in this order,
    /// external access is off (no <c>read_csv</c>, <c>COPY</c>, <c>ATTACH</c>, httpfs), extension install and autoload are off, and the
    /// configuration is locked last so none of it can be set back. Nothing may follow the lock.
    /// </summary>
    public static ReadOnlySession ReadOnlyRecipe { get; } = new(
        "duckdb", ReadOnlyEnforcement.Engine,
        [
            "SET enable_external_access = false",
            "SET autoinstall_known_extensions = false",
            "SET autoload_known_extensions = false",
            "SET lock_configuration = true",
        ],
        new Dictionary<string, string> { ["access_mode"] = "READ_ONLY" },
        "Engine enforced. An in-memory database cannot be opened read-only; use a file.");

    public DuckDbProvider(DialectCatalog? dialects = null, DbDataKit.Secrets.IRedactor? redactor = null, bool includeRawFailures = false)
        : base(EngineId.DuckDb, new DuckDbComposer(), ReadOnlyRecipe, new DuckDbFailureMapper(), dialects, redactor, includeRawFailures) { }

    /// <summary>A provider whose sandbox allows reading below <see cref="DuckDbSandboxOptions.AllowedDirectories"/> (see <see cref="Recipe"/>).</summary>
    public DuckDbProvider(DuckDbSandboxOptions sandbox, DialectCatalog? dialects = null, DbDataKit.Secrets.IRedactor? redactor = null, bool includeRawFailures = false)
        : this(Recipe(sandbox), dialects, redactor, includeRawFailures) { }

    private DuckDbProvider(ReadOnlySession recipe, DialectCatalog? dialects, DbDataKit.Secrets.IRedactor? redactor, bool includeRawFailures)
        : base(EngineId.DuckDb, new DuckDbComposer(recipe), recipe, new DuckDbFailureMapper(), dialects, redactor, includeRawFailures) { }

    /// <summary>True for <c>:memory:</c>, an empty path and the other spellings of a private in-memory database.</summary>
    public static bool IsInMemory(string? dataSource) =>
        string.IsNullOrWhiteSpace(dataSource) || dataSource.Trim().StartsWith(":memory:", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The recipe for <paramref name="sandbox"/>: the standard one with <c>SET allowed_directories</c> placed before external access is turned
    /// off (DuckDB takes the list while the configuration is still open) and the lock still last.
    /// </summary>
    public static ReadOnlySession Recipe(DuckDbSandboxOptions? sandbox)
    {
        if (sandbox is null || sandbox.AllowedDirectories.Count == 0) return ReadOnlyRecipe;
        var statements = new List<string> { AllowedDirectoriesStatement(sandbox.AllowedDirectories) };
        statements.AddRange(ReadOnlyRecipe.Statements);
        return new ReadOnlySession("duckdb", ReadOnlyEnforcement.Engine, statements, ReadOnlyRecipe.ConnectionStringKeys,
            ReadOnlyRecipe.Notes + " Reads below the allowed directories are permitted.");
    }

    /// <summary>The <c>SET allowed_directories</c> statement for a validated list. Quotes are doubled; names with control characters or relative paths are refused.</summary>
    public static string AllowedDirectoriesStatement(IReadOnlyList<string> directories)
    {
        var items = new List<string>();
        foreach (var d in directories)
        {
            if (string.IsNullOrWhiteSpace(d)) throw new ConnectionPolicyException("allowed directory is empty");
            if (d.Any(char.IsControl)) throw new ConnectionPolicyException("allowed directory has a control character");
            if (!Path.IsPathFullyQualified(d)) throw new ConnectionPolicyException("allowed directory must be an absolute path", d);
            items.Add("'" + d.Replace('\\', '/').Replace("'", "''", StringComparison.Ordinal) + "'");
        }
        return "SET allowed_directories = [" + string.Join(", ", items) + "]";
    }

    protected override DbConnection NewConnection(string connectionString, ConnectionContext context) => new DuckDBConnection(connectionString);
}
