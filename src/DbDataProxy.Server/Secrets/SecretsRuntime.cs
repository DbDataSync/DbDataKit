using System.Collections.Concurrent;
using System.Text;
using DbDataKit.Secrets;
using DbDataKit.Secrets.Windows;
using DbDataProxy.Server.Configuration;
using Microsoft.Extensions.Options;

namespace DbDataProxy.Server.Secrets;

/// <summary>A secret reference in configuration cannot be used (unresolvable, unreadable). Names keys and references, never values.</summary>
public class SecretConfigurationException(string configKey, string message) : InvalidOperationException(message)
{
    public string ConfigKey { get; } = configKey;
}

/// <summary>A literal secret was found in configuration while <c>Proxy:RequireReferencedSecrets</c> is on.</summary>
public sealed class PlainSecretNotAllowedException(string configKey, string message) : SecretConfigurationException(configKey, message);

/// <summary>
/// Resolves secret references in configuration values at the point where the options snapshot is consumed, keeps the
/// process-wide <see cref="Redactor"/> informed of every resolved value, and reports literal secrets by config key
/// (once per key, never with the value). Nothing resolved is cached here; callers keep a value no longer than the
/// options snapshot it came from.
/// </summary>
public sealed class SecretsRuntime
{
    /// <summary>Entropy for DPAPI blobs written by <c>DbDataProxy protect-secret</c> (machine scope).</summary>
    public static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("DbDataProxy");

    private readonly Redactor _redactor;
    private readonly IOptionsMonitor<ProxyOptions> _options;
    private readonly ILogger _log;
    private readonly ISecretResolver _resolver;
    private readonly ConcurrentDictionary<string, byte> _warned = new(StringComparer.Ordinal);

    public SecretsRuntime(Redactor redactor, IOptionsMonitor<ProxyOptions> options, ILoggerFactory loggerFactory)
    {
        _redactor = redactor;
        _options = options;
        _log = loggerFactory.CreateLogger("DbDataProxy.Secrets");
        _resolver = new SecretResolverChain(CreateProviders(options.CurrentValue),
            new DelegateSecretAudit(line => _log.LogDebug("{Audit}", line)), redactor);
    }

    private static List<ISecretProvider> CreateProviders(ProxyOptions options)
    {
        var providers = new List<ISecretProvider> { new EnvSecretProvider() };

        // systemd LoadCredential= and container secret mounts: secret:<name> resolves to a file in that directory.
        var credentials = Environment.GetEnvironmentVariable("CREDENTIALS_DIRECTORY");
        providers.Add(new FileSecretProvider(new FileSecretOptions { SecretsDirectory = string.IsNullOrWhiteSpace(credentials) ? null : credentials }));

        if (OperatingSystem.IsWindows())
        {
            string directory = Path.Combine(Hosting.ProxyHost.ResolveDataDirectory(options), "secrets");
            providers.Add(new DpapiSecretStore(directory, DpapiEntropy));
        }
        return providers;
    }

    private bool Require => _options.CurrentValue.RequireReferencedSecrets;

    /// <summary>
    /// Resolves a single-value secret (certificate password, signing key). A literal is returned as is after a one-time
    /// warning naming <paramref name="configKey"/>; with <c>RequireReferencedSecrets</c> it is refused instead.
    /// </summary>
    public string? ResolveSecret(string configKey, string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return configured;
        if (SecretRef.TryParse(configured, out var reference)) return ResolveReference(configKey, reference);
        NotePlain(configKey, null);
        return configured;
    }

    /// <summary>
    /// Resolves references in a connection string: the whole string (<c>secret:...</c>) or the value of a credential key
    /// (<c>Password=env:DB_PASSWORD</c>). Literal credentials are reported through <see cref="NotePlain"/>.
    /// </summary>
    public string ResolveConnectionString(string configKey, string connectionString, string provider)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return connectionString;

        if (!ConnectionStringReferences.IsWholeReference(connectionString, out _))
        {
            foreach (var finding in CredentialKeyDetector.FindInConnectionString(connectionString))
            {
                if (finding.Kind != CredentialFindingKind.PasswordAuthenticationMode) NotePlain(configKey, finding.Key);
            }
        }

        ConnectionStringReferences.QuoteValue quote = provider.Equals("odbc", StringComparison.OrdinalIgnoreCase)
            ? ConnectionStringReferences.QuoteBraces
            : ConnectionStringReferences.QuoteDouble;
        try
        {
            string resolved = ConnectionStringReferences.ResolveAsync(connectionString, _resolver, quote).AsTask().GetAwaiter().GetResult();
            // A whole-string secret is registered as one value; also register the passwords inside it, so a driver message
            // that quotes only the password is scrubbed too.
            if (!ReferenceEquals(resolved, connectionString))
                foreach (var credential in ConnectionStringReferences.CredentialValues(resolved)) _redactor.Register(credential);
            return resolved;
        }
        catch (SecretNotFoundException ex)
        {
            throw Fail(Unresolvable(configKey, ex.Reference));
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            throw Fail(Unreadable(configKey, ex));
        }
    }

    private string ResolveReference(string configKey, SecretRef reference)
    {
        try
        {
            using var value = _resolver.ResolveAsync(reference).AsTask().GetAwaiter().GetResult();
            return value.RevealString();
        }
        catch (SecretNotFoundException)
        {
            throw Fail(Unresolvable(configKey, reference.ToAuditString()));
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            throw Fail(Unreadable(configKey, ex));
        }
    }

    private static bool IsReadFailure(Exception ex) =>
        ex is IOException or InvalidDataException or FormatException or System.Security.Cryptography.CryptographicException or UnauthorizedAccessException;

    private SecretConfigurationException Fail(SecretConfigurationException ex)
    {
        _log.LogError("{Message}", ex.Message);
        return ex;
    }

    private static SecretConfigurationException Unresolvable(string configKey, string reference) =>
        new(configKey, $"The secret reference {reference} in {configKey} could not be resolved.");

    private static SecretConfigurationException Unreadable(string configKey, Exception ex) =>
        new(configKey, $"A secret reference in {configKey} could not be read ({ex.GetType().Name}).");

    /// <summary>One-time warning (or, with RequireReferencedSecrets, an exception) for a literal secret at <paramref name="configKey"/>.</summary>
    private void NotePlain(string configKey, string? detail)
    {
        string where = detail is null ? configKey : $"{configKey} ({detail})";
        if (Require)
            throw Fail(new PlainSecretNotAllowedException(configKey,
                $"{where} holds a literal secret and Proxy:RequireReferencedSecrets is true; use a secret reference (secret:, env:, file: or dpapi:)."));
        if (_warned.TryAdd(where, 0))
            _log.LogWarning("Config key {Key} holds a literal secret; replace it with a secret reference (secret:, env:, file: or dpapi:). Set Proxy:RequireReferencedSecrets=true to refuse literal secrets.", where);
    }

    /// <summary>
    /// Startup check: resolves every secret the options mention (which also registers the values for redaction), warns about
    /// literals and, when <c>RequireReferencedSecrets</c> is on, fails with every offending key listed.
    /// </summary>
    public void ValidateStartup(ProxyOptions options)
    {
        var plain = new List<string>();
        void Check(Action resolve)
        {
            try { resolve(); }
            catch (PlainSecretNotAllowedException ex) { plain.Add(ex.Message); }
            catch (SecretConfigurationException) { /* already logged; requests fail until the reference resolves */ }
        }

        foreach (var (name, source) in options.DataSources)
        {
            string key = $"Proxy:DataSources:{name}:ConnectionString";
            Check(() => ResolveConnectionString(key, source.ConnectionString, source.Provider));
        }
        Check(() => ResolveSecret("Proxy:Tls:CertificatePassword", options.Tls.CertificatePassword));
        Check(() => ResolveSecret("Proxy:Auth:Jwt:SigningKey", options.Auth.Jwt.SigningKey));

        if (plain.Count > 0)
            throw new PlainSecretNotAllowedException("Proxy", "Literal secrets are not allowed (Proxy:RequireReferencedSecrets is true): " + string.Join(" ", plain));
    }

    /// <summary>Scrubs registered secrets, credential fragments and, optionally, one caller-supplied password.</summary>
    public string Redact(string? text, string? extraSecret = null)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        text = _redactor.Redact(text);
        if (!string.IsNullOrEmpty(extraSecret))
        {
            // Caller passwords are not registered process-wide (unbounded input); they only scrub this one text.
            var local = new Redactor();
            local.Register(extraSecret);
            text = local.Redact(text);
        }
        return text;
    }
}
