using System.Net;
using DbDataKit.Config;

namespace DbDataProxy.Server.Configuration;

/// <summary>
/// Cross-reference and semantic checks that a type-and-range binder cannot express. Paths are relative to the Proxy section
/// (<c>DataSources.erp.Provider</c>, <c>Users[0].Name</c>); the loader prints them as <c>Proxy:DataSources:erp:Provider</c>.
/// Warnings are reported but do not stop startup.
/// </summary>
public sealed class ProxyConfigValidator(IReadOnlyList<string>? sourcesWithoutClassifier = null) : IConfigValidator<ProxyOptions>
{
    /// <summary>Provider names the server registers (a test compares this with the registered <c>IDbProvider</c>s).</summary>
    public static readonly IReadOnlyList<string> KnownProviders = ["odbc", "sqlserver", "duckdb"];

    /// <summary>Providers that can connect with the caller's own credentials (DuckDB has no user authentication).</summary>
    public static readonly IReadOnlyList<string> PassThroughProviders = ["odbc", "sqlserver"];

    private static readonly string[] MetadataModes = ["auto", "odbc", "informationschema"];

    public void Validate(ProxyOptions o, IConfigValidationContext cx)
    {
        ValidateListenAndTls(o, cx);
        ValidateManagement(o, cx);
        ValidateCompression(o.Compression, "Compression", cx);
        ValidateDataSources(o, cx);
        ValidateUsers(o, cx);
        ValidateJwt(o, cx);

        if (!string.IsNullOrWhiteSpace(o.DataDirectory))
        {
            try { _ = Path.GetFullPath(o.DataDirectory); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                cx.Error("DataDirectory", "`DataDirectory` is not a valid path.", "Use an absolute directory path.");
            }
        }
        // Only worth a warning when the new default would change something: sources that rely on the default Classifier.
        if (o.SchemaVersion < ProxyConfig.LatestSchemaVersion && sourcesWithoutClassifier is { Count: > 0 })
            cx.Warning("SchemaVersion", $"Schema version {o.SchemaVersion}: the statement `Classifier` of {string.Join(", ", sourcesWithoutClassifier)} defaults to warn. " +
                $"Schema version {ProxyConfig.LatestSchemaVersion} defaults it to strict.",
                $"Set `Classifier` on each data source, then set `SchemaVersion` to {ProxyConfig.LatestSchemaVersion}.");
    }

    private static void ValidateManagement(ProxyOptions o, IConfigValidationContext cx)
    {
        if (!o.Management.Enabled) return;
        if (!IPAddress.TryParse(o.Management.Address, out _))
            cx.Error("Management.Address", $"`Address` is `{o.Management.Address}`, which is not an IP address.", "Use an IP address such as 127.0.0.1 or 0.0.0.0.");
        if (o.Tls.Mode == TlsMode.None)
            cx.Error("Management.Enabled", "The management API is https only, and `Tls.Mode` is None.", "Use SelfSigned, File or Store, or leave the management API off.");
        if (o.Management.Port == o.Listen.Port && o.Listen.Port != 0)
            cx.Error("Management.Port", "The management API has its own port; it is the Flight port here.", $"Choose another port than {o.Listen.Port}.");
    }

    private static void ValidateListenAndTls(ProxyOptions o, IConfigValidationContext cx)
    {
        if (!IPAddress.TryParse(o.Listen.Address, out var address))
            cx.Error("Listen.Address", $"`Address` is `{o.Listen.Address}`, which is not an IP address.", "Use an IP address such as 0.0.0.0, 127.0.0.1 or ::1.");

        switch (o.Tls.Mode)
        {
            case TlsMode.File:
                if (string.IsNullOrWhiteSpace(o.Tls.CertificatePath))
                    cx.Error("Tls.CertificatePath", "`CertificatePath` is required when `Tls.Mode` is File.", "Set the path of the PFX file, or use SelfSigned.");
                break;
            case TlsMode.Store:
                if (string.IsNullOrWhiteSpace(o.Tls.CertificateThumbprint) && string.IsNullOrWhiteSpace(o.Tls.CertificateSubject))
                    cx.Error("Tls.CertificateThumbprint", "`CertificateThumbprint` or `CertificateSubject` is required when `Tls.Mode` is Store.", "Set the thumbprint (or a subject name) of the certificate in the store, or use SelfSigned.");
                break;
            case TlsMode.None:
                if (address is not null && !IPAddress.IsLoopback(address))
                    cx.Warning("Tls.Mode", "TLS is off and the proxy listens on a non-loopback address: credentials and data travel in clear text.", "Use SelfSigned, File or Store outside local testing.");
                if (!string.IsNullOrWhiteSpace(o.Tls.CertificatePath) || !string.IsNullOrWhiteSpace(o.Tls.CertificatePassword))
                    cx.Warning("Tls.CertificatePath", "A certificate is configured but `Tls.Mode` is None, so it is not used.");
                break;
        }
        for (int i = 0; i < o.Tls.Hostnames.Count; i++)
            if (string.IsNullOrWhiteSpace(o.Tls.Hostnames[i]))
                cx.Error($"Tls.Hostnames[{i}]", "A host name is empty.", "Remove the entry or give a DNS name or IP address.");
    }

    private static void ValidateCompression(CompressionOptions c, string path, IConfigValidationContext cx)
    {
        string codec = (c.Codec ?? "").ToLowerInvariant();
        switch (c.Mode)
        {
            case CompressionMode.Arrow when codec is not ("zstd" or "lz4"):
                cx.Error($"{path}.Codec", $"`Codec` is `{c.Codec}`, which Arrow compression does not support.", "Use zstd or lz4.");
                break;
            case CompressionMode.Grpc when codec != "gzip":
                cx.Error($"{path}.Codec", $"`Codec` is `{c.Codec}`, which gRPC compression does not support.", "Use gzip.");
                break;
        }
    }

    private static void ValidateDataSources(ProxyOptions o, IConfigValidationContext cx)
    {
        foreach (var (name, source) in o.DataSources)
        {
            string at = $"DataSources.{name}";
            if (string.IsNullOrWhiteSpace(name) || name != name.Trim())
                cx.Error(at, $"The data source name `{name}` is empty or has leading or trailing spaces.", "Clients select a data source by this exact name.");

            string provider = (source.Provider ?? "").ToLowerInvariant();
            if (!KnownProviders.Contains(provider))
                cx.Error($"{at}.Provider", $"`Provider` is `{source.Provider}`.", $"One of: {string.Join(", ", KnownProviders)}.");

            if (string.IsNullOrWhiteSpace(source.ConnectionString))
                cx.Error($"{at}.ConnectionString", "`ConnectionString` is required.", "Give a connection string or a secret reference (secret:, env:, file:, dpapi:).");

            string bitness = (source.Bitness ?? "").ToLowerInvariant();
            if (bitness is not ("x64" or "x86"))
                cx.Error($"{at}.Bitness", $"`Bitness` is `{source.Bitness}`.", "Use x64 or x86.");
            else if (bitness == "x86")
            {
                if (provider != "odbc")
                    cx.Error($"{at}.Bitness", "Bitness x86 works only with the odbc provider (the 32-bit worker hosts ODBC drivers).", "Use Provider odbc, or Bitness x64.");
                else if (!OperatingSystem.IsWindows())
                    cx.Warning($"{at}.Bitness", "Bitness x86 needs the 32-bit worker, which exists only on Windows.");
            }

            if (source.PassThrough && KnownProviders.Contains(provider) && !PassThroughProviders.Contains(provider))
                cx.Error($"{at}.PassThrough", $"The {provider} provider cannot connect with the caller's credentials.", $"Pass-through works with: {string.Join(", ", PassThroughProviders)}.");

            if (!MetadataModes.Contains((source.Metadata ?? "").ToLowerInvariant()))
                cx.Error($"{at}.Metadata", $"`Metadata` is `{source.Metadata}`.", $"One of: {string.Join(", ", MetadataModes)}.");

            for (int i = 0; i < source.AllowedDirectories.Count; i++)
            {
                string dir = source.AllowedDirectories[i];
                if (provider != "duckdb")
                    cx.Error($"{at}.AllowedDirectories", "`AllowedDirectories` works only with the duckdb provider (it lists directories the DuckDB sandbox lets queries read).", "Remove it or use Provider duckdb.");
                else if (string.IsNullOrWhiteSpace(dir) || dir.Any(char.IsControl) || !Path.IsPathFullyQualified(dir))
                    cx.Error($"{at}.AllowedDirectories[{i}]", "A directory must be an absolute path without control characters.", "Example: C:/data/parquet or /srv/data/parquet.");
            }
            if (source.AllowedDirectories.Count > 0 && provider == "duckdb" && source.AllowWrites)
                cx.Warning($"{at}.AllowedDirectories", "The source has `AllowWrites`, so it is not sandboxed and `AllowedDirectories` has no effect.", "Remove it, or drop `AllowWrites` to sandbox the source.");
            if (source.MaxConcurrentQueries > 0 && source.MaxConcurrentQueriesPerUser > source.MaxConcurrentQueries)
                cx.Warning($"{at}.MaxConcurrentQueriesPerUser", "The per-user cap is above the data source cap, so it can never be reached.", "Lower it or raise `MaxConcurrentQueries`.");

            if (source.Compression is { } own) ValidateCompression(own, $"{at}.Compression", cx);
        }
    }

    private static void ValidateUsers(ProxyOptions o, IConfigValidationContext cx)
    {
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < o.Users.Count; i++)
        {
            var user = o.Users[i];
            string at = $"Users[{i}]";
            if (string.IsNullOrWhiteSpace(user.Name))
                cx.Error($"{at}.Name", "`Name` is empty.", "Every user needs a name.");
            else if (seen.TryGetValue(user.Name, out int first))
                cx.Error($"{at}.Name", $"The user `{user.Name}` is defined twice (also Users[{first}]); names are not case sensitive.", "Merge the entries or rename one.");
            else
                seen[user.Name] = i;

            if (user.AuthType == UserAuthType.Password && string.IsNullOrWhiteSpace(user.PasswordHash))
                cx.Error($"{at}.PasswordHash", "A password user needs a `PasswordHash`; the user could never sign in.", "Create one with: dbdataproxy secret hash");
            if (user.AuthType == UserAuthType.Windows && !string.IsNullOrWhiteSpace(user.PasswordHash))
                cx.Warning($"{at}.PasswordHash", "A Windows user is checked by the operating system; `PasswordHash` is ignored.");

            CheckReferences(o, user.DataSources, user.DefaultDataSource, at, cx);
            if (user.DataSources.Count == 0)
                cx.Warning($"{at}.DataSources", "The user may not use any data source.", "List data source names, or \"*\" for all.");
        }
    }

    private static void ValidateJwt(ProxyOptions o, IConfigValidationContext cx)
    {
        var jwt = o.Auth.Jwt;
        if (!jwt.Enabled) return;
        if (string.IsNullOrWhiteSpace(jwt.Audience))
            cx.Error("Auth.Jwt.Audience", "`Audience` is required when JWT authentication is enabled (Authority or SigningKey is set); every token would be refused.", "Set the expected audience, for example api://dbproxy.");
        if (!string.IsNullOrWhiteSpace(jwt.Authority) && !(Uri.TryCreate(jwt.Authority, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"))
            cx.Error("Auth.Jwt.Authority", $"`Authority` is `{jwt.Authority}`, which is not an http(s) URL.", "For Entra ID: https://login.microsoftonline.com/{tenant}/v2.0");
        if (jwt.Mappings.Count == 0)
            cx.Warning("Auth.Jwt.Mappings", "No mappings: a token that matches no mapping is refused, so every token will be.", "Map a claim value to data sources.");
        for (int i = 0; i < jwt.Mappings.Count; i++)
        {
            var m = jwt.Mappings[i];
            string at = $"Auth.Jwt.Mappings[{i}]";
            if (string.IsNullOrWhiteSpace(m.Claim)) cx.Error($"{at}.Claim", "`Claim` is empty.", "Name the claim to inspect, for example groups or scp.");
            if (string.IsNullOrWhiteSpace(m.Value)) cx.Error($"{at}.Value", "`Value` is empty.", "Give the claim value that triggers this mapping.");
            CheckReferences(o, m.DataSources, m.DefaultDataSource, at, cx);
        }
    }

    /// <summary>Data source lists (users, token mappings) may name only defined sources or "*"; a default must be one the grant allows.</summary>
    private static void CheckReferences(ProxyOptions o, List<string> granted, string? defaultSource, string at, IConfigValidationContext cx)
    {
        for (int j = 0; j < granted.Count; j++)
        {
            if (granted[j] != "*" && !o.DataSources.ContainsKey(granted[j]))
                cx.Error($"{at}.DataSources[{j}]", $"The data source `{granted[j]}` is not defined under DataSources.", DidYouMean(o, granted[j]));
        }
        if (string.IsNullOrWhiteSpace(defaultSource)) return;
        if (!o.DataSources.ContainsKey(defaultSource))
            cx.Error($"{at}.DefaultDataSource", $"The data source `{defaultSource}` is not defined under DataSources.", DidYouMean(o, defaultSource));
        else if (!granted.Contains("*") && !granted.Contains(defaultSource, StringComparer.OrdinalIgnoreCase))
            cx.Error($"{at}.DefaultDataSource", $"`DefaultDataSource` `{defaultSource}` is not in this entry's `DataSources`.", "Add it to the list, or choose another default.");
    }

    private static string? DidYouMean(ProxyOptions o, string name)
    {
        var near = DbDataKit.Config.Yaml.DidYouMean.Closest(name, o.DataSources.Keys);
        return near is null ? (o.DataSources.Count == 0 ? "No data sources are defined." : $"Defined: {string.Join(", ", o.DataSources.Keys)}.") : $"Did you mean `{near}`?";
    }
}
