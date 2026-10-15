namespace DbData.Secrets;

/// <summary>Resolves a reference to a value. Throws <see cref="SecretNotFoundException"/> when nothing can.</summary>
public interface ISecretResolver
{
    ValueTask<SecretValue> ResolveAsync(SecretRef reference, CancellationToken ct = default);
}

/// <summary>One link of a provider chain. Returns null when it handles the reference but has no value.</summary>
public interface ISecretProvider
{
    string Name { get; }
    bool CanHandle(SecretRef reference);
    ValueTask<SecretValue?> TryResolveAsync(SecretRef reference, CancellationToken ct = default);
}

/// <summary>Writable store. Names only are ever listed.</summary>
public interface ISecretStore
{
    Task SetAsync(SecretName name, SecretValue value, CancellationToken ct = default);
    Task DeleteAsync(SecretName name, CancellationToken ct = default);
    IAsyncEnumerable<SecretName> ListAsync(string prefix, CancellationToken ct = default);
}

public interface IRedactor
{
    string Redact(string text);
    void Register(SecretValue value);
}

/// <summary>Audit hook. Implementations receive names only, never values.</summary>
public interface ISecretAudit
{
    void Resolved(string reference, string? provider, bool found);
}

public sealed class SecretNotFoundException(string reference)
    : Exception($"Secret '{reference}' was not found by any configured provider.")
{
    public string Reference { get; } = reference;
}
