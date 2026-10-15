using System.Runtime.CompilerServices;

namespace DbData.Secrets;

/// <summary>
/// The seam to an OS keyring (Credential Manager, Keychain, Secret Service). A concrete implementation
/// is expected to wrap <c>ClrKernel.Core.Secrets.SecretStore</c> (Phase 0 decision pending; this package
/// deliberately does not reference it). Values cross this interface as strings because that is what the
/// underlying store exposes.
/// </summary>
public interface IKeyringBackend
{
    Task<string?> GetAsync(string service, string name, CancellationToken ct);
    Task SetAsync(string service, string name, string value, CancellationToken ct);
    Task DeleteAsync(string service, string name, CancellationToken ct);
    /// <summary>Names (never values) under a service whose name starts with the prefix.</summary>
    IAsyncEnumerable<string> ListAsync(string service, string prefix, CancellationToken ct);
}

/// <summary>
/// Provider for <c>keyring:&lt;service&gt;/&lt;name&gt;</c> references and, for <c>secret:&lt;name&gt;</c>, the
/// keyring item <c>dbdata</c>/<c>&lt;name&gt;</c> (service configurable). Also an <see cref="ISecretStore"/>.
/// Constructed without a backend it is inert: it handles nothing and the chain moves on, so wiring it into a
/// default chain is safe before the backend decision is made.
/// </summary>
public sealed class KeyringSecretProvider(IKeyringBackend? backend = null, string defaultService = "dbdata")
    : ISecretProvider, ISecretStore
{
    public string Name => "keyring";

    public bool IsAvailable => backend is not null;

    public bool CanHandle(SecretRef r) =>
        backend is not null && (r.Kind == SecretRefKind.Keyring || r.Kind == SecretRefKind.Secret);

    public async ValueTask<SecretValue?> TryResolveAsync(SecretRef r, CancellationToken ct = default)
    {
        var (service, item) = r.Kind == SecretRefKind.Keyring ? (r.Service!, r.Item!) : (defaultService, r.Target);
        var value = await Require().GetAsync(service, item, ct).ConfigureAwait(false);
        return string.IsNullOrEmpty(value) ? null : SecretValue.From(value);
    }

    public Task SetAsync(SecretName name, SecretValue value, CancellationToken ct = default) =>
        Require().SetAsync(defaultService, name.ToString(), value.RevealString(), ct);

    public Task DeleteAsync(SecretName name, CancellationToken ct = default) =>
        Require().DeleteAsync(defaultService, name.ToString(), ct);

    public async IAsyncEnumerable<SecretName> ListAsync(string prefix, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var n in Require().ListAsync(defaultService, prefix ?? "", ct).ConfigureAwait(false))
            if (SecretName.TryParse(n, out var name)) yield return name;
    }

    private IKeyringBackend Require() =>
        backend ?? throw new PlatformNotSupportedException(
            "No keyring backend is configured. See DbData.Secrets.Keyring README (wrap ClrKernel.Core.Secrets).");
}
