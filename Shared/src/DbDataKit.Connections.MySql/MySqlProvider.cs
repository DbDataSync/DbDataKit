using System.Data.Common;
using System.Globalization;
using DbDataKit.Dialect;
using MySqlConnector;

namespace DbDataKit.Connections.MySql;

/// <summary>
/// MySqlConnector composer (MySQL and MariaDB). TLS is <c>SSL Mode=VerifyFull</c> by default; <c>tls.verify: false</c> (or trust) gives <c>Required</c>
/// (encrypted, not verified), and <c>tls.encrypt: false</c> gives <c>None</c>. <c>tls.ca_file</c> is <c>SSL CA</c>.
/// </summary>
public sealed class MySqlComposer() : BuilderComposer(EngineId.MySql)
{
    protected override DbConnectionStringBuilder NewBuilder() => new MySqlConnectionStringBuilder();
    protected override string UserKey => "User ID";
    protected override string PasswordKey => "Password";

    /// <summary>MySQL has no integrated flag: an authentication plugin (LDAP, PAM, Kerberos) is server configuration, and the user name is still written.</summary>
    protected override void SetIntegrated(IKeyBag bag) { }

    protected override void ApplyAddress(IKeyBag bag, AddressSpec a, ConnectionContext ctx)
    {
        if (a.Host is not null) bag.Set("Server", a.Host);
        if (a.Port is int p) bag.Set("Port", p.ToString(CultureInfo.InvariantCulture));
        if (a.Database is not null) bag.Set("Database", a.Database);
    }

    protected override void ApplyTls(IKeyBag bag, TlsSpec tls, ConnectionContext ctx)
    {
        bag.Set("SSL Mode", !tls.Encrypt ? "None" : tls.EffectiveVerify ? "VerifyFull" : "Required");
        if (tls.CaFile is not null && tls.Encrypt) bag.Set("SSL CA", tls.CaFile);
    }

    protected override void ApplyTimeouts(IKeyBag bag, TimeoutSpec t, ConnectionContext ctx)
    {
        bag.Set("Connection Timeout", t.ConnectS.ToString(CultureInfo.InvariantCulture));
        bag.Set("Default Command Timeout", t.CommandS.ToString(CultureInfo.InvariantCulture));
    }
}

/// <summary>Maps MySqlConnector exceptions by server error number, then by SQLSTATE.</summary>
public sealed class MySqlFailureMapper : IFailureMapper
{
    private static readonly HashSet<int> Login = [1045, 1049, 1251, 1130, 1862, 1698];
    private static readonly HashSet<int> Permission = [1044, 1142, 1143, 1227, 1370, 1290];
    private static readonly HashSet<int> Network = [1040, 1042, 1043, 1053, 1077, 1129, 1152, 1153, 1158, 1159, 1160, 1161, 2002, 2003, 2006, 2013];
    private static readonly HashSet<int> Timeout = [1205, 1969, 3024];
    private static readonly HashSet<int> Syntax = [1054, 1064, 1065, 1146, 1109, 1051, 1066, 1060, 1110];

    public ConnectionFailure? Map(Exception exception, EngineId engine)
    {
        if (exception is not MySqlException my) return null;
        int n = my.Number;
        var kind = Login.Contains(n) ? ConnectionFailureKind.Login
            : Permission.Contains(n) ? ConnectionFailureKind.Permission
            : Timeout.Contains(n) ? ConnectionFailureKind.Timeout
            : Network.Contains(n) ? ConnectionFailureKind.Network
            : Syntax.Contains(n) ? ConnectionFailureKind.Syntax
            : SqlStateMap.Classify(my.SqlState) ?? ConnectionFailureKind.Other;
        return new ConnectionFailure(kind, n.ToString(CultureInfo.InvariantCulture), engine);
    }
}

/// <summary>MySQL and MariaDB provider over MySqlConnector.</summary>
public sealed class MySqlProvider : DbProviderBase
{
    /// <summary><c>SET SESSION TRANSACTION READ ONLY</c> after open. The session can undo it, so this stops accidents, not a hostile statement: pair it with a read-only account.</summary>
    public static ReadOnlySession ReadOnlyRecipe { get; } = new(
        "mysql", ReadOnlyEnforcement.SessionDefault, ["SET SESSION TRANSACTION READ ONLY"], null,
        "A session can undo it. Use a read-only account for enforcement.");

    public MySqlProvider(DialectCatalog? dialects = null)
        : base(EngineId.MySql, new MySqlComposer(), ReadOnlyRecipe, new MySqlFailureMapper(), dialects) { }

    protected override DbConnection NewConnection(string connectionString, ConnectionContext context) => new MySqlConnection(connectionString);
}
