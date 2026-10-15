using System.Data.Common;
using DbData.Dialect;
using DuckDB.NET.Data;

namespace DbData.Connections.DuckDb;

/// <summary>
/// DuckDB composer: <c>address.database</c> is the file path (or <c>:memory:</c>); there is no host, port, TLS or login. A login that
/// carries credentials is refused rather than ignored. A read-only context adds <c>access_mode=READ_ONLY</c>.
/// </summary>
public sealed class DuckDbComposer() : BuilderComposer(EngineId.DuckDb)
{
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

    protected override IEnumerable<KeyValuePair<string, string>> ReadOnlyKeys(IKeyBag bag, ConnectionContext ctx) =>
        DuckDbProvider.ReadOnlyRecipe.ConnectionStringKeys;
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

    public DuckDbProvider(DialectCatalog? dialects = null)
        : base(EngineId.DuckDb, new DuckDbComposer(), ReadOnlyRecipe, new DuckDbFailureMapper(), dialects) { }

    protected override DbConnection NewConnection(string connectionString, ConnectionContext context) => new DuckDBConnection(connectionString);
}
