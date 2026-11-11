using System.Data.Common;
using System.Globalization;
using DbDataKit.Dialect;
using Oracle.ManagedDataAccess.Client;

namespace DbDataKit.Connections.Oracle;

/// <summary>
/// ODP.NET composer. A host address is written as EZ Connect, <c>host:port/service</c> (<c>tcps://host:port/service</c> when <c>tls.encrypt</c>
/// is on and a host is named); the default port is 1521. With a connection string as the base the data source is the operator's (a TNS alias, a
/// descriptor, a wallet) and TLS is not rewritten. The connection builder cannot relax certificate checks, so <c>tls.verify: false</c> changes nothing,
/// and <c>tls.ca_file</c> is refused rather than guessed (a wallet belongs in the data source or <c>TNS_ADMIN</c>).
/// </summary>
public sealed class OracleComposer() : BuilderComposer(EngineId.Oracle)
{
    protected override DbConnectionStringBuilder NewBuilder() => new OracleConnectionStringBuilder();
    protected override string UserKey => "User Id";
    protected override string PasswordKey => "Password";

    /// <summary>ODP.NET Core has no Windows integrated security; <see cref="ApplyAddress"/> never sees it, so the login is refused here.</summary>
    protected override void SetIntegrated(IKeyBag bag) =>
        throw new ConnectionPolicyException("integrated authentication is not supported for Oracle; use a password, or a wallet with no login");

    protected override void ApplyAddress(IKeyBag bag, AddressSpec a, ConnectionContext ctx)
    {
        if (a.Host is null) return;
        string service = a.Database ?? throw new ConnectionPolicyException("the database (the Oracle service name) is required when a host is given");
        string port = (a.Port ?? 1521).ToString(CultureInfo.InvariantCulture);
        string scheme = ctx.Definition.Tls.Encrypt ? "tcps://" : "";
        bag.Set("Data Source", $"{scheme}{a.Host}:{port}/{service}");
    }

    protected override void ApplyTls(IKeyBag bag, TlsSpec tls, ConnectionContext ctx)
    {
        if (tls.CaFile is not null) throw new ConnectionPolicyException("tls.ca_file is not supported for Oracle; put the wallet in the data source or TNS_ADMIN");
    }

    protected override void ApplyTimeouts(IKeyBag bag, TimeoutSpec t, ConnectionContext ctx) =>
        bag.Set("Connection Timeout", t.ConnectS.ToString(CultureInfo.InvariantCulture));
}

/// <summary>Maps ODP.NET exceptions by ORA error number.</summary>
public sealed class OracleFailureMapper : IFailureMapper
{
    private static readonly HashSet<int> Login = [1017, 1005, 28000, 28001, 28002, 28009, 1045, 12505, 12514, 12516, 12519, 12520, 12521, 12528, 12537];
    private static readonly HashSet<int> Permission = [1031, 1950, 1749, 1994];
    private static readonly HashSet<int> Network = [12154, 12203, 12500, 12541, 12543, 12545, 12560, 3113, 3114, 3135, 17002, 17008, 12547];
    private static readonly HashSet<int> Timeout = [12170, 12535, 1013, 30006];
    private static readonly HashSet<int> Tls = [28750, 28759, 28860, 29024, 29106];
    private static readonly HashSet<int> Syntax = [900, 903, 904, 905, 906, 907, 908, 911, 923, 933, 936, 942, 955, 1722];

    public ConnectionFailure? Map(Exception exception, EngineId engine)
    {
        if (exception is not OracleException ora) return null;
        int n = ora.Number;
        var kind = Tls.Contains(n) ? ConnectionFailureKind.Tls
            : Login.Contains(n) ? ConnectionFailureKind.Login
            : Timeout.Contains(n) ? ConnectionFailureKind.Timeout
            : Permission.Contains(n) ? ConnectionFailureKind.Permission
            : Network.Contains(n) ? ConnectionFailureKind.Network
            : Syntax.Contains(n) ? ConnectionFailureKind.Syntax
            : ConnectionFailureKind.Other;
        return new ConnectionFailure(kind, n.ToString(CultureInfo.InvariantCulture), engine);
    }
}

/// <summary>Oracle provider over ODP.NET.</summary>
public sealed class OracleProvider : DbProviderBase
{
    public static ReadOnlySession ReadOnlyRecipe { get; } = ReadOnlySession.NoneAvailable(
        "oracle", "Oracle has no session setting that makes a connection read-only (SET TRANSACTION READ ONLY lasts one transaction). Use a read-only user.");

    public OracleProvider(DialectCatalog? dialects = null)
        : base(EngineId.Oracle, new OracleComposer(), ReadOnlyRecipe, new OracleFailureMapper(), dialects) { }

    protected override string TestSql => "SELECT 1 FROM DUAL";

    protected override DbConnection NewConnection(string connectionString, ConnectionContext context) => new OracleConnection(connectionString);
}
