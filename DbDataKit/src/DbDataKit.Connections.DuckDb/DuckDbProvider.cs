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
        var kind = Classes.GetValueOrDefault(cls, ConnectionFailureKind.Other);
        // A write against a READ_ONLY database or a change to a locked configuration is reported as "Invalid Input Error"; to a caller it is a permission failure.
        if (kind == ConnectionFailureKind.Other && IsSandboxRefusal(exception.Message)) kind = ConnectionFailureKind.Permission;
        return new ConnectionFailure(kind, cls.Replace(' ', '-'), engine);
    }

    private static bool IsSandboxRefusal(string message) =>
        message.StartsWith("Invalid Input Error:", StringComparison.OrdinalIgnoreCase)
        && (message.Contains("read-only mode", StringComparison.OrdinalIgnoreCase) || message.Contains("has been locked", StringComparison.OrdinalIgnoreCase));

    /// <summary>The error class named at the start of a message, or <c>other</c>.</summary>
    public static string ClassOf(string message)
    {
        int colon = message.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0) return "other";
        var head = message[..colon].Trim();
        return Classes.ContainsKey(head) ? head : "other";
    }
}

/// <summary>
/// The sandbox as a session that can be applied to any number of connections of the same file. DuckDB.NET shares one database instance per file within a
/// process, and <c>lock_configuration</c> is a setting of that instance: the first connection locks it, and every later connection (a second query while the
/// first is still streaming) would fail on its first <c>SET</c>. So a connection that finds the configuration already locked checks that it is the sandbox
/// this recipe would have applied (external access off, the same allowed directories) instead of setting it again, and fails closed when it is not.
/// </summary>
internal sealed class DuckDbSandboxSession(
    IReadOnlyList<string> statements, IReadOnlyDictionary<string, string> keys, string? notes, IReadOnlyList<string> allowedDirectories)
    : ReadOnlySession("duckdb", ReadOnlyEnforcement.Engine, statements, keys, notes)
{
    public override async Task ApplyAsync(DbConnection connection, CancellationToken ct = default)
    {
        if (!await IsLockedAsync(connection, ct).ConfigureAwait(false))
        {
            try
            {
                await base.ApplyAsync(connection, ct).ConfigureAwait(false);
                return;
            }
            catch (DuckDBException)
            {
                // Another connection to the same file may have locked the configuration between our check and our SET (it ran the same statements before the lock).
                // Anything else is a real failure.
                if (!await IsLockedAsync(connection, ct).ConfigureAwait(false)) throw;
            }
        }
        await VerifyAsync(connection, ct).ConfigureAwait(false);
    }

    private static async Task<object?> ScalarAsync(DbConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }

    private static bool IsTrue(object? value) => value is bool b ? b : string.Equals(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), "true", StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> IsLockedAsync(DbConnection connection, CancellationToken ct) =>
        IsTrue(await ScalarAsync(connection, "SELECT current_setting('lock_configuration')", ct).ConfigureAwait(false));

    private static string Normal(string path) => path.Replace('\\', '/').TrimEnd('/');

    private async Task VerifyAsync(DbConnection connection, CancellationToken ct)
    {
        bool external = IsTrue(await ScalarAsync(connection, "SELECT current_setting('enable_external_access')", ct).ConfigureAwait(false));
        bool install = IsTrue(await ScalarAsync(connection, "SELECT current_setting('autoinstall_known_extensions')", ct).ConfigureAwait(false));
        bool load = IsTrue(await ScalarAsync(connection, "SELECT current_setting('autoload_known_extensions')", ct).ConfigureAwait(false));
        var actual = (await ScalarAsync(connection, "SELECT current_setting('allowed_directories')", ct).ConfigureAwait(false) is System.Collections.IEnumerable list and not string
            ? list.Cast<object?>().Select(o => Normal(Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture) ?? "")) : []).ToList();

        var compare = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var expected = allowedDirectories.Select(Normal).ToList();
        // DuckDB adds the database's own temporary directory (<file>.tmp) to the list; nothing else may be in it besides what the recipe sets.
        bool same = !external && !install && !load
            && expected.All(e => actual.Contains(e, compare))
            && actual.All(a => expected.Contains(a, compare) || a.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
        if (!same)
            throw new ConnectionPolicyException("the DuckDB database is already open in this process with a different sandbox (another data source uses the same file with other allowed directories, or without the sandbox)");
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
    public static ReadOnlySession ReadOnlyRecipe { get; } = new DuckDbSandboxSession(
        [
            "SET enable_external_access = false",
            "SET autoinstall_known_extensions = false",
            "SET autoload_known_extensions = false",
            "SET lock_configuration = true",
        ],
        new Dictionary<string, string> { ["access_mode"] = "READ_ONLY" },
        "Engine enforced. An in-memory database cannot be opened read-only; use a file. The configuration lives on the database instance DuckDB shares per file within a process, so a later connection verifies it instead of setting it again.",
        []);

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
        return new DuckDbSandboxSession(statements, ReadOnlyRecipe.ConnectionStringKeys, ReadOnlyRecipe.Notes + " Reads below the allowed directories are permitted.", sandbox.AllowedDirectories);
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
