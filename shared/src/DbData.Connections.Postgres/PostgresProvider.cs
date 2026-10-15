using System.Data.Common;
using System.Globalization;
using DbData.Dialect;
using Npgsql;

namespace DbData.Connections.Postgres;

/// <summary>
/// Npgsql composer. TLS is <c>SSL Mode=VerifyFull</c> by default; <c>tls.verify: false</c> (or trust) gives <c>Require</c> (encrypted, not verified), and
/// <c>tls.encrypt: false</c> gives <c>Disable</c>. <c>tls.ca_file</c> is the <c>Root Certificate</c>.
/// </summary>
public sealed class PostgresComposer() : BuilderComposer(EngineId.Postgres)
{
    protected override DbConnectionStringBuilder NewBuilder() => new NpgsqlConnectionStringBuilder();
    protected override string UserKey => "Username";
    protected override string PasswordKey => "Password";

    /// <summary>Npgsql has no integrated flag: GSS/SSPI is attempted when the server asks for it and no password is set, so nothing is written.</summary>
    protected override void SetIntegrated(IKeyBag bag) { }

    protected override void ApplyAddress(IKeyBag bag, AddressSpec a, ConnectionContext ctx)
    {
        if (a.Host is not null) bag.Set("Host", a.Host);
        if (a.Port is int p) bag.Set("Port", p.ToString(CultureInfo.InvariantCulture));
        if (a.Database is not null) bag.Set("Database", a.Database);
    }

    protected override void ApplyTls(IKeyBag bag, TlsSpec tls, ConnectionContext ctx)
    {
        bag.Set("SSL Mode", !tls.Encrypt ? "Disable" : tls.EffectiveVerify ? "VerifyFull" : "Require");
        if (tls.CaFile is not null && tls.Encrypt) bag.Set("Root Certificate", tls.CaFile);
    }

    protected override void ApplyTimeouts(IKeyBag bag, TimeoutSpec t, ConnectionContext ctx)
    {
        bag.Set("Timeout", t.ConnectS.ToString(CultureInfo.InvariantCulture));
        bag.Set("Command Timeout", t.CommandS.ToString(CultureInfo.InvariantCulture));
    }

    protected override IEnumerable<KeyValuePair<string, string>> ReadOnlyKeys(IKeyBag bag, ConnectionContext ctx) =>
        PostgresProvider.ReadOnlyRecipe.ConnectionStringKeys;
}

/// <summary>Maps Npgsql exceptions by SQLSTATE; <c>25006</c> (a write in a read-only transaction) is a permission failure.</summary>
public sealed class PostgresFailureMapper : IFailureMapper
{
    public ConnectionFailure? Map(Exception exception, EngineId engine)
    {
        if (exception is not PostgresException pg) return null;
        var kind = pg.SqlState switch
        {
            "25006" or "42501" => ConnectionFailureKind.Permission,
            "3D000" => ConnectionFailureKind.Login,
            _ => SqlStateMap.Classify(pg.SqlState) ?? ConnectionFailureKind.Other,
        };
        return new ConnectionFailure(kind, pg.SqlState, engine);
    }
}

/// <summary>PostgreSQL provider over Npgsql.</summary>
public sealed class PostgresProvider : DbProviderBase
{
    /// <summary>
    /// <c>default_transaction_read_only=on</c> as a startup option (so a pooled connection that is reset keeps it) and again as a session
    /// statement. The session can turn it off with <c>SET</c> or <c>BEGIN READ WRITE</c>, so this stops accidents, not a hostile
    /// statement: pair it with a read-only role and the Plan 05 statement classifier.
    /// </summary>
    public static ReadOnlySession ReadOnlyRecipe { get; } = new(
        "postgres", ReadOnlyEnforcement.SessionDefault, ["SET default_transaction_read_only = on"],
        new Dictionary<string, string> { ["Options"] = "-c default_transaction_read_only=on" },
        "A session can undo it. Use a read-only role for enforcement.");

    public PostgresProvider(DialectCatalog? dialects = null)
        : base(EngineId.Postgres, new PostgresComposer(), ReadOnlyRecipe, new PostgresFailureMapper(), dialects) { }

    protected override DbConnection NewConnection(string connectionString, ConnectionContext context) => new NpgsqlConnection(connectionString);
}
