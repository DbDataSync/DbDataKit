using DbDataKit.Config;
using DbDataKit.Config.Yaml;
using DbDataKit.Secrets;

namespace DbDataKit.Connections;

public sealed class ConnectionLoadResult(ConnectionDefinition? definition, IReadOnlyList<ConfigDiagnostic> diagnostics)
{
    public ConnectionDefinition? Definition { get; } = definition;
    public IReadOnlyList<ConfigDiagnostic> Diagnostics { get; } = diagnostics;
    public bool Ok => Definition is not null && !Diagnostics.HasErrors();

    /// <summary>The definition, or an exception listing every error.</summary>
    public ConnectionDefinition Require() =>
        Ok ? Definition! : throw new FormatException(string.Join(Environment.NewLine, Diagnostics.Where(d => d.Severity == ConfigSeverity.Error)));
}

/// <summary>
/// Strict loader for <c>schema: connection/1</c> and <c>connection/2</c> (the second adds the sections <c>cache</c> and <c>checks</c>, plan 24a UC03): unknown keys are errors (with a suggestion), then the rules a binder cannot express are
/// checked: address XOR connection string, no write login on a read-only connection, no plaintext credentials, no credential
/// properties, valid secret references, login completeness. TLS opt-outs are reported as warnings.
/// </summary>
public static class ConnectionDefinitionLoader
{
    public const string BadSchema = "DBD-CONN-001";
    public const string BadAddress = "DBD-CONN-002";
    public const string WriteLoginOnReadonly = "DBD-CONN-003";
    public const string PlaintextCredential = "DBD-CONN-004";
    public const string DeniedProperty = "DBD-CONN-005";
    public const string BadSecretRef = "DBD-CONN-006";
    public const string BadLogin = "DBD-CONN-007";
    public const string TlsOptOut = "DBD-CONN-008";
    public const string SectionNotInThisFormat = "DBD-CONN-009";
    public const string CannotBeCached = "DBD-CONN-010";
    public const string BadCache = "DBD-CONN-011";
    public const string BadGrants = "DBD-CONN-012";

    /// <summary>JSON Schema for editor aids, generated from the type.</summary>
    public static string JsonSchema() => JsonSchemaGenerator.ToJson(typeof(ConnectionDefinition), "https://dbdata.dev/schema/connection/1.json");

    public static ConnectionLoadResult LoadFile(string path) => Load(File.ReadAllText(path), path);

    /// <param name="addressRequired">False for a product whose connection files carry no address because the login comes from its environment (DbDataBuild: plan 13, E03); every other rule still applies.</param>
    public static ConnectionLoadResult Load(string yaml, string file = "connection.yml", bool addressRequired = true)
    {
        var diags = new List<ConfigDiagnostic>();
        var root = StrictYamlReader.Read(yaml, file, diags);
        if (diags.HasErrors() || root is null) return new(null, diags);
        var declared = 1;

        // the schema key is checked first so a wrong kind is one clear error, not a cascade of unknown keys
        if (root is YamlMapping map)
        {
            var schema = map.Get("schema");
            if (schema is not YamlScalar { Value: var schemaText } || FormatOf(schemaText) is not { } format)
            {
                var at = schema ?? map;
                diags.Add(Diag(BadSchema, file, at, schema is null ? "`schema` is missing." : $"`schema` is `{(schema as YamlScalar)?.Value}`.", $"Use `schema: {ConnectionDefinition.SchemaId}`."));
                return new(null, diags);
            }
            if (format > ConnectionDefinition.CurrentVersion)
            {
                diags.Add(DefaultDiagnosticCatalog.Instance.Create(SharedCodes.SchemaVersionNewer, file, schema.Line, schema.Column,
                    $"`schema` is `{schemaText}`, but this tool understands `connection` files up to version {ConnectionDefinition.CurrentVersion}.", "Upgrade the tool; this file was written by a newer version."));
                return new(null, diags);
            }
            declared = format;
        }

        var bound = new ConfigBinder().Bind<ConnectionDefinition>(root, file);
        diags.AddRange(bound.Diagnostics);
        if (bound.Value is null || diags.HasErrors()) return new(null, diags);
        bound.Value.SchemaVersion = declared;
        Validate(bound.Value, file, bound.Sources, diags, addressRequired);
        // format 2's checks are check/1 entries (23a FQ01); format 1 has none, and a file that has them was refused above
        if (declared >= 2 && root is YamlMapping { } whole && whole.Get("checks") is { } checksNode)
            bound.Value.CheckDefinitions = DbDataKit.Quality.CheckSection.Read(checksNode, file, diags);
        return new(bound.Value, diags);
    }

    /// <summary>The number of a <c>connection/&lt;n&gt;</c> id; null for anything else (another kind, no number, zero).</summary>
    public static int? FormatOf(string? schema) =>
        schema is not null && schema.StartsWith("connection/", StringComparison.Ordinal) && int.TryParse(schema["connection/".Length..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n >= 1 ? n : null;

    /// <summary>The format a file's text says it is in (its <c>schema:</c> line), 1 when it does not say; used to record the format of a published data source (24a UC01).</summary>
    public static int FormatOfText(string text) =>
        System.Text.RegularExpressions.Regex.Match(text, "^schema:\\s*[\"']?(connection/[0-9]+)", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.CultureInvariant) is { Success: true } m
            ? FormatOf(m.Groups[1].Value) ?? 1 : 1;

    /// <summary>
    /// <paramref name="text"/> with its <c>schema:</c> line set to <see cref="ConnectionDefinition.CurrentSchemaId"/>, the rest as written: what a save or a publish of a connection file that loads writes (plan 33 6d, rules 3 and 5),
    /// so a file read as <c>connection/1</c> never leaves as 1. A text with no such line is unchanged.
    /// </summary>
    public static string WithCurrentVersion(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, "^schema:[ \\t]*[\"']?connection/[0-9]+[\"']?", "schema: " + ConnectionDefinition.CurrentSchemaId,
            System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>The semantic rules on their own, for definitions built in code.</summary>
    public static IReadOnlyList<ConfigDiagnostic> Validate(ConnectionDefinition d)
    {
        var diags = new List<ConfigDiagnostic>();
        Validate(d, "connection", new Dictionary<string, SourceInfo>(), diags);
        return diags;
    }

    private static void Validate(ConnectionDefinition d, string file, IReadOnlyDictionary<string, SourceInfo> src, List<ConfigDiagnostic> diags, bool addressRequired = true)
    {
        void Err(string code, string path, string message, string? fix = null) => diags.Add(Diag(code, file, src, path, message, fix, ConfigSeverity.Error));
        void Warn(string code, string path, string message, string? fix = null) => diags.Add(Diag(code, file, src, path, message, fix, ConfigSeverity.Warning));

        var logins = new (string Path, LoginSpec? Spec)[] { ("auth", d.Auth), ("read_login", d.ReadLogin), ("write_login", d.WriteLogin) };

        int forms = (d.Address is not null ? 1 : 0) + (d.ConnectionString is not null ? 1 : 0) + (d.ConnectionStringSecret is not null ? 1 : 0);
        bool loginHoldsString = logins.Any(l => l.Spec?.ConnectionStringSecret is not null);
        if (forms > 1)
            Err(BadAddress, d.Address is not null ? "address" : "connection_string", "Give either `address` or a connection string, not both.", "Remove one of `address`, `connection_string`, `connection_string_secret`.");
        else if (forms == 0 && !loginHoldsString && addressRequired)
            Err(BadAddress, "", "The connection has no address.", "Add `address:` (host, port, database), `connection_string:` or `connection_string_secret:`.");
        if (d.Address is { Host: null, Database: null })
            Err(BadAddress, "address", "`address` needs at least a `host` or a `database`.");

        if (d.Access == AccessMode.Readonly && d.WriteLogin is not null)
            Err(WriteLoginOnReadonly, "write_login", "A read-only connection cannot have a write login.", "Remove `write_login`, or set `access: readwrite`.");

        if (d.ConnectionString is not null)
            foreach (var f in CredentialKeyDetector.FindInConnectionString(d.ConnectionString))
                Err(PlaintextCredential, "connection_string", $"The connection string contains a plaintext credential (`{f.Key}`).", "Move it to a secret and use `auth.password_secret` or `connection_string_secret`.");

        foreach (var (key, value) in d.Properties)
        {
            try { CredentialPolicy.ValidateProperties([new KeyValuePair<string, string>(key, value)]); }
            catch (ConnectionPolicyException e) { Err(DeniedProperty, "properties." + key, e.Message, "Use `address`, `auth`, `tls` or `timeouts` for this."); }
        }

        if (d.ConnectionStringSecret is not null) CheckRef(d.ConnectionStringSecret, "connection_string_secret");
        foreach (var (path, spec) in logins)
        {
            if (spec is null) continue;
            if (spec.PasswordSecret is not null) CheckRef(spec.PasswordSecret, path + ".password_secret");
            if (spec.ConnectionStringSecret is not null) CheckRef(spec.ConnectionStringSecret, path + ".connection_string_secret");
            if (spec.TokenSecret is not null) CheckRef(spec.TokenSecret, path + ".token_secret");
            CheckLogin(path, spec);
        }

        // the sections of format 2 (24a UC03): refused in a format-1 file, checked in a format-2 one
        if (d.SchemaVersion < 2)
        {
            if (d.Cache is not null) Err(SectionNotInThisFormat, "cache", "`cache` is a section of format 2 (connection/2), and this file says `schema: connection/1`.", "Set `schema: connection/2`. A program that reads only connection/1 then refuses the file by its version, which keeps it from serving the data source without the section.");
            if (d.Checks is not null) Err(SectionNotInThisFormat, "checks", "`checks` is a section of format 2 (connection/2), and this file says `schema: connection/1`.", "Set `schema: connection/2`.");
            if (d.Grants is not null) Err(SectionNotInThisFormat, "grants", "`grants` is a section of format 2 (connection/2), and this file says `schema: connection/1`.", "Set `schema: connection/2`. A program that reads only connection/1 then refuses the file by its version instead of serving the data source without the grants the definition names.");
        }
        else if (d.Cache is { } cache)
        {
            if (!CacheSection.TryParseTtl(cache.Ttl, out _, out var ttlProblem)) Err(BadCache, "cache.ttl", ttlProblem!.Replace("`ttl`", "`cache.ttl`", StringComparison.Ordinal), "Write a whole number and `s`, `m` or `h`, at most `24h`.");
            if (!CacheSection.TryParseScope(cache.Scope, out _)) Err(BadCache, "cache.scope", $"`cache.scope` is `{cache.Scope}`; it is one of {string.Join(", ", CacheSection.ScopeNames.Select(n => $"`{n}`"))}.");
            // a data source with no identity of its own (no server, no database, no connection string to read them from) has no lineage identity, which the cache's expiry on a data change is keyed by
            if (cache.Enabled && d.Address is not { Host.Length: > 0 } and not { Database.Length: > 0 } && d.ConnectionString is not { Length: > 0 })
                Err(CannotBeCached, "cache.enabled", "The cache cannot be turned on for this data source: it names no server and no database (`address`, or a `connection_string`), so it has no lineage identity to expire entries by when its data changes.",
                    "Give it an `address` (host or database) or a `connection_string`, or leave the cache off.");
        }

        if (d.SchemaVersion >= 2 && d.Grants is { } grants) CheckGrants(grants);

        if (!d.Tls.Verify) Warn(TlsOptOut, "tls.verify", "Certificate verification is off for this connection.");
        if (d.Tls.TrustServerCertificate) Warn(TlsOptOut, "tls.trust_server_certificate", "The server certificate is trusted without verification.");
        if (!d.Tls.Encrypt) Warn(TlsOptOut, "tls.encrypt", "Encryption is off for this connection.");

        void CheckGrants(GrantsSpec grants)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < grants.Groups.Count; i++)
            {
                var group = grants.Groups[i];
                if (string.IsNullOrWhiteSpace(group) || group != group.Trim() || group.Any(char.IsControl))
                    Err(BadGrants, $"grants.groups[{i}]", "A group name in `grants.groups` is empty, has leading or trailing spaces, or has control characters.", "Name a group of the identity home exactly as the home shows it.");
                else if (!seen.Add(group))
                    Err(BadGrants, $"grants.groups[{i}]", $"The group `{group}` is listed twice in `grants.groups` (names match ignoring case).", "Remove the repeated name.");
            }
            if (grants.Everyone && grants.Groups.Count > 0)
                Err(BadGrants, "grants.everyone", "`grants` opens the data source to everyone and also names groups; the groups would mean nothing.", "Remove `everyone`, or remove `groups`.");
        }

        void CheckRef(string text, string path)
        {
            if (!SecretRef.TryParse(text, out _, out string? error)) Err(BadSecretRef, path, $"`{path}` is not a secret reference: {error}", "Use secret:, env:, file:, keyring: or dpapi:.");
        }

        void CheckLogin(string path, LoginSpec spec)
        {
            switch (spec.Mode)
            {
                case AuthMode.Sql:
                    if (spec.ConnectionStringSecret is null && (string.IsNullOrWhiteSpace(spec.User) || spec.PasswordSecret is null))
                        Err(BadLogin, path, "A `sql` login needs `user` and `password_secret` (or `connection_string_secret`).");
                    break;
                case AuthMode.Token:
                    if (spec.TokenSecret is null) Err(BadLogin, path, "A `token` login needs `token_secret`.");
                    break;
                default:
                    if (spec.PasswordSecret is not null || spec.TokenSecret is not null)
                        Err(BadLogin, path, $"A `{spec.Mode.ToString().ToLowerInvariant()}` login does not take a password or token.");
                    break;
            }
        }
    }

    private static ConfigDiagnostic Diag(string code, string file, YamlNode at, string message, string? fix) =>
        new(code, ConfigSeverity.Error, file, at.Line, at.Column, message, fix);

    private static ConfigDiagnostic Diag(string code, string file, IReadOnlyDictionary<string, SourceInfo> src, string path, string message, string? fix, ConfigSeverity severity)
    {
        // a key that is not in the document (a default) falls back to its parent, then to the top
        var p = path;
        while (true)
        {
            if (src.TryGetValue(p + "#key", out var k)) return new(code, severity, file, k.Line, k.Column, message, fix) { KeyPath = path };
            if (src.TryGetValue(p, out var s)) return new(code, severity, file, s.Line, s.Column, message, fix) { KeyPath = path };
            int i = p.LastIndexOf('.');
            if (i < 0) break;
            p = p[..i];
        }
        return new(code, severity, file, 0, 0, message, fix) { KeyPath = path };
    }
}
