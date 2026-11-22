using DbDataKit.Hosting.Startup;
using DbDataKit.Serving.FlightSql;
using System.Net;
using DbDataKit.Config;
using DbDataGateway.Server.Hosting;

namespace DbDataGateway.Server.Configuration;

/// <summary>
/// Cross-reference and semantic checks that a type-and-range binder cannot express. Paths are relative to the Gateway section
/// (<c>DataSources.erp.Provider</c>, <c>Auth.Jwt.Mappings[0].Claim</c>); the loader prints them as <c>Gateway:DataSources:erp:Provider</c>.
/// Warnings are reported but do not stop startup.
/// </summary>
public sealed class GatewayConfigValidator(IReadOnlyList<string>? sourcesWithoutClassifier = null) : IConfigValidator<GatewayOptions>
{
    /// <summary>Provider names the server registers (a test compares this with the registered <c>IDbProvider</c>s).</summary>
    public static readonly IReadOnlyList<string> KnownProviders = ["odbc", "sqlserver", "duckdb", "postgres", "flightsql"];

    /// <summary>Providers that can connect with the caller's own credentials (DuckDB has no user authentication).</summary>
    public static readonly IReadOnlyList<string> PassThroughProviders = ["odbc", "sqlserver", "postgres"];

    private static readonly string[] MetadataModes = ["auto", "odbc", "informationschema"];

    public void Validate(GatewayOptions o, IConfigValidationContext cx)
    {
        ValidateListenAndTls(o, cx);
        ValidateManagement(o, cx);
        ValidateModules(o, cx);
        ValidateIdentity(o, cx);
        ValidateApps(o, cx);
        ValidateStore(o, cx);
        ValidateViews(o, cx);
        ValidateMcp(o, cx);
        ValidateRest(o, cx);
        ValidatePorts(o, cx);
        ValidateCompression(o.Compression, "Compression", cx);
        ValidateDataSources(o, cx);
        ValidateJwt(o, cx);
        ValidateWindowsSignIn(o, cx);
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
        if (o.SchemaVersion < GatewayConfig.LatestSchemaVersion && sourcesWithoutClassifier is { Count: > 0 })
            cx.Warning("SchemaVersion", $"Schema version {o.SchemaVersion}: the statement `Classifier` of {string.Join(", ", sourcesWithoutClassifier)} defaults to warn. " +
                $"Schema version {GatewayConfig.LatestSchemaVersion} defaults it to strict.",
                $"Set `Classifier` on each data source, then set `SchemaVersion` to {GatewayConfig.LatestSchemaVersion}.");
    }

    private static void ValidateState(GatewayOptions o, IConfigValidationContext cx)
    {
        try
        {
            var settings = o.State.ToSettings();
            _ = settings.EngineId;
            if (!settings.IsSqlite && string.IsNullOrWhiteSpace(settings.ConnectionString))
                cx.Error("State.Connection", $"The state engine is `{o.State.Engine}`, which needs a connection string.", "Give the connection string of a database for the gateway's state, with the password as a secret reference, or use `sqlite`.");
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

    private static void ValidateSemantic(GatewayOptions o, IConfigValidationContext cx)
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

    private static void ValidateManagement(GatewayOptions o, IConfigValidationContext cx)
    {
        if (!o.Management.Enabled) return;
        ParseListen(cx, "Management.Listen", o.Management.Listen);
        if (o.Tls.Mode == TlsMode.None)
            cx.Error("Management.Enabled", "The management API is https only, and `Tls.Mode` is None.", "Choose another TLS mode (SelfSigned, File or Store), or set `management.enabled: false`.");
    }

    private static void ValidateModules(GatewayOptions o, IConfigValidationContext cx)
    {
        foreach (var name in o.Modules.Where(m => !GatewayModules.Known.Contains(m, StringComparer.OrdinalIgnoreCase)))
            cx.Error("Modules", $"`{name}` is not a module of this server.", $"Use {string.Join(", ", GatewayModules.Known)}.");
        if (o.Modules.Count != o.Modules.Distinct(StringComparer.OrdinalIgnoreCase).Count())
            cx.Error("Modules", "A module is listed twice.", "List each module once.");
        if (!GatewayModules.Known.Any(m => GatewayModules.Has(o, m)))
            cx.Error("Modules", "No module is enabled, so the server would serve nothing.", $"List at least one of {string.Join(", ", GatewayModules.Known)}.");
    }

    private static void ValidateStore(GatewayOptions o, IConfigValidationContext cx)
    {
        if (!GatewayModules.Has(o, GatewayModules.Store)) return;
        ParseListen(cx, "Store.Listen", o.Store.Listen);
    }

    private static void ValidateMcp(GatewayOptions o, IConfigValidationContext cx)
    {
        if (!GatewayModules.Has(o, GatewayModules.Mcp)) return;
        ParseListen(cx, "Mcp.Listen", o.Mcp.Listen);
    }

    /// <summary>
    /// Reads the addresses of a <c>listen</c> setting: each is a Kestrel-style URL (<c>https://0.0.0.0:7820</c>) with the scheme <paramref name="scheme"/>. A bad one is an error on the setting, naming it.
    /// </summary>
    internal static List<ListenUrl> ParseListen(IConfigValidationContext cx, string key, IReadOnlyList<string> values, string scheme = "https")
    {
        var urls = new List<ListenUrl>();
        for (var i = 0; i < values.Count; i++)
        {
            if (!ListenUrl.TryParse(values[i], out var url, out var problem))
                cx.Error($"{key}[{i}]", problem, $"Write the address as {scheme}://0.0.0.0:7820, {scheme}://127.0.0.1:9443 or a list of them.");
            else if (url!.Scheme != scheme)
                cx.Error($"{key}[{i}]", $"`{values[i]}` is {url.Scheme}; this listener is {scheme}" + (scheme == "https" ? ": it uses the Gateway's certificate." : " because `tls.mode` is None."),
                    $"Write it as {scheme}://{(url.Host.Contains(':') ? $"[{url.Host}]" : url.Host)}:{url.Port}.");
            else urls.Add(url);
        }
        return urls;
    }

    /// <summary>
    /// No two listeners share a port: Flight's, the management API's, the module listeners that have addresses of their own, and the public listener. The error is on the setting of the later of the two
    /// in that order, naming the earlier. Port 0 is the system's pick and never clashes.
    /// </summary>
    private static void ValidatePorts(GatewayOptions o, IConfigValidationContext cx)
    {
        var listeners = new List<(string Key, string Subject, string Possessive, int Port)>();
        void Add(string key, string subject, string possessive, IReadOnlyList<string> values)
        {
            foreach (var value in values)
                if (ListenUrl.TryParse(value, out var url, out _)) listeners.Add((key, subject, possessive, url!.Port));
        }
        if (GatewayModules.Has(o, GatewayModules.FlightSql)) Add("Listen", "The Flight listener", "the Flight listener's", o.Listen);
        if (o.Management.Enabled) Add("Management.Listen", "The management API", "the management API's", o.Management.Listen);
        void Own(string module, IReadOnlyList<string> values, string key, string subject, string possessive)
        {
            if (GatewayModules.Has(o, module)) Add(key, subject, possessive, values);
        }
        Own(GatewayModules.Identity, o.Identity.Listen, "Identity.Listen", "The identity home", "the identity home's");
        Own(GatewayModules.Apps, o.Apps.Listen, "Apps.Listen", "The apps host", "the apps host's");
        Own(GatewayModules.Store, o.Store.Listen, "Store.Listen", "The store module", "the store module's");
        Own(GatewayModules.Views, o.Views.Listen, "Views.Listen", "The views module", "the views module's");
        Own(GatewayModules.Mcp, o.Mcp.Listen, "Mcp.Listen", "The mcp module", "the mcp module's");
        Own(GatewayModules.Feeds, o.Rest.Listen, "Rest.Listen", "The feeds module", "the feeds module's");
        Add("Public.Listen", "The public listener", "the public listener's", o.Public.Listen);
        ParseListen(cx, "Public.Listen", o.Public.Listen);
        var reported = new HashSet<string>();
        for (var i = 0; i < listeners.Count; i++)
            for (var j = 0; j < i; j++)
                if (listeners[i].Port == listeners[j].Port && listeners[i].Port != 0)
                {
                    if (reported.Add($"{listeners[i].Key}:{listeners[i].Port}"))
                        cx.Error(listeners[i].Key, $"{listeners[i].Subject} has its own port; it is {listeners[j].Possessive} here.", $"Choose another port than {listeners[i].Port}.");
                    break;
                }
    }

    private static void ValidateRest(GatewayOptions o, IConfigValidationContext cx)
    {
        if (!GatewayModules.Has(o, GatewayModules.Feeds)) return;
        ParseListen(cx, "Rest.Listen", o.Rest.Listen);
        if (o.Rest.DefaultLimit > o.Rest.MaxRows)
            cx.Error("Rest.DefaultLimit", $"`DefaultLimit` is {o.Rest.DefaultLimit}, more than `MaxRows` ({o.Rest.MaxRows}).", "Set a default at or below the most a call may ask for.");
    }

    private static void ValidateViews(GatewayOptions o, IConfigValidationContext cx)
    {
        if (!GatewayModules.Has(o, GatewayModules.Views)) return;
        ParseListen(cx, "Views.Listen", o.Views.Listen);
        var export = o.Views.Export;
        if (!string.IsNullOrEmpty(export.Mode) && export.Mode is not ("pinned" or "system"))
            cx.Error("Views.Export.Mode", $"`Mode` is `{export.Mode}`.", "Use `pinned` (the Chromium chromium.pin names) or `system` (an installed Chrome or Edge at `Path`); leave it out to keep export off.");
        if (export.Mode == "system" && string.IsNullOrWhiteSpace(export.Path))
            cx.Error("Views.Export.Path", "Mode `system` needs `Path`, the installed Chrome or Edge.", "Set `Path`, or use mode `pinned`.");
    }

    private static void ValidateApps(GatewayOptions o, IConfigValidationContext cx)
    {
        for (var i = 0; i < o.Apps.AllowedPackages.Count; i++)
            if (!System.Text.RegularExpressions.Regex.IsMatch(o.Apps.AllowedPackages[i] ?? "", @"^(@[a-z0-9][a-z0-9._-]*/)?[a-z0-9][a-z0-9._-]*$"))
                cx.Error($"Apps.AllowedPackages[{i}]", $"`{o.Apps.AllowedPackages[i]}` is not a package name.", "Use an npm package name such as `left-pad` or `@scope/name`, lower case, with no version and no path.");
        if (o.Apps.AllowedPackages.Count > 0 && string.IsNullOrWhiteSpace(o.Apps.PackageStore))
            cx.Warning("Apps.AllowedPackages", "`allowedPackages` lists packages and no `packageStore` says where they are vendored, so no build can use them.", "Set `Apps.PackageStore` to the folder `Shared/web/app-store/build.mjs` made.");
        if (!GatewayModules.Has(o, GatewayModules.Apps)) return;
        ParseListen(cx, "Apps.Listen", o.Apps.Listen);
        if (string.IsNullOrWhiteSpace(o.Apps.Home.ClientId))
            cx.Error("Apps.Home.ClientId", "The apps host signs people in through an identity home and needs the id of the client registered there for it.", "Register one (`identity client-add` with the redirect URI <public url>/apps/-/callback) and set `Home.ClientId`.");
        if (string.IsNullOrWhiteSpace(o.Apps.Home.Authority))
        {
            if (!GatewayModules.Has(o, GatewayModules.Identity))
                cx.Error("Apps.Home.Authority", "The apps host has no identity home to sign people in through: this instance serves none.", "Set `Home.Authority` to the home's address, or list `identity` in `Modules`.");
        }
        else if (!Uri.TryCreate(o.Apps.Home.Authority, UriKind.Absolute, out var home) || home.Scheme != Uri.UriSchemeHttps)
            cx.Error("Apps.Home.Authority", $"`Authority` is `{o.Apps.Home.Authority}`, which is not an https address.", "Use the home's address, such as https://identity.example.com.");
        if (!string.IsNullOrWhiteSpace(o.Apps.PublicUrl)
            && (!Uri.TryCreate(o.Apps.PublicUrl, UriKind.Absolute, out var pub) || pub.Scheme != Uri.UriSchemeHttps || pub.AbsolutePath != "/" || pub.Query.Length > 0 || pub.Fragment.Length > 0))
            cx.Error("Apps.PublicUrl", $"`PublicUrl` is `{o.Apps.PublicUrl}`, which is not an https address without a path.", "Use the address people reach the apps host at, such as https://apps.example.com.");
        if (o.Tls.Mode == TlsMode.None)
            cx.Error("Modules", "The apps host is https only (a sandboxed app and its sign-in need a secure origin), and `Tls.Mode` is None.", "Use SelfSigned, File or Store, or leave `apps` out of `Modules`.");
    }

    private static void ValidateIdentity(GatewayOptions o, IConfigValidationContext cx)
    {
        if (!GatewayModules.Has(o, GatewayModules.Identity)) return;
        ParseListen(cx, "Identity.Listen", o.Identity.Listen);
        if (o.Tls.Mode == TlsMode.None)
            cx.Error("Identity.Enabled", "The identity home is https only (a passkey needs a secure origin), and `Tls.Mode` is None.", "Use SelfSigned, File or Store, or leave the identity home off.");
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
                cx.Error("Identity.Entra.ClientSecret", "Entra sign-in needs the client secret of the app registered in the tenant for the home.", "Set `ClientSecret` to a secret reference, such as secret:dbdatakit:gateway:identity:entra.");
        }
        else if (!string.IsNullOrWhiteSpace(entra.ClientId) || !string.IsNullOrWhiteSpace(entra.ClientSecret))
            cx.Error("Identity.Entra.Authority", "Entra sign-in is set up without the tenant.", "Set `Authority` to the tenant's issuer, https://login.microsoftonline.com/<tenant id>/v2.0, or remove the `Entra` section.");
    }

    private static void ValidateListenAndTls(GatewayOptions o, IConfigValidationContext cx)
    {
        // Flight SQL is https with a certificate and plain http only when TLS is off
        var flight = ParseListen(cx, "Listen", o.Listen, o.Tls.Mode == TlsMode.None ? "http" : "https");
        var address = flight.Count > 0 ? flight[0].Address : null;

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
                if (flight.Any(u => !IPAddress.IsLoopback(u.Address)))
                    cx.Warning("Tls.Mode", "TLS is off and the gateway listens on a non-loopback address: credentials and data travel in clear text.", "Use SelfSigned, File or Store outside local testing.");
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

    private static void ValidateDataSources(GatewayOptions o, IConfigValidationContext cx)
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
                    cx.Error($"{at}.Bitness", "Bitness x86 needs the 32-bit worker (DbDataGateway.Worker), which exists only on Windows.", "Run this server on Windows, or use Bitness x64 with a 64-bit driver.");
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
    private static void ValidateAccess(GatewayOptions o, string name, DataSourceOptions source, string at, IConfigValidationContext cx)
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
                "List the home groups allowed to use it under `grants.groups` (connection/2; a published definition carries them) or, in a file made by hand, `extensions.gateway.groups`, or open it to everyone with `everyone: true` in the same place.");
    }

    private static void ValidateWindowsSignIn(GatewayOptions o, IConfigValidationContext cx)
    {
        if (o.Auth.Windows && !OperatingSystem.IsWindows())
            cx.Error("Auth.Windows", "Windows password sign-in (a UPN checked by the operating system) runs only on Windows.", "Run this server on Windows, or set `Auth.Windows` to false (Entra, JWT and Kerberos through `Identity.Windows` work elsewhere).");
    }

    private static void ValidateJwt(GatewayOptions o, IConfigValidationContext cx)
    {
        var jwt = o.Auth.Jwt;
        if (!jwt.Enabled) return;
        if (string.IsNullOrWhiteSpace(jwt.Audience))
            cx.Error("Auth.Jwt.Audience", "`Audience` is required when JWT authentication is enabled (Authority or SigningKey is set); every token would be refused.", "Set the expected audience, for example api://dbgateway.");
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
    private static void CheckReferences(GatewayOptions o, List<string> granted, string? defaultSource, string at, IConfigValidationContext cx)
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

    private static string? DidYouMean(GatewayOptions o, string name)
    {
        var near = DbDataKit.Config.Yaml.DidYouMean.Closest(name, o.DataSources.Keys);
        return near is null ? (o.DataSources.Count == 0 ? "No data sources are defined." : $"Defined: {string.Join(", ", o.DataSources.Keys)}.") : $"Did you mean `{near}`?";
    }
}
