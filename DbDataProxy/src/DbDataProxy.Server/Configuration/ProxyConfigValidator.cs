using DbDataKit.Serving.FlightSql;
using System.Net;
using DbDataKit.Config;

namespace DbDataProxy.Server.Configuration;

/// <summary>
/// Cross-reference and semantic checks that a type-and-range binder cannot express. Paths are relative to the Proxy section
/// (<c>DataSources.erp.Provider</c>, <c>Auth.Jwt.Mappings[0].Claim</c>); the loader prints them as <c>Proxy:DataSources:erp:Provider</c>.
/// Warnings are reported but do not stop startup.
/// </summary>
public sealed class ProxyConfigValidator(IReadOnlyList<string>? sourcesWithoutClassifier = null) : IConfigValidator<ProxyOptions>
{
    /// <summary>Provider names the server registers (a test compares this with the registered <c>IDbProvider</c>s).</summary>
    public static readonly IReadOnlyList<string> KnownProviders = ["odbc", "sqlserver", "duckdb", "postgres", "flightsql"];

    /// <summary>Providers that can connect with the caller's own credentials (DuckDB has no user authentication).</summary>
    public static readonly IReadOnlyList<string> PassThroughProviders = ["odbc", "sqlserver", "postgres"];

    private static readonly string[] MetadataModes = ["auto", "odbc", "informationschema"];

    public void Validate(ProxyOptions o, IConfigValidationContext cx)
    {
        ValidateListenAndTls(o, cx);
        ValidateManagement(o, cx);
        ValidateModules(o, cx);
        ValidateIdentity(o, cx);
        ValidateApps(o, cx);
        ValidateStore(o, cx);
        ValidateCompression(o.Compression, "Compression", cx);
        ValidateDataSources(o, cx);
        ValidateJwt(o, cx);
        ValidateSemantic(o, cx);
        ValidateState(o, cx);

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

    private static void ValidateState(ProxyOptions o, IConfigValidationContext cx)
    {
        try
        {
            var settings = o.State.ToSettings();
            _ = settings.EngineId;
            if (!settings.IsSqlite && string.IsNullOrWhiteSpace(settings.ConnectionString))
                cx.Error("State.Connection", $"The state engine is `{o.State.Engine}`, which needs a connection string.", "Give the connection string of a database for the proxy's state, with the password as a secret reference, or use `sqlite`.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("is not one of sqlite", StringComparison.Ordinal))
        {
            cx.Error("State.Engine", $"`{o.State.Engine}` is not a state engine.", "Use `sqlite`, `mssql` or `postgres`.");
        }
        catch (InvalidOperationException ex)
        {
            cx.Error("State", ex.Message, "See the state section in the README.");
        }
    }

    private static void ValidateSemantic(ProxyOptions o, IConfigValidationContext cx)
    {
        for (int i = 0; i < o.Semantic.Models.Count; i++)
        {
            var model = o.Semantic.Models[i];
            string at = $"Semantic.Models[{i}]";
            if (string.IsNullOrWhiteSpace(model.Catalog))
                cx.Error($"{at}.Catalog", "`Catalog` is empty.", "Name the catalog file of the model.");
            foreach (var (source, dataSource) in model.Sources)
                if (!o.DataSources.ContainsKey(dataSource))
                    cx.Error($"{at}.Sources.{source}", $"The catalog source `{source}` is mapped to `{dataSource}`, which is not a data source.", $"Use one of: {string.Join(", ", o.DataSources.Keys)}.");
            foreach (var (source, connection) in model.Ssas)
            {
                if (string.IsNullOrWhiteSpace(connection))
                    cx.Error($"{at}.Ssas.{source}", $"The connection string of `{source}` is empty.", "Give the connection string of the Analysis Services server, or a secret reference to it.");
                else if (!OperatingSystem.IsWindows())
                    cx.Error($"{at}.Ssas.{source}", "Analysis Services sources run only on Windows (the ADOMD.NET client does).", "Run this server on Windows, or leave this source out.");
            }
        }
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

    private static void ValidateModules(ProxyOptions o, IConfigValidationContext cx)
    {
        foreach (var name in o.Modules.Where(m => !ProxyModules.Known.Contains(m, StringComparer.OrdinalIgnoreCase)))
            cx.Error("Modules", $"`{name}` is not a module of this server.", $"Use {string.Join(", ", ProxyModules.Known)}.");
        if (o.Modules.Count != o.Modules.Distinct(StringComparer.OrdinalIgnoreCase).Count())
            cx.Error("Modules", "A module is listed twice.", "List each module once.");
        if (!ProxyModules.Known.Any(m => ProxyModules.Has(o, m)))
            cx.Error("Modules", "No module is enabled, so the server would serve nothing.", $"List at least one of {string.Join(", ", ProxyModules.Known)}.");
    }

    private static void ValidateStore(ProxyOptions o, IConfigValidationContext cx)
    {
        if (!ProxyModules.Has(o, ProxyModules.Store)) return;
        if (!IPAddress.TryParse(o.Store.Address, out _))
            cx.Error("Store.Address", $"`Address` is `{o.Store.Address}`, which is not an IP address.", "Use an IP address such as 0.0.0.0 or 127.0.0.1.");
        if (o.Store.Port == o.Listen.Port && o.Listen.Port != 0 && ProxyModules.Has(o, ProxyModules.FlightSql))
            cx.Error("Store.Port", "The store module has its own port; it is the Flight port here.", $"Choose another port than {o.Listen.Port}.");
        if (o.Management.Enabled && o.Store.Port == o.Management.Port)
            cx.Error("Store.Port", "The store module has its own port; it is the management API's here.", $"Choose another port than {o.Management.Port}.");
        if (ProxyModules.Has(o, ProxyModules.Identity) && o.Store.Port == o.Identity.Port)
            cx.Error("Store.Port", "The store module has its own port; it is the identity home's here.", $"Choose another port than {o.Identity.Port}.");
        if (ProxyModules.Has(o, ProxyModules.Apps) && o.Store.Port == o.Apps.Port)
            cx.Error("Store.Port", "The store module has its own port; it is the apps host's here.", $"Choose another port than {o.Apps.Port}.");
    }

    private static void ValidateApps(ProxyOptions o, IConfigValidationContext cx)
    {
        if (!ProxyModules.Has(o, ProxyModules.Apps)) return;
        if (!IPAddress.TryParse(o.Apps.Address, out _))
            cx.Error("Apps.Address", $"`Address` is `{o.Apps.Address}`, which is not an IP address.", "Use an IP address such as 0.0.0.0 or 127.0.0.1.");
        if (string.IsNullOrWhiteSpace(o.Apps.Home.ClientId))
            cx.Error("Apps.Home.ClientId", "The apps host signs people in through an identity home and needs the id of the client registered there for it.", "Register one (`identity client-add` with the redirect URI <public url>/apps/-/callback) and set `Home.ClientId`.");
        if (string.IsNullOrWhiteSpace(o.Apps.Home.Authority))
        {
            if (!ProxyModules.Has(o, ProxyModules.Identity))
                cx.Error("Apps.Home.Authority", "The apps host has no identity home to sign people in through: this instance serves none.", "Set `Home.Authority` to the home's address, or list `identity` in `Modules`.");
        }
        else if (!Uri.TryCreate(o.Apps.Home.Authority, UriKind.Absolute, out var home) || home.Scheme != Uri.UriSchemeHttps)
            cx.Error("Apps.Home.Authority", $"`Authority` is `{o.Apps.Home.Authority}`, which is not an https address.", "Use the home's address, such as https://identity.example.com.");
        if (!string.IsNullOrWhiteSpace(o.Apps.PublicUrl)
            && (!Uri.TryCreate(o.Apps.PublicUrl, UriKind.Absolute, out var pub) || pub.Scheme != Uri.UriSchemeHttps || pub.AbsolutePath != "/" || pub.Query.Length > 0 || pub.Fragment.Length > 0))
            cx.Error("Apps.PublicUrl", $"`PublicUrl` is `{o.Apps.PublicUrl}`, which is not an https address without a path.", "Use the address people reach the apps host at, such as https://apps.example.com.");
        if (o.Tls.Mode == TlsMode.None)
            cx.Error("Modules", "The apps host is https only (a sandboxed app and its sign-in need a secure origin), and `Tls.Mode` is None.", "Use SelfSigned, File or Store, or leave `apps` out of `Modules`.");
        if (o.Apps.Port == o.Listen.Port && o.Listen.Port != 0 && ProxyModules.Has(o, ProxyModules.FlightSql))
            cx.Error("Apps.Port", "The apps host has its own port; it is the Flight port here.", $"Choose another port than {o.Listen.Port}.");
        if (o.Management.Enabled && o.Apps.Port == o.Management.Port)
            cx.Error("Apps.Port", "The apps host has its own port; it is the management API's here.", $"Choose another port than {o.Management.Port}.");
        if (ProxyModules.Has(o, ProxyModules.Identity) && o.Apps.Port == o.Identity.Port)
            cx.Error("Apps.Port", "The apps host has its own port; it is the identity home's here.", $"Choose another port than {o.Identity.Port}.");
    }

    private static void ValidateIdentity(ProxyOptions o, IConfigValidationContext cx)
    {
        if (!ProxyModules.Has(o, ProxyModules.Identity)) return;
        if (!IPAddress.TryParse(o.Identity.Address, out _))
            cx.Error("Identity.Address", $"`Address` is `{o.Identity.Address}`, which is not an IP address.", "Use an IP address such as 0.0.0.0 or 127.0.0.1.");
        if (o.Tls.Mode == TlsMode.None)
            cx.Error("Identity.Enabled", "The identity home is https only (a passkey needs a secure origin), and `Tls.Mode` is None.", "Use SelfSigned, File or Store, or leave the identity home off.");
        if (o.Identity.Port == o.Listen.Port && o.Listen.Port != 0)
            cx.Error("Identity.Port", "The identity home has its own port; it is the Flight port here.", $"Choose another port than {o.Listen.Port}.");
        if (o.Management.Enabled && o.Identity.Port == o.Management.Port)
            cx.Error("Identity.Port", "The identity home has its own port; it is the management API's here.", $"Choose another port than {o.Management.Port}.");
        if (!string.IsNullOrWhiteSpace(o.Identity.PublicUrl)
            && (!Uri.TryCreate(o.Identity.PublicUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps || url.AbsolutePath != "/" || url.Query.Length > 0 || url.Fragment.Length > 0))
            cx.Error("Identity.PublicUrl", $"`PublicUrl` is `{o.Identity.PublicUrl}`, which is not an https address without a path.", "Use the address people reach the home at, such as https://identity.example.com.");
        var entra = o.Identity.Entra;
        if (!string.IsNullOrWhiteSpace(entra.Authority))
        {
            // https, except a stand-in tenant on this machine
            if (!Uri.TryCreate(entra.Authority, UriKind.Absolute, out var authority) || !(authority.Scheme == Uri.UriSchemeHttps || authority.Scheme == Uri.UriSchemeHttp && authority.IsLoopback) || authority.Query.Length > 0)
                cx.Error("Identity.Entra.Authority", $"`Authority` is `{entra.Authority}`, which is not an https address.", "Use the tenant's issuer: https://login.microsoftonline.com/<tenant id>/v2.0.");
            if (string.IsNullOrWhiteSpace(entra.ClientId))
                cx.Error("Identity.Entra.ClientId", "Entra sign-in needs the id of the app registered in the tenant for the home.", "Set `ClientId`.");
            if (string.IsNullOrWhiteSpace(entra.ClientSecret))
                cx.Error("Identity.Entra.ClientSecret", "Entra sign-in needs the client secret of the app registered in the tenant for the home.", "Set `ClientSecret` to a secret reference, such as secret:dbdata:proxy:identity:entra.");
        }
        else if (!string.IsNullOrWhiteSpace(entra.ClientId) || !string.IsNullOrWhiteSpace(entra.ClientSecret))
            cx.Error("Identity.Entra.Authority", "Entra sign-in is set up without the tenant.", "Set `Authority` to the tenant's issuer, https://login.microsoftonline.com/<tenant id>/v2.0, or remove the `Entra` section.");
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

            if (provider == "flightsql" && source.AllowWrites)
                cx.Error($"{at}.AllowWrites", "A Flight SQL data source is read only: the client has no write path.", "Remove `AllowWrites`.");

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

            ValidateAccess(o, name, source, at, cx);

            if (source.Compression is { } own) ValidateCompression(own, $"{at}.Compression", cx);
        }
    }

    /// <summary>Which home users may use a data source: its <c>groups</c> (home group names) or <c>everyone</c>; a source naming neither is used by no home user.</summary>
    private static void ValidateAccess(ProxyOptions o, string name, DataSourceOptions source, string at, IConfigValidationContext cx)
    {
        for (int i = 0; i < source.Groups.Count; i++)
        {
            var group = source.Groups[i];
            if (string.IsNullOrWhiteSpace(group) || group != group.Trim() || group.Any(char.IsControl))
                cx.Error($"{at}.Groups[{i}]", "A group name is empty, has leading or trailing spaces, or has control characters.", "Name a group of the identity home exactly as the home shows it.");
        }
        if (source.Everyone && source.Groups.Count > 0)
            cx.Error($"{at}.Everyone", "The data source is open to everyone and also names groups; the groups would mean nothing.", "Remove `everyone`, or remove `groups`.");
        if (source.Default && o.DataSources.Where(d => d.Value.Default).Select(d => d.Key).Order(StringComparer.OrdinalIgnoreCase).First() is { } firstDefault && !string.Equals(firstDefault, name, StringComparison.OrdinalIgnoreCase))
            cx.Error($"{at}.Default", $"`{name}` and `{firstDefault}` are both the default data source; only one can be.", "Keep `default: true` on one of them.");
        // a pass-through source is decided by the database; the JWT mappings are the other way in, so a source only they reach is not forgotten
        if (!source.Everyone && source.Groups.Count == 0 && !source.PassThrough
            && !o.Auth.Jwt.Mappings.Any(m => m.DataSources.Contains("*") || m.DataSources.Contains(name, StringComparer.OrdinalIgnoreCase)))
            cx.Warning($"{at}.Groups", $"The data source `{name}` names no home group and is not open to everyone, so no user of the identity home may use it.",
                "List the home groups allowed to use it under `extensions.proxy.groups`, or set `extensions.proxy.everyone: true`.");
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
