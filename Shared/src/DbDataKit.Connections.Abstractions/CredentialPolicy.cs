using DbDataKit.Secrets;

namespace DbDataKit.Connections;

/// <summary>A connection-string property or value was refused by policy. The message names the key and rule, never a value.</summary>
public sealed class ConnectionPolicyException(string rule, string? key = null)
    : Exception(key is null ? "Connection policy violation: " + rule : $"Connection policy violation: {rule} ('{key}')")
{
    public string Rule { get; } = rule;
    public string? Key { get; } = key;
}

/// <summary>
/// The deny-list for <c>properties</c> (and descriptor <c>fixed_properties</c>): credential keys (shared with the Plan 02
/// <see cref="CredentialKeyDetector"/>), host and identity keys, integrated-security style keys and TLS keys (which have their own
/// fields). Credentials are applied last by the composer and can never be overridden from here (Sync 9.8).
/// </summary>
public static class CredentialPolicy
{
    /// <summary>Normalised (lower case, no space, underscore, dash or dot) keys that select an identity, host or login mode.</summary>
    private static readonly HashSet<string> Denied = new(StringComparer.Ordinal)
    {
        "userid", "uid", "user", "username", "login", "loginname", "role", "roleid",
        "server", "host", "hostname", "address", "addr", "networkaddress", "datasource", "dsn", "filedsn", "savefile", "driver",
        "port", "database", "initialcatalog", "dbq", "attachdbfilename", "failoverpartner",
        "persistsecurityinfo", "applicationintent", "multisubnetfailover",
        "encrypt", "trustservercertificate", "sslmode", "ssl", "sslcert", "sslkey", "sslrootcert", "rootcertificate",
        "hostnameincertificate", "servercertificate",
        "connecttimeout", "connectiontimeout", "logintimeout", "timeout", "commandtimeout", "accessmode",
        "options", "searchpath",
    };

    /// <summary>Normalised keys that select integrated (pass-through-unsafe) authentication; stripped when injecting a pass-through credential.</summary>
    public static readonly IReadOnlySet<string> IntegratedKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "integratedsecurity", "trustedconnection", "authentication", "authenticationtype", "authtype", "authmethod",
        "gsslib", "krbsrvname", "kerberosservicename", "kerberos", "sspi", "sspiprotocol", "useraccesstoken", "accesstoken",
        "integratedsecurityprovider", "trustedauthentication", "winauth", "windowsauthentication", "usewindowsauth", "gssencmode",
        "krbservicename", "servicename", "clientcertificate", "clientcertificatefile", "clientcertificatekey", "includerealm",
        "passfile", "sslcertificate", "sslpassword", "sslkeypassword", "usesslstream", "authenticationmode",
    };

    /// <summary>The comparison form of a key.</summary>
    public static string NormalizeKey(string key) =>
        key.Trim().Replace(" ", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal)
           .Replace("-", "", StringComparison.Ordinal).Replace(".", "", StringComparison.Ordinal).ToLowerInvariant();

    public static bool IsDeniedProperty(string key) =>
        Denied.Contains(NormalizeKey(key)) || CredentialKeyDetector.IsCredentialPropertyKey(key) || IntegratedKeys.Contains(NormalizeKey(key));

    public static bool IsIntegratedKey(string key) => IntegratedKeys.Contains(NormalizeKey(key));

    /// <summary>Throws <see cref="ConnectionPolicyException"/> for the first denied key, empty key, or key/value with control characters.</summary>
    public static void ValidateProperties(IEnumerable<KeyValuePair<string, string>> properties)
    {
        foreach (var (key, value) in properties)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Any(char.IsControl)) throw new ConnectionPolicyException("property key is empty or has control characters");
            if (value is not null && value.Any(char.IsControl)) throw new ConnectionPolicyException("property value has control characters", key);
            if (IsDeniedProperty(key)) throw new ConnectionPolicyException("property key is reserved (credential, host, identity, TLS or authentication key)", key);
        }
    }
}
