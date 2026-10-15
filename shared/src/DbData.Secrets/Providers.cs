using System.Text;

namespace DbData.Secrets;

/// <summary>Maps a <c>secret:</c> target to an environment variable name, or null when not applicable.</summary>
public delegate string? EnvMapping(string secretTarget);

/// <summary>
/// <c>env:NAME</c> always; <c>secret:&lt;name&gt;</c> via an optional mapping (default
/// <c>DBDATA_SECRET_&lt;NAME&gt;</c> with every non-alphanumeric character replaced by '_', upper-cased).
/// </summary>
public sealed class EnvSecretProvider(Func<string, string?>? env = null, string? secretPrefix = "DBDATA_SECRET_", EnvMapping? mapping = null)
    : ISecretProvider
{
    private readonly Func<string, string?> _env = env ?? Environment.GetEnvironmentVariable;
    private readonly EnvMapping? _mapping = mapping ?? (secretPrefix is null ? null : t => secretPrefix + EnvNames.Sanitize(t));

    public string Name => "env";

    public bool CanHandle(SecretRef r) => r.Kind == SecretRefKind.Env || (r.Kind == SecretRefKind.Secret && _mapping is not null);

    public ValueTask<SecretValue?> TryResolveAsync(SecretRef r, CancellationToken ct = default)
    {
        var variable = r.Kind == SecretRefKind.Env ? r.Target : _mapping?.Invoke(r.Target);
        var value = variable is null ? null : _env(variable);
        return ValueTask.FromResult(string.IsNullOrEmpty(value) ? null : SecretValue.From(value));
    }
}

internal static class EnvNames
{
    public static string Sanitize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text) sb.Append(char.IsAsciiLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_');
        return sb.ToString();
    }
}

/// <summary>
/// Compatibility with the existing product conventions, so no user breaks during migration:
/// <list type="bullet">
/// <item>Build: <c>dbdata:build:connection:&lt;conn&gt;:&lt;read|write&gt;</c> maps to <c>DBDATABUILD_&lt;CONN&gt;_&lt;READ|WRITE&gt;</c>
/// (whole connection string). No fallback from read to write.</item>
/// <item>Sync: any other ref maps to <c>DBDATASYNC_SECRET_&lt;REF&gt;</c>, REF being the ref upper-cased with
/// non-alphanumerics replaced by '_' (e.g. <c>dbdatasync:connection:erp</c> becomes <c>DBDATASYNC_SECRET_DBDATASYNC_CONNECTION_ERP</c>).
/// The exact Sync sanitisation lives in ClrKernel.Core.Secrets and must be confirmed in Phase 0; supply a custom rule if it differs.</item>
/// </list>
/// Only handles <c>secret:</c> references. Rules are tried in order; the first non-null variable name that is set wins.
/// </summary>
public sealed class LegacyEnvProvider : ISecretProvider
{
    private readonly Func<string, string?> _env;
    private readonly IReadOnlyList<EnvMapping> _rules;

    public LegacyEnvProvider(Func<string, string?>? env = null, IEnumerable<EnvMapping>? rules = null)
    {
        _env = env ?? Environment.GetEnvironmentVariable;
        _rules = (rules ?? DefaultRules()).ToArray();
    }

    public string Name => "legacy-env";

    /// <summary>Build rule with a configurable variable prefix (default <c>DBDATABUILD</c>).</summary>
    public static EnvMapping BuildRule(string prefix = "DBDATABUILD") => target =>
    {
        var p = target.Split(':');
        if (p.Length != 5 || !p[0].Equals("dbdata", StringComparison.OrdinalIgnoreCase)
            || !p[1].Equals("build", StringComparison.OrdinalIgnoreCase)
            || !p[2].Equals("connection", StringComparison.OrdinalIgnoreCase)) return null;
        var part = p[4].ToUpperInvariant();
        return part is "READ" or "WRITE" ? $"{prefix}_{EnvNames.Sanitize(p[3])}_{part}" : null;
    };

    /// <summary>Sync rule with a configurable prefix (default <c>DBDATASYNC_SECRET</c>).</summary>
    public static EnvMapping SyncRule(string prefix = "DBDATASYNC_SECRET") =>
        target => $"{prefix}_{EnvNames.Sanitize(target)}";

    public static IEnumerable<EnvMapping> DefaultRules() => [BuildRule(), SyncRule()];

    public bool CanHandle(SecretRef r) => r.Kind == SecretRefKind.Secret;

    public ValueTask<SecretValue?> TryResolveAsync(SecretRef r, CancellationToken ct = default)
    {
        foreach (var rule in _rules)
        {
            var variable = rule(r.Target);
            if (variable is null) continue;
            var value = _env(variable);
            if (!string.IsNullOrWhiteSpace(value)) return ValueTask.FromResult<SecretValue?>(SecretValue.From(value));
        }
        return ValueTask.FromResult<SecretValue?>(null);
    }
}

public sealed class FileSecretOptions
{
    /// <summary>Files larger than this are refused (not read). Default 64 KiB.</summary>
    public long MaxBytes { get; set; } = 64 * 1024;

    /// <summary>
    /// When set, <c>secret:&lt;name&gt;</c> resolves to a file in this directory (e.g. <c>$CREDENTIALS_DIRECTORY</c>
    /// for systemd, <c>/run/secrets</c> for Docker); the name's ':' characters become '_'.
    /// </summary>
    public string? SecretsDirectory { get; set; }
}

/// <summary><c>file:&lt;path&gt;</c>: the file's contents, trimmed. Missing file yields null; oversized file throws.</summary>
public sealed class FileSecretProvider(FileSecretOptions? options = null) : ISecretProvider
{
    private readonly FileSecretOptions _options = options ?? new();

    public string Name => "file";

    public bool CanHandle(SecretRef r) =>
        r.Kind == SecretRefKind.File || (r.Kind == SecretRefKind.Secret && _options.SecretsDirectory is not null);

    public async ValueTask<SecretValue?> TryResolveAsync(SecretRef r, CancellationToken ct = default)
    {
        string path;
        if (r.Kind == SecretRefKind.File) path = r.Target;
        else
        {
            var file = r.Target.Replace(':', '_');
            if (file.IndexOfAny(['/', '\\']) >= 0 || file.Contains("..", StringComparison.Ordinal)) return null;
            path = Path.Combine(_options.SecretsDirectory!, file);
        }

        var info = new FileInfo(path);
        if (!info.Exists) return null;
        if (info.Length > _options.MaxBytes)
            throw new InvalidDataException($"Secret file '{path}' is {info.Length} bytes; the limit is {_options.MaxBytes}.");
        var text = (await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)).Trim();
        return text.Length == 0 ? null : SecretValue.From(text);
    }
}
