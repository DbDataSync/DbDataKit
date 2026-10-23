using System.Collections.Concurrent;
using DbDataKit.Secrets;

namespace DbDataSync.Core.Secrets;

/// <summary>A store that keeps its secrets in this process only. For tests, and for a host that must not touch disk.</summary>
public sealed class InMemorySecretProvider : ISecretProvider, ISecretStore
{
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);

    public string Name => "memory";

    public bool CanHandle(SecretRef reference) => reference.Kind == SecretRefKind.Secret;

    public ValueTask<SecretValue?> TryResolveAsync(SecretRef reference, CancellationToken ct = default) =>
        ValueTask.FromResult(_values.TryGetValue(reference.Target, out var value) ? SecretValue.From(value) : null);

    public Task SetAsync(SecretName name, SecretValue value, CancellationToken ct = default)
    {
        _values[name.ToString()] = value.RevealString();
        return Task.CompletedTask;
    }

    public Task DeleteAsync(SecretName name, CancellationToken ct = default)
    {
        _values.TryRemove(name.ToString(), out _);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<SecretName> ListAsync(string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var key in _values.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(k => k, StringComparer.Ordinal))
            yield return SecretName.Parse(key);
        await Task.CompletedTask;
    }
}
