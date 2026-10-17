using System.Data.Common;
using System.Globalization;
using DbDataKit.Connections;
using DbDataKit.Odbc.Metadata;

namespace DbDataProxy.Data;

/// <summary>
/// What an administrator already said in a data source's connection string about TLS and timeouts, as the shared <see cref="TlsSpec"/> and
/// <see cref="TimeoutSpec"/>. The shared composers write TLS and timeout keys from the definition and would overrule the string, so an explicit
/// value in the string (for example <c>TrustServerCertificate=True</c>) is carried into the definition to be kept; a key that is not there gets
/// the secure default (encrypt and verify on).
/// </summary>
public static class ConnectionHints
{
    private static readonly string[] ConnectTimeoutKeys = ["connecttimeout", "connectiontimeout", "logintimeout", "timeout"];

    /// <summary>The explicit TLS settings of the string, over the secure defaults. Unreadable strings give the defaults (the composer rejects them with its own error).</summary>
    public static TlsSpec TlsFrom(string engine, string connectionString)
    {
        var tls = new TlsSpec();
        var keys = Read(engine, connectionString);

        if (keys.TryGetValue("encrypt", out var encrypt) && ParseBool(encrypt) is bool e) tls.Encrypt = e;
        if (keys.TryGetValue("trustservercertificate", out var trust) && ParseBool(trust) is true) tls.TrustServerCertificate = true;

        // ODBC PostgreSQL spells it sslmode.
        if (keys.TryGetValue("sslmode", out var mode))
            switch (mode.Trim().ToLowerInvariant())
            {
                case "disable": tls.Encrypt = false; break;
                case "allow" or "prefer" or "require": tls.TrustServerCertificate = true; break;
            }
        return tls;
    }

    /// <summary>True when the string opts out of encryption or of certificate validation explicitly (what the startup warning is about).</summary>
    public static bool OptsOutOfTls(string engine, string connectionString) =>
        TlsFrom(engine, connectionString) is { Encrypt: false } or { TrustServerCertificate: true };

    /// <summary>Connect and command timeouts the string sets; anything it does not set keeps the shared default. A data source's own command timeout wins.</summary>
    public static TimeoutSpec TimeoutsFrom(string engine, string connectionString, int? commandTimeoutSeconds)
    {
        var timeouts = new TimeoutSpec();
        var keys = Read(engine, connectionString);
        foreach (var key in ConnectTimeoutKeys)
            if (keys.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds)) { timeouts.ConnectS = seconds; break; }
        if (keys.TryGetValue("commandtimeout", out var c) && int.TryParse(c, NumberStyles.None, CultureInfo.InvariantCulture, out int command)) timeouts.CommandS = command;
        if (commandTimeoutSeconds is > 0) timeouts.CommandS = commandTimeoutSeconds.Value;
        return timeouts;
    }

    private static bool? ParseBool(string value) => value.Trim().ToLowerInvariant() switch
    {
        "true" or "yes" or "1" or "mandatory" or "strict" or "on" => true,
        "false" or "no" or "0" or "optional" or "off" => false,
        _ => null,
    };

    private static Dictionary<string, string> Read(string engine, string connectionString)
    {
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(connectionString)) return keys;
        try
        {
            if (engine.Equals("odbc", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var (key, value) in OdbcConnectionString.Parse(OdbcConnectionString.Normalize(connectionString)))
                    keys[CredentialPolicy.NormalizeKey(key)] = value;
            }
            else
            {
                var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
                foreach (string key in builder.Keys) keys[CredentialPolicy.NormalizeKey(key)] = Convert.ToString(builder[key], CultureInfo.InvariantCulture) ?? "";
            }
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException) { keys.Clear(); }
        return keys;
    }
}
