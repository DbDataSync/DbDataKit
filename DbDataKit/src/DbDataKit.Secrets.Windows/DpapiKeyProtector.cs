using System.Runtime.Versioning;
using System.Security.Cryptography;
using DbDataKit.Secrets.Store;

namespace DbDataKit.Secrets.Windows;

/// <summary>
/// Keeps a data key under Windows DPAPI with per-product entropy. <see cref="DataProtectionScope.LocalMachine"/> (the default, for a service) lets any process on the machine that knows the entropy
/// decrypt it, so the key directory's access list is the real control (the key ring sets and checks it); <see cref="DataProtectionScope.CurrentUser"/> ties it to the account that wrote it. A key
/// protected here cannot be read on another machine, or with other entropy: moving a server goes through <c>secret export</c> and <c>import</c>, never through the key file.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiKeyProtector(byte[]? entropy, DataProtectionScope scope = DataProtectionScope.LocalMachine) : IKeyProtector
{
    public byte Kind => scope == DataProtectionScope.LocalMachine ? (byte)2 : (byte)3;

    public string Description => scope == DataProtectionScope.LocalMachine ? "DPAPI for the machine" : "DPAPI for the current user";

    public byte[] Protect(ReadOnlySpan<byte> key) => ProtectedData.Protect(key.ToArray(), entropy, scope);

    public byte[] Unprotect(ReadOnlySpan<byte> blob) => ProtectedData.Unprotect(blob.ToArray(), entropy, scope);
}

/// <summary>
/// Resolves only the inline <c>dpapi:&lt;base64&gt;</c> references (the output of <c>secret protect</c>): a single field's value kept under DPAPI in the configuration file, where a store is not wanted.
/// A named <c>secret:</c> reference is the encrypted store's, not this provider's.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiInlineProvider(byte[]? entropy, DataProtectionScope scope = DataProtectionScope.LocalMachine) : ISecretProvider
{
    public string Name => "dpapi-inline";

    public bool CanHandle(SecretRef reference) => reference.Kind == SecretRefKind.Dpapi;

    public ValueTask<SecretValue?> TryResolveAsync(SecretRef reference, CancellationToken ct = default)
    {
        byte[] blob;
        try { blob = Convert.FromBase64String(reference.Target); }
        catch (FormatException) { throw new FormatException("dpapi reference is not valid base64."); }
        return ValueTask.FromResult<SecretValue?>(DpapiProtector.Unprotect(blob, entropy, scope));
    }
}
