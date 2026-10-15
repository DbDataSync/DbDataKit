using System.Data.Common;
using System.Globalization;
using DbData.Dialect;
using Microsoft.Data.SqlClient;

namespace DbData.Connections.SqlServer;

/// <summary>
/// SqlClient composer. Encryption is Mandatory and the certificate is validated unless the definition says otherwise; the old
/// Sync default <c>TrustServerCertificate=true</c> is gone (Sync 9.5). <c>tls.ca_file</c> becomes <c>ServerCertificate</c> (a pin: SqlClient
/// matches the server certificate against that file, it does not take a CA bundle).
/// </summary>
public sealed class SqlServerComposer() : BuilderComposer(EngineId.SqlServer)
{
    protected override DbConnectionStringBuilder NewBuilder() => new SqlConnectionStringBuilder();
    protected override string UserKey => "User ID";
    protected override string PasswordKey => "Password";

    protected override void SetIntegrated(IKeyBag bag) => bag.Set("Integrated Security", "True");

    protected override void ApplyAddress(IKeyBag bag, AddressSpec a, ConnectionContext ctx)
    {
        if (a.Host is not null) bag.Set("Data Source", a.Port is int p ? $"{a.Host},{p.ToString(CultureInfo.InvariantCulture)}" : a.Host);
        if (a.Database is not null) bag.Set("Initial Catalog", a.Database);
    }

    protected override void ApplyTls(IKeyBag bag, TlsSpec tls, ConnectionContext ctx)
    {
        bag.Set("Encrypt", tls.Encrypt ? "Mandatory" : "Optional");
        bag.Set("Trust Server Certificate", tls.EffectiveVerify ? "False" : "True");
        if (tls.CaFile is not null && tls.EffectiveVerify) bag.Set("Server Certificate", tls.CaFile);
    }

    protected override void ApplyTimeouts(IKeyBag bag, TimeoutSpec t, ConnectionContext ctx)
    {
        bag.Set("Connect Timeout", t.ConnectS.ToString(CultureInfo.InvariantCulture));
        bag.Set("Command Timeout", t.CommandS.ToString(CultureInfo.InvariantCulture));
    }

    protected override IEnumerable<KeyValuePair<string, string>> ReadOnlyKeys(IKeyBag bag, ConnectionContext ctx) =>
        SqlServerProvider.ReadOnlyRecipe.ConnectionStringKeys;
}

/// <summary>Maps <see cref="SqlException"/> numbers to failure kinds. The number is the public code; the message is never used.</summary>
public sealed class SqlServerFailureMapper : IFailureMapper
{
    private static readonly HashSet<int> Login = [18456, 18452, 18451, 18470, 18486, 18487, 18488, 4060, 233, 18450];
    private static readonly HashSet<int> Network = [53, 64, 121, 10053, 10054, 10060, 10061, 11001, 40613, 40197, 40501, 40540, 17142, 6005, -1, 26, 2, 20];
    private static readonly HashSet<int> Timeout = [-2, 258];
    private static readonly HashSet<int> Permission = [229, 230, 262, 297, 300, 371, 916, 15247, 15151];
    private static readonly HashSet<int> Syntax = [102, 105, 156, 128, 137, 170, 195, 207, 208, 213, 245, 515, 8114];
    private static readonly HashSet<int> Tls = [-2146893019, -2146893007, -2146893048, -2146893018, -2146893043];

    public ConnectionFailure? Map(Exception exception, EngineId engine)
    {
        if (exception is not SqlException sql) return null;
        return new ConnectionFailure(KindOf(sql.Number), sql.Number.ToString(CultureInfo.InvariantCulture), engine);
    }

    /// <summary>The kind for a SQL Server error number.</summary>
    public static ConnectionFailureKind KindOf(int n)
    {
        return Tls.Contains(n) ? ConnectionFailureKind.Tls
            : Login.Contains(n) ? ConnectionFailureKind.Login
            : Timeout.Contains(n) ? ConnectionFailureKind.Timeout
            : Permission.Contains(n) ? ConnectionFailureKind.Permission
            : Syntax.Contains(n) ? ConnectionFailureKind.Syntax
            : Network.Contains(n) ? ConnectionFailureKind.Network
            : ConnectionFailureKind.Other;
    }
}

/// <summary>SQL Server provider over Microsoft.Data.SqlClient.</summary>
public sealed class SqlServerProvider : DbProviderBase
{
    /// <summary>
    /// SQL Server has no session level read-only switch. <c>ApplicationIntent=ReadOnly</c> only routes to a readable secondary
    /// (and is ignored on a plain instance), so the real control is a dedicated read login with no write permission.
    /// </summary>
    public static ReadOnlySession ReadOnlyRecipe { get; } = new(
        "sqlserver", ReadOnlyEnforcement.Routing, [],
        new Dictionary<string, string> { ["Application Intent"] = "ReadOnly" },
        "Routing hint only. Enforce with a read login that has db_datareader and nothing else; ApplicationIntent does not stop writes on a primary.");

    public SqlServerProvider(DialectCatalog? dialects = null)
        : base(EngineId.SqlServer, new SqlServerComposer(), ReadOnlyRecipe, new SqlServerFailureMapper(), dialects) { }

    protected override DbConnection NewConnection(string connectionString, ConnectionContext context)
    {
        var c = new SqlConnection(connectionString);
        if (context.Login?.Mode == AuthMode.Token && context.Login.Token is { IsEmpty: false } token) c.AccessToken = token.RevealString();
        return c;
    }
}
