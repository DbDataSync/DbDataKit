using DbDataProxy.Data;
using DbDataProxy.Server.Configuration;
using DbDataProxy.Server.Secrets;

namespace DbDataProxy.Server.Data;

/// <summary>Start-up checks of the data sources' connection strings that are about security posture, not validity.</summary>
public static class ConnectionStartupChecks
{
    /// <summary>
    /// SQL Server and PostgreSQL sources encrypt and verify the certificate by default. A source whose connection string says otherwise explicitly
    /// (<c>TrustServerCertificate=True</c>, <c>Encrypt=False</c>, <c>SSL Mode=Require</c>) keeps that setting, and gets one warning naming the source (never the string).
    /// Returns the names, for tests.
    /// </summary>
    public static IReadOnlyList<string> WarnAboutTlsOptOuts(ProxyOptions options, SecretsRuntime? secrets, ILogger log)
    {
        var named = new List<string>();
        foreach (var (name, source) in options.DataSources)
        {
            string engine = source.Provider.ToLowerInvariant();
            if (engine is not ("sqlserver" or "odbc" or "postgres")) continue;

            string connectionString;
            try { connectionString = secrets?.ResolveConnectionString($"Proxy:DataSources:{name}:ConnectionString", source.ConnectionString, source.Provider) ?? source.ConnectionString; }
            catch (SecretConfigurationException) { continue; } // already reported by the secrets check

            var tls = ConnectionHints.TlsFrom(engine, connectionString);
            if (tls.Encrypt && !tls.TrustServerCertificate) continue;

            named.Add(name);
            log.LogWarning(
                "Data source {Source} turns TLS protection off in its connection string ({Setting}). It keeps working as configured, but the default for SQL Server and PostgreSQL sources is to encrypt and verify the server certificate; " +
                "install a trusted certificate on the server (or pin it) and remove the setting.",
                name, !tls.Encrypt ? "Encrypt=false" : "TrustServerCertificate=true");
        }
        return named;
    }
}
