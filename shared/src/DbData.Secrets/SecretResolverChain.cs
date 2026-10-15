namespace DbData.Secrets;

/// <summary>
/// Tries each provider that can handle the reference, in order; the first non-null value wins.
/// Resolved values are registered with the optional redactor and every attempt is audited by name.
/// </summary>
public sealed class SecretResolverChain(
    IEnumerable<ISecretProvider> providers,
    ISecretAudit? audit = null,
    IRedactor? redactor = null) : ISecretResolver
{
    private readonly ISecretProvider[] _providers = providers.ToArray();

    public async ValueTask<SecretValue> ResolveAsync(SecretRef reference, CancellationToken ct = default)
    {
        foreach (var p in _providers)
        {
            if (!p.CanHandle(reference)) continue;
            var value = await p.TryResolveAsync(reference, ct).ConfigureAwait(false);
            if (value is null) continue;
            redactor?.Register(value);
            audit?.Resolved(reference.ToAuditString(), p.Name, true);
            return value;
        }
        audit?.Resolved(reference.ToAuditString(), null, false);
        throw new SecretNotFoundException(reference.ToAuditString());
    }
}

/// <summary>Audit sink that forwards a formatted line (names only) to a delegate, e.g. a logger.</summary>
public sealed class DelegateSecretAudit(Action<string> write) : ISecretAudit
{
    public void Resolved(string reference, string? provider, bool found) =>
        write(found ? $"secret resolved: {reference} via {provider}" : $"secret NOT FOUND: {reference}");
}
