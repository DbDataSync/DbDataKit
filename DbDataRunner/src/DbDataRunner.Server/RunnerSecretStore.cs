using System.Text;
using DbDataKit.Secrets;
using DbDataKit.Secrets.Store;

namespace DbDataRunner.Server;

/// <summary>
/// The runner's own encrypted store of secrets (plan 28c E20d): <c>secrets/secrets.yaml</c> and its key ring in the server's directory, opened the first time a secret is stored or looked up (so a runner that never receives one
/// creates no key). <see cref="Open"/> is what <c>PUT /api/v1/secrets/{name}</c> writes to; <see cref="Provider"/> is the last link of the chain a connection's references resolve against, after the environment and the credentials
/// directory, so what an operator set by hand is never overridden. A store that cannot be opened (a key it may not create) says why on a write and has no value on a read: a run then fails naming the slot, the connection and the reference.
/// </summary>
public sealed class RunnerSecretStore(string directory, Func<string, string?>? environment = null)
{
    private readonly object _gate = new();
    private EncryptedFileSecretStore? _store;

    /// <summary>The store, opened; throws what the open threw (the message names the file) when the runner cannot keep a secret.</summary>
    public EncryptedFileSecretStore Open()
    {
        lock (_gate)
        {
            if (_store is not null) return _store;
            Directory.CreateDirectory(directory);
            IKeyProtector? protector = OperatingSystem.IsWindows() ? new DbDataKit.Secrets.Windows.DpapiKeyProtector(Encoding.UTF8.GetBytes("DbDataRunner")) : null;
            _store = new EncryptedFileSecretStore(SecretKeys.StoreDirectory(directory), SecretKeys.Open(directory, new SecretKeyOptions(), protector, environment));
            return _store;
        }
    }

    /// <summary>The store as a provider that opens it on first use and answers "no value" when it cannot be opened or has no file yet.</summary>
    public ISecretProvider Provider => new Lazy(this, directory);

    private sealed class Lazy(RunnerSecretStore owner, string directory) : ISecretProvider
    {
        public string Name => "runner-store";

        public bool CanHandle(SecretRef reference) => reference.Kind == SecretRefKind.Secret && SecretName.TryParse(reference.Target, out _);

        public async ValueTask<SecretValue?> TryResolveAsync(SecretRef reference, CancellationToken ct = default)
        {
            // nothing was ever stored here: no key is made for a lookup
            if (!File.Exists(Path.Combine(SecretKeys.StoreDirectory(directory), EncryptedFileSecretStore.FileName))) return null;
            try { return await owner.Open().TryResolveAsync(reference, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException and (IOException or InvalidOperationException or UnauthorizedAccessException or SecretStoreException))
            {
                return null;
            }
        }
    }
}
