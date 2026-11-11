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
    public const string BadKind = "DBD-CONN-013";
    public const string KindNotKept = "DBD-CONN-014";
    public const string BadOutside = "DBD-CONN-015";
    public const string AddressHidden = "DBD-CONN-016";
    public const string WebhookFormatMismatch = "DBD-CONN-017";

    /// <summary>JSON Schema for editor aids, generated from the type.</summary>
    public static string JsonSchema() => JsonSchemaGenerator.ToJson(typeof(ConnectionDefinition), "https://dbdata.dev/schema/connection/1.json");

    public static ConnectionLoadResult LoadFile(string path) => Load(File.ReadAllText(path), path);

    /// <summary>Loads a file of any kind, outside systems and model providers included (the console, the runner and what uses them); <see cref="LoadFile(string)"/> reads database connections only.</summary>
    public static ConnectionLoadResult LoadFileOfAnyKind(string path) => Load(File.ReadAllText(path), path, outsideKinds: true);

    /// <param name="addressRequired">False for a product whose connection files carry no address because the login comes from its environment (DbDataBuild: plan 13, E03); every other rule still applies.</param>
    /// <param name="outsideKinds">False (the default) for a program that keeps database connections only: a file of kind <c>http</c> or <c>model-provider</c> is refused naming the file. True for one that uses them (the console, the runner).</param>
    public static ConnectionLoadResult Load(string yaml, string file = "connection.yml", bool addressRequired = true, bool outsideKinds = false)
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
        Validate(bound.Value, file, bound.Sources, diags, addressRequired, outsideKinds);
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

    private static void Validate(ConnectionDefinition d, string file, IReadOnlyDictionary<string, SourceInfo> src, List<ConfigDiagnostic> diags, bool addressRequired = true, bool outsideKinds = true)
    {
        void Err(string code, string path, string message, string? fix = null) => diags.Add(Diag(code, file, src, path, message, fix, ConfigSeverity.Error));
        void Warn(string code, string path, string message, string? fix = null) => diags.Add(Diag(code, file, src, path, message, fix, ConfigSeverity.Warning));

        // the kind first (plan 27a IX01): a file of a kind this program does not know, or does not keep, is refused naming the file, before any rule of another kind misreads it
        if (d.Kind is not null && d.SchemaVersion < 2)
        {
            Err(SectionNotInThisFormat, "kind", "`kind` is a key of format 2 (connection/2), and this file says `schema: connection/1`.", "Set `schema: connection/2`. A program that reads only connection/1 then refuses the file by its version instead of misreading it as a database.");
            return;
        }
        if (!ConnectionKinds.IsKnown(d.KindName))
        {
            Err(BadKind, "kind", $"The connection file '{file}' is of kind `{d.Kind}`, which this program does not know.", $"The kinds are {string.Join(", ", ConnectionKinds.All.Select(k => $"`{k}`"))}; a database is the default.");
            return;
        }
        if (d.IsOutside)
        {
            if (!outsideKinds)
                Err(KindNotKept, "kind", $"The connection file '{file}' is of kind `{d.KindName}` (an outside system), and this program keeps database connections only.", "Keep it where the console or the runner keeps outside connections.");
            else
                ValidateOutside(d, file, src, diags);
            return;
        }
        if (d.Engine.Length == 0) Err(SharedCodes.MissingKey, "engine", "Required key `engine` is missing: a database connection says which engine it is.", "Add `engine: postgres` (or sqlserver, mysql, oracle, duckdb, odbc, flightsql).");
        foreach (var (key, present) in new[] { ("endpoint", d.Endpoint is not null), ("sign_in", d.SignIn is not null), ("test_path", d.TestPath is not null), ("model_provider", d.ModelProvider is not null),
            ("endpoint_secrets", d.EndpointSecrets.Count > 0), ("webhook", d.Webhook is not null), ("smtp", d.Smtp is not null) })
            if (present) Err(BadOutside, key, $"`{key}` belongs to an outside kind (`http`, `model-provider`, `smtp`), not to a database.", "Remove it, or say `kind: http` (and drop the database keys).");

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

    /// <summary>The rules of the kinds that are not a database (plan 27a IX01): an endpoint, secret references for what signs in, and for a model provider its section.</summary>
    private static void ValidateOutside(ConnectionDefinition d, string file, IReadOnlyDictionary<string, SourceInfo> src, List<ConfigDiagnostic> diags)
    {
        void Err(string path, string message, string? fix = null) => diags.Add(Diag(BadOutside, file, src, path, message, fix, ConfigSeverity.Error));
        void Warn(string path, string message, string? fix = null, string code = TlsOptOut) => diags.Add(Diag(code, file, src, path, message, fix, ConfigSeverity.Warning));
        void CheckRef(string? text, string path)
        {
            if (text is not null && !SecretRef.TryParse(text, out _, out var error))
                diags.Add(Diag(BadSecretRef, file, src, path, $"`{path}` is not a secret reference: {error}", "Use secret:, env:, file:, keyring: or dpapi: (a key is never written in the file).", ConfigSeverity.Error));
        }

        foreach (var (key, present) in new[]
        {
            ("engine", d.Engine.Length > 0), ("dialect", d.Dialect is not null), ("address", d.Address is not null), ("connection_string", d.ConnectionString is not null), ("connection_string_secret", d.ConnectionStringSecret is not null),
            ("auth", d.Auth is not null), ("read_login", d.ReadLogin is not null), ("write_login", d.WriteLogin is not null), ("cache", d.Cache is not null), ("checks", d.Checks is not null), ("grants", d.Grants is not null),
        })
            if (present) Err(key, $"`{key}` is a key of a database connection; a `{d.KindName}` connection has an `endpoint` and `sign_in`.", $"Remove `{key}`.");

        // an address is an http(s) one with no user name, password or query: a key is never part of it
        if (d.KindName == ConnectionKinds.Smtp)
        {
            foreach (var (key, present) in new[] { ("endpoint", d.Endpoint is not null), ("endpoint_secrets", d.EndpointSecrets.Count > 0), ("webhook", d.Webhook is not null), ("model_provider", d.ModelProvider is not null), ("test_path", d.TestPath is not null) })
                if (present) Err(key, $"`{key}` does not belong to a `smtp` connection: the mail server is `smtp.host`.", $"Remove `{key}`.");
            CheckSmtp();
        }
        else
        {
            if (d.Smtp is not null) Err("smtp", "`smtp` belongs to the kind `smtp`.", "Remove it, or say `kind: smtp`.");
            string? endpoint = d.Endpoint ?? (d.ModelProvider is { Provider: var p } ? ModelProviderSpec.DefaultEndpoint(p) : null);
            if (endpoint is null)
            {
                if (d.EndpointSecrets.Count > 0) Err("endpoint_secrets", "`endpoint_secrets` binds the placeholders of `endpoint`, and there is none.", "Write the address as a template: `endpoint: https://hooks.example.com/services/{token}`.");
                Err("endpoint", d.KindName == ConnectionKinds.Http ? "A `http` connection needs an `endpoint`." : "A model provider of this kind has no default address: write `endpoint`.", "Write the address, such as `endpoint: https://api.example.com/v1`; a secret part of it is a `{placeholder}` bound in `endpoint_secrets`.");
            }
            else if (EndpointTemplate.IsHidden(endpoint))
            {
                if (d.EndpointSecrets.Count > 0) Err("endpoint_secrets", "`endpoint` is wholly a secret reference, so there is no placeholder to bind.", "Remove `endpoint_secrets`, or write the visible address with `{placeholders}`.");
                Warn("endpoint", "The whole address is a secret (shown as \"address hidden\"): what the connection reaches cannot be read, and the private-address check cannot see it. Prefer the address with its secret part as a `{placeholder}` bound in `endpoint_secrets`.", null, AddressHidden);
            }
            else
            {
                foreach (var (path, message) in EndpointTemplate.Problems(endpoint)) Err(path, message, "A key, token or signature is never written in the address: bind it in `endpoint_secrets` (or `sign_in`).");
                if (d.Endpoint is not null)
                    foreach (var (path, message) in EndpointTemplate.BindingProblems(endpoint, d.EndpointSecrets)) Err(path, message);
            }
        }

        if (d.SignIn is { } s) CheckSignIn(s);
        if (d.TestPath is { } testPath && (testPath.Contains("://", StringComparison.Ordinal) || testPath.Contains('?', StringComparison.Ordinal) || testPath.Contains('#', StringComparison.Ordinal) || testPath.Contains("..", StringComparison.Ordinal) || testPath.Any(char.IsWhiteSpace)))
            Err("test_path", "`test_path` is an address beneath the endpoint, such as `/v1/ping`: no scheme, no query, no `..`, no spaces.", "A key is never part of an address: it is `sign_in.key_secret`.");
        if (d.KindName == ConnectionKinds.Http)
        {
            if (d.ModelProvider is not null) Err("model_provider", "`model_provider` belongs to the kind `model-provider`.", "Remove it.");
            CheckWebhook();
        }
        else if (d.KindName == ConnectionKinds.ModelProvider)
        {
            if (d.Webhook is not null) Err("webhook", "`webhook` belongs to the kind `http`.", "Remove it.");
            CheckModelProvider();
        }
        else if (d.Webhook is not null) Err("webhook", "`webhook` belongs to the kind `http`.", "Remove it.");
        if (!d.Tls.Verify) diags.Add(Diag(TlsOptOut, file, src, "tls.verify", "Certificate verification is off for this connection.", null, ConfigSeverity.Warning));
        if (d.Tls.TrustServerCertificate) diags.Add(Diag(TlsOptOut, file, src, "tls.trust_server_certificate", "The server certificate is trusted without verification.", null, ConfigSeverity.Warning));
        if (!d.Tls.Encrypt) diags.Add(Diag(TlsOptOut, file, src, "tls.encrypt", "Encryption is off for this connection.", null, ConfigSeverity.Warning));

        void CheckSignIn(SignInSpec s)
        {
            CheckRef(s.KeySecret, "sign_in.key_secret");
            CheckRef(s.PasswordSecret, "sign_in.password_secret");
            CheckRef(s.ClientSecretSecret, "sign_in.client_secret_secret");
            CheckRef(s.CertificateSecret, "sign_in.certificate_secret");
            CheckRef(s.CertificatePasswordSecret, "sign_in.certificate_password_secret");
            void Needs(string key, bool given) { if (!given) Err("sign_in." + key, $"`sign_in.method: {s.Method}` needs `{key}`."); }
            void NoOthers(params (string Key, bool Present)[] keys)
            {
                foreach (var (key, present) in keys) if (present) Err("sign_in." + key, $"`{key}` does not belong to `sign_in.method: {s.Method}`.", $"Remove `{key}`.");
            }
            switch (s.Method)
            {
                case "none":
                    NoOthers(("key_secret", s.KeySecret is not null), ("user", s.User is not null), ("password_secret", s.PasswordSecret is not null), ("token_url", s.TokenUrl is not null), ("client_id", s.ClientId is not null),
                        ("client_secret_secret", s.ClientSecretSecret is not null), ("certificate_file", s.CertificateFile is not null), ("certificate_secret", s.CertificateSecret is not null));
                    break;
                case "api_key":
                    Needs("key_secret", s.KeySecret is not null);
                    NoOthers(("user", s.User is not null), ("password_secret", s.PasswordSecret is not null), ("token_url", s.TokenUrl is not null), ("client_secret_secret", s.ClientSecretSecret is not null), ("certificate_file", s.CertificateFile is not null));
                    if (s.Name is { } n && (n.Length == 0 || n.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == ':'))) Err("sign_in.name", "`name` is a header or query parameter name: no spaces, no colon.");
                    if (s.In == "query" && s.Prefix is not null) Err("sign_in.prefix", "`prefix` is for a header; a query parameter holds the key as it is.");
                    break;
                case "basic":
                    Needs("user", !string.IsNullOrWhiteSpace(s.User));
                    Needs("password_secret", s.PasswordSecret is not null);
                    NoOthers(("key_secret", s.KeySecret is not null), ("token_url", s.TokenUrl is not null), ("client_secret_secret", s.ClientSecretSecret is not null), ("certificate_file", s.CertificateFile is not null));
                    break;
                case "oauth2_client_credentials":
                    Needs("token_url", s.TokenUrl is not null);
                    Needs("client_id", !string.IsNullOrWhiteSpace(s.ClientId));
                    Needs("client_secret_secret", s.ClientSecretSecret is not null);
                    if (s.TokenUrl is not null && !IsSafeAddress(s.TokenUrl)) Err("sign_in.token_url", "`token_url` is an http or https address with no user name, password or query in it.");
                    NoOthers(("key_secret", s.KeySecret is not null), ("password_secret", s.PasswordSecret is not null), ("certificate_file", s.CertificateFile is not null));
                    break;
                case "signed_webhook":
                    Needs("key_secret", s.KeySecret is not null);
                    NoOthers(("user", s.User is not null), ("password_secret", s.PasswordSecret is not null), ("token_url", s.TokenUrl is not null), ("client_secret_secret", s.ClientSecretSecret is not null), ("certificate_file", s.CertificateFile is not null), ("certificate_secret", s.CertificateSecret is not null));
                    if (s.Name is { } hn && (hn.Length == 0 || hn.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == ':'))) Err("sign_in.name", "`name` is a header name: no spaces, no colon.");
                    break;
                case "client_certificate":
                    if ((s.CertificateFile is null) == (s.CertificateSecret is null)) Err("sign_in", "`sign_in.method: client_certificate` needs one of `certificate_file` and `certificate_secret`.", "Give the PKCS#12 file, or a secret that holds it as Base64.");
                    NoOthers(("key_secret", s.KeySecret is not null), ("user", s.User is not null), ("password_secret", s.PasswordSecret is not null), ("token_url", s.TokenUrl is not null), ("client_secret_secret", s.ClientSecretSecret is not null));
                    break;
            }
        }

        void CheckWebhook()
        {
            if (d.Webhook is not { } w)
            {
                if (d.SignIn?.Method == "signed_webhook") Err("sign_in.method", "`signed_webhook` signs what a delivery channel posts: it needs a `webhook:` section (`format: signed`).", "Add `webhook: { format: signed }`.");
                return;
            }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < w.AllowPrivate.Count; i++)
            {
                var entry = w.AllowPrivate[i];
                if (string.IsNullOrWhiteSpace(entry) || entry != entry.Trim() || entry.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) Err($"webhook.allow_private[{i}]", "An entry of `allow_private` is a host name, an address or a range such as `10.1.0.0/16`, with no spaces.");
                else if (entry.Contains('/', StringComparison.Ordinal) && !CidrOk(entry)) Err($"webhook.allow_private[{i}]", $"`{entry}` is not a range such as `10.1.0.0/16`.");
                else if (!seen.Add(entry)) Err($"webhook.allow_private[{i}]", $"`{entry}` is listed twice.");
            }
            if (d.Endpoint is { } shown && !EndpointTemplate.IsHidden(shown) && Uri.TryCreate(System.Text.RegularExpressions.Regex.Replace(shown, @"\{[A-Za-z][A-Za-z0-9_]*\}", "x"), UriKind.Absolute, out var shownUri)
                && WebhookPatterns.ServiceOfHost(shownUri.Host) is { Format: { } expected } known && expected != w.Format)
                Warn("webhook.format", $"The address is {known.Service}'s, which takes `format: {expected}`, and this webhook says `format: {w.Format}`.", $"Set `format: {expected}`, or check the address.", WebhookFormatMismatch);
            var method = d.SignIn?.Method ?? "none";
            if (w.Format == "signed" && method != "signed_webhook") Err("sign_in.method", "A `signed` webhook is signed with the shared secret: `sign_in: { method: signed_webhook, key_secret: ... }`.", "Store the shared secret and write its reference.");
            if (w.Format is "slack" or "teams" && method != "none") Err("sign_in.method", $"A `{w.Format}` webhook takes no sign-in: the token is in its address (a placeholder of `endpoint`).", "Remove `sign_in`.");
        }

        void CheckSmtp()
        {
            if (d.Smtp is not { } m) { Err("smtp", "A `smtp` connection needs an `smtp` section: the host and the sender address.", "Write `smtp: { host: smtp.example.com, sender: noreply@example.com }`."); return; }
            if (string.IsNullOrWhiteSpace(m.Host) || m.Host.Any(c => char.IsWhiteSpace(c) || c is '/' or '@')) Err("smtp.host", "`smtp.host` is the mail server's name or address, nothing else.");
            if (!System.Net.Mail.MailAddress.TryCreate(m.Sender, out _)) Err("smtp.sender", "`smtp.sender` is an address: `ana@example.com` or `Name <ana@example.com>`.");
            if (m.Tls == "none" && !m.AllowPlainText && !IsLoopbackHost(m.Host))
                Err("smtp.tls", "`tls: none` sends mail, and a login, in clear text; it is allowed only to this machine (`localhost`, `127.0.0.1`, `::1`).", "Use `starttls` or `implicit`, or say `allow_plain_text: true` for a relay on a network you trust.");
            if (m.AllowPlainText && m.Tls != "none") Err("smtp.allow_plain_text", "`allow_plain_text` goes with `tls: none`.", "Remove it.");
            if (m.TrustedCertificate is { } cert && string.IsNullOrWhiteSpace(cert)) Err("smtp.trusted_certificate", "`smtp.trusted_certificate` is the path of a certificate file.");
            var method = d.SignIn?.Method ?? "none";
            if (method is not ("none" or "basic")) Err("sign_in.method", $"A mail server signs in with `basic` (user and `password_secret`) or `none`, not `{method}`.");
            if (m.Tls == "none" && method == "basic" && !IsLoopbackHost(m.Host)) Warn("smtp.tls", "A login is sent without TLS.");
        }

        void CheckModelProvider()
        {
            if (d.TestPath is not null) Err("test_path", "`test_path` belongs to the kind `http`; a model provider is tested per model.", "Remove it.");
            if (d.ModelProvider is not { } m) { Err("model_provider", "A `model-provider` connection needs a `model_provider` section: the provider, and the models it allows.", "Write `model_provider: { provider: anthropic, models: [ { id: ..., window: 200000 } ] }`."); return; }
            if (m.Provider == "openai-compatible" && m.Locality is null) Err("model_provider.locality", "`locality` is missing: say whether this server is `cloud` or `local`.", "The install policy treats a cloud provider differently (data rules, fallback).");
            if (m.Models.Count == 0) Err("model_provider.models", "`models` is a list of at least one model the provider allows.", "Write `models: [ { id: <the provider's model id>, window: 200000 } ]`.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < m.Models.Count; i++)
            {
                var id = m.Models[i].Id;
                if (id.Length > 120 || id.Any(char.IsWhiteSpace)) Err($"model_provider.models[{i}].id", "A model id is the provider's own name for it, with no spaces.");
                else if (!seen.Add(id)) Err($"model_provider.models[{i}].id", $"`{id}` is listed more than once.");
            }
            var auth = d.SignIn?.Method ?? "none";
            if (auth is not ("none" or "api_key")) Err("sign_in.method", $"A model provider signs in with `api_key` or `none`, not `{auth}`.");
            if (auth == "api_key" && m.AuthStyle == "none") Err("sign_in.method", "`auth_style: none` and an api key contradict each other.");
            if (auth == "none" && m.Provider is "anthropic" or "openai" or "foundry" && m.AuthStyle != "none")
                Err("sign_in", $"A {m.Provider} provider needs `sign_in: {{ method: api_key, key_secret: ... }}`: the reference of the key (`secret:<name>` or `env:<NAME>`).", "Store the key with the console's secrets and write its reference; a key is never typed into the file.");
        }
    }

    private static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) || (System.Net.IPAddress.TryParse(host.Trim('[', ']'), out var address) && System.Net.IPAddress.IsLoopback(address));

    private static bool CidrOk(string entry)
    {
        var slash = entry.IndexOf('/', StringComparison.Ordinal);
        return System.Net.IPAddress.TryParse(entry[..slash], out var network) && int.TryParse(entry[(slash + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var bits)
            && bits <= (network.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
    }

    /// <summary>An http or https address with no user name, password or query: where a key never belongs.</summary>
    public static bool IsSafeAddress(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query);

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
