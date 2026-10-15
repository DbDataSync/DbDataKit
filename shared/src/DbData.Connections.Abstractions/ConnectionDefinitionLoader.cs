using DbData.Config;
using DbData.Config.Yaml;
using DbData.Secrets;

namespace DbData.Connections;

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
/// Strict loader for <c>schema: connection/1</c>: unknown keys are errors (with a suggestion), then the rules a binder cannot express are
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

    /// <summary>JSON Schema for editor aids, generated from the type.</summary>
    public static string JsonSchema() => JsonSchemaGenerator.ToJson(typeof(ConnectionDefinition), "https://dbdata.dev/schema/connection/1.json");

    public static ConnectionLoadResult LoadFile(string path) => Load(File.ReadAllText(path), path);

    public static ConnectionLoadResult Load(string yaml, string file = "connection.yml")
    {
        var diags = new List<ConfigDiagnostic>();
        var root = StrictYamlReader.Read(yaml, file, diags);
        if (diags.HasErrors() || root is null) return new(null, diags);

        // the schema key is checked first so a wrong kind is one clear error, not a cascade of unknown keys
        if (root is YamlMapping map)
        {
            var schema = map.Get("schema");
            if (schema is not YamlScalar { Value: ConnectionDefinition.SchemaId })
            {
                var at = schema ?? map;
                diags.Add(Diag(BadSchema, file, at, schema is null ? "`schema` is missing." : $"`schema` is `{(schema as YamlScalar)?.Value}`.", $"Use `schema: {ConnectionDefinition.SchemaId}`."));
                return new(null, diags);
            }
        }

        var bound = new ConfigBinder().Bind<ConnectionDefinition>(root, file);
        diags.AddRange(bound.Diagnostics);
        if (bound.Value is null || diags.HasErrors()) return new(null, diags);
        Validate(bound.Value, file, bound.Sources, diags);
        return new(bound.Value, diags);
    }

    /// <summary>The semantic rules on their own, for definitions built in code.</summary>
    public static IReadOnlyList<ConfigDiagnostic> Validate(ConnectionDefinition d)
    {
        var diags = new List<ConfigDiagnostic>();
        Validate(d, "connection", new Dictionary<string, SourceInfo>(), diags);
        return diags;
    }

    private static void Validate(ConnectionDefinition d, string file, IReadOnlyDictionary<string, SourceInfo> src, List<ConfigDiagnostic> diags)
    {
        void Err(string code, string path, string message, string? fix = null) => diags.Add(Diag(code, file, src, path, message, fix, ConfigSeverity.Error));
        void Warn(string code, string path, string message, string? fix = null) => diags.Add(Diag(code, file, src, path, message, fix, ConfigSeverity.Warning));

        var logins = new (string Path, LoginSpec? Spec)[] { ("auth", d.Auth), ("read_login", d.ReadLogin), ("write_login", d.WriteLogin) };

        int forms = (d.Address is not null ? 1 : 0) + (d.ConnectionString is not null ? 1 : 0) + (d.ConnectionStringSecret is not null ? 1 : 0);
        bool loginHoldsString = logins.Any(l => l.Spec?.ConnectionStringSecret is not null);
        if (forms > 1)
            Err(BadAddress, d.Address is not null ? "address" : "connection_string", "Give either `address` or a connection string, not both.", "Remove one of `address`, `connection_string`, `connection_string_secret`.");
        else if (forms == 0 && !loginHoldsString)
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

        if (!d.Tls.Verify) Warn(TlsOptOut, "tls.verify", "Certificate verification is off for this connection.");
        if (d.Tls.TrustServerCertificate) Warn(TlsOptOut, "tls.trust_server_certificate", "The server certificate is trusted without verification.");
        if (!d.Tls.Encrypt) Warn(TlsOptOut, "tls.encrypt", "Encryption is off for this connection.");

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
