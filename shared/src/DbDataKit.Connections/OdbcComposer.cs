using System.Globalization;
using DbDataKit.Dialect;
using DbDataKit.Odbc.Metadata;

namespace DbDataKit.Connections;

/// <summary>What the composer knows about a few ODBC drivers: how they spell encryption, ports and read-only intent.</summary>
public enum OdbcDriverProfile { Unknown, SqlServer, Postgres, DuckDb }

/// <summary>
/// ODBC composer: values are brace quoted by the <see cref="OdbcConnectionString"/> writer (never double quoted), user authored
/// strings go through <see cref="OdbcConnectionPolicy"/> (driver and DSN allow-lists, no FILEDSN or SAVEFILE) before they are read,
/// credentials are written last, and the driver profile decides TLS and read-only keys.
/// </summary>
public sealed class OdbcComposer(string? fixedDriver = null) : ComposerBase(new EngineId("odbc"))
{
    /// <summary>A driver name supplied by the engine descriptor, used when the string names neither a driver nor a DSN.</summary>
    protected override void ApplyFixed(IKeyBag bag, ConnectionContext ctx)
    {
        if (fixedDriver is not null && !bag.TryGet("Driver", out _) && !bag.TryGet("DSN", out _)) bag.Set("Driver", fixedDriver);
    }

    protected override string UserKey => "UID";
    protected override string PasswordKey => "PWD";

    protected override IKeyBag NewBag() => new OdbcBag();

    protected override IKeyBag ParseText(string text)
    {
        if (!OdbcConnectionString.TryParseStrict(OdbcConnectionString.Normalize(text), out _, out _))
            throw new ConnectionPolicyException("the ODBC connection string is malformed");
        return OdbcBag.Parse(text);
    }

    protected override void CheckBaseText(string text, ConnectionContext? ctx)
    {
        var policy = new OdbcConnectionPolicy
        {
            AllowedDrivers = ctx?.Policy.AllowedOdbcDrivers,
            AllowedDsns = ctx?.Policy.AllowedOdbcDsns,
        };
        if (!policy.TryValidate(text, out string? reason)) throw new ConnectionPolicyException("ODBC policy: " + reason);
    }

    /// <summary>The profile of the driver named in the bag (or the DSN's, which is unknown).</summary>
    public static OdbcDriverProfile ProfileOf(IKeyBag bag)
    {
        if (!bag.TryGet("Driver", out var driver)) return OdbcDriverProfile.Unknown;
        return ProfileOfDriverName(driver);
    }

    public static OdbcDriverProfile ProfileOfDriverName(string driver)
    {
        if (driver.Contains("SQL Server", StringComparison.OrdinalIgnoreCase)) return OdbcDriverProfile.SqlServer;
        if (driver.Contains("PostgreSQL", StringComparison.OrdinalIgnoreCase)) return OdbcDriverProfile.Postgres;
        if (driver.Contains("DuckDB", StringComparison.OrdinalIgnoreCase)) return OdbcDriverProfile.DuckDb;
        return OdbcDriverProfile.Unknown;
    }

    protected override void SetIntegrated(IKeyBag bag) => bag.Set("Trusted_Connection", "yes");

    protected override void ApplyAddress(IKeyBag bag, AddressSpec a, ConnectionContext ctx)
    {
        var profile = ProfileOf(bag);
        if (a.Host is not null)
        {
            // the Microsoft driver has no separate port keyword
            bag.Set("Server", profile == OdbcDriverProfile.SqlServer && a.Port is int p ? a.Host + "," + p.ToString(CultureInfo.InvariantCulture) : a.Host);
            if (profile != OdbcDriverProfile.SqlServer && a.Port is int port) bag.Set("Port", port.ToString(CultureInfo.InvariantCulture));
        }
        if (a.Database is not null) bag.Set("Database", a.Database);
    }

    protected override void ApplyTls(IKeyBag bag, TlsSpec tls, ConnectionContext ctx)
    {
        switch (ProfileOf(bag))
        {
            case OdbcDriverProfile.SqlServer:
                bag.Set("Encrypt", tls.Encrypt ? "yes" : "no");
                bag.Set("TrustServerCertificate", tls.EffectiveVerify ? "no" : "yes");
                break;
            case OdbcDriverProfile.Postgres:
                bag.Set("sslmode", !tls.Encrypt ? "disable" : tls.EffectiveVerify ? "verify-full" : "require");
                if (tls.CaFile is not null && tls.Encrypt) bag.Set("sslrootcert", tls.CaFile);
                break;
        }
    }

    protected override void ApplyTimeouts(IKeyBag bag, TimeoutSpec t, ConnectionContext ctx)
    {
        if (ProfileOf(bag) == OdbcDriverProfile.SqlServer) bag.Set("Login Timeout", t.ConnectS.ToString(CultureInfo.InvariantCulture));
    }

    protected override IEnumerable<KeyValuePair<string, string>> ReadOnlyKeys(IKeyBag bag, ConnectionContext ctx) => ProfileOf(bag) switch
    {
        OdbcDriverProfile.SqlServer => [new("ApplicationIntent", "ReadOnly")],
        OdbcDriverProfile.Postgres => [new("ReadOnly", "1")],
        OdbcDriverProfile.DuckDb => [new("access_mode", "READ_ONLY")],
        _ => [],
    };
}
