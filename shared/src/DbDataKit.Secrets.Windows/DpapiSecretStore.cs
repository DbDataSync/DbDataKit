using DbDataKit.Security.Acl;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace DbDataKit.Secrets.Windows;

/// <summary>DPAPI helpers. Windows only.</summary>
[SupportedOSPlatform("windows")]
public static class DpapiProtector
{
    public static byte[] Protect(ReadOnlySpan<char> secret, byte[]? entropy, DataProtectionScope scope)
    {
        var plain = Encoding.UTF8.GetBytes(secret.ToArray());
        try { return ProtectedData.Protect(plain, entropy, scope); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public static SecretValue Unprotect(byte[] blob, byte[]? entropy, DataProtectionScope scope)
    {
        var plain = ProtectedData.Unprotect(blob, entropy, scope);
        try { return SecretValue.From(Encoding.UTF8.GetString(plain)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    /// <summary>Produces an inline <c>dpapi:&lt;base64&gt;</c> reference for config.</summary>
    public static string ToInlineRef(SecretValue value, byte[]? entropy, DataProtectionScope scope) =>
        "dpapi:" + Convert.ToBase64String(Protect(value.Reveal(), entropy, scope));
}

/// <summary>
/// Secrets stored one-file-per-name under a directory, each encrypted with DPAPI (per-product entropy),
/// plus resolution of inline <c>dpapi:&lt;base64&gt;</c> references. <see cref="DataProtectionScope.LocalMachine"/>
/// is the service-account choice: <b>any process on the machine that knows the entropy can decrypt</b>, so
/// the directory ACL is the real control. When <c>allowedIdentities</c> is supplied the directory and every
/// written file are restricted to them via <see cref="AclHelper"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore : ISecretStore, ISecretProvider
{
    private const string Extension = ".secret";
    private readonly string _directory;
    private readonly byte[]? _entropy;
    private readonly DataProtectionScope _scope;
    private readonly string[]? _identities;

    public DpapiSecretStore(string directory, byte[]? entropy, DataProtectionScope scope = DataProtectionScope.LocalMachine,
        IEnumerable<string>? allowedIdentities = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI is only available on Windows.");
        _directory = directory;
        _entropy = entropy;
        _scope = scope;
        _identities = allowedIdentities?.ToArray();
    }

    public string Name => "dpapi";

    public bool CanHandle(SecretRef r) =>
        r.Kind == SecretRefKind.Dpapi || (r.Kind == SecretRefKind.Secret && SecretName.TryParse(r.Target, out _));

    public ValueTask<SecretValue?> TryResolveAsync(SecretRef r, CancellationToken ct = default)
    {
        byte[] blob;
        if (r.Kind == SecretRefKind.Dpapi)
        {
            try { blob = Convert.FromBase64String(r.Target); }
            catch (FormatException) { throw new FormatException("dpapi reference is not valid base64."); }
        }
        else
        {
            var file = PathFor(SecretName.Parse(r.Target));
            if (!File.Exists(file)) return ValueTask.FromResult<SecretValue?>(null);
            blob = File.ReadAllBytes(file);
        }
        return ValueTask.FromResult<SecretValue?>(DpapiProtector.Unprotect(blob, _entropy, _scope));
    }

    public Task SetAsync(SecretName name, SecretValue value, CancellationToken ct = default)
    {
        if (_identities is { Length: > 0 }) AclHelper.EnsureRestrictedDirectory(_directory, _identities);
        else Directory.CreateDirectory(_directory);
        var blob = DpapiProtector.Protect(value.Reveal(), _entropy, _scope);
        if (_identities is { Length: > 0 }) AclHelper.WriteAllBytesRestricted(PathFor(name), blob, _identities);
        else File.WriteAllBytes(PathFor(name), blob);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(SecretName name, CancellationToken ct = default)
    {
        var file = PathFor(name);
        if (File.Exists(file)) File.Delete(file);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<SecretName> ListAsync(string prefix, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        if (!Directory.Exists(_directory)) yield break;
        foreach (var f in Directory.EnumerateFiles(_directory, "*" + Extension).OrderBy(f => f, StringComparer.Ordinal))
        {
            var text = Path.GetFileNameWithoutExtension(f).Replace('~', ':');
            if (SecretName.TryParse(text, out var n) && n.ToString().StartsWith(prefix ?? "", StringComparison.OrdinalIgnoreCase))
                yield return n;
        }
    }

    private string PathFor(SecretName name) => Path.Combine(_directory, name.ToString().Replace(':', '~') + Extension);
}
