using System.Text;
using DbDataKit.Secrets;
using DbDataKit.Secrets.Store;
using DbDataKit.Secrets.Windows;

namespace DbDataSync.Core.Secrets;

/// <summary>
/// The secrets Sync reads and writes, by name (<see cref="SecretRefs"/>), over the shared encrypted store
/// (<see cref="EncryptedFileSecretStore"/>) with the environment behind it. Sync's own code is synchronous, so this offers the
/// few calls it makes in that shape; the providers and the store are the shared ones, and the names are
/// <c>dbdata:sync:...</c>.
/// <para>
/// The store lives in <c>secrets/</c> under the repository root, its key in <c>secrets/keys</c> (DPAPI on Windows, a 0600 file elsewhere, created on first use)
/// or in the key file <c>DBDATA_SECRETS_KEY_FILE</c> names. Nothing here ever logs or prints a value.
/// </para>
/// </summary>
public sealed class SecretStore
{
    /// <summary>Entropy for the DPAPI protection of the store's key (machine scope).</summary>
    public static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("DbDataSync");

    private readonly SecretResolverChain _resolver;
    private readonly ISecretStore? _writable;
    private readonly string[] _providerNames;

    public SecretStore(IReadOnlyList<ISecretProvider> providers, ISecretStore? writable)
    {
        _resolver = new SecretResolverChain(providers);
        _writable = writable;
        _providerNames = providers.Select(p => p.Name).ToArray();
    }

    /// <summary>The store in <c>secrets/</c> under the repository root <paramref name="repoRoot"/> (kept out of git), then the environment (<c>DBDATA_SECRET_...</c>) and the credentials directory.</summary>
    public static SecretStore Open(string repoRoot, Func<string, string?>? environment = null)
    {
        var dataDirectory = Path.GetFullPath(repoRoot);
        IKeyProtector? protector = OperatingSystem.IsWindows() ? new DpapiKeyProtector(DpapiEntropy) : null;
        var keys = SecretKeys.Open(dataDirectory, new SecretKeyOptions(), protector, environment);
        var store = new EncryptedFileSecretStore(SecretKeys.StoreDirectory(dataDirectory), keys);
        EnsureGitignored(dataDirectory);

        // systemd LoadCredential= and container secret mounts: secret:<name> resolves to a file in that directory.
        var credentials = Environment.GetEnvironmentVariable("CREDENTIALS_DIRECTORY");
        return new SecretStore(
            [store, new EnvSecretProvider(environment), new FileSecretProvider(new FileSecretOptions { SecretsDirectory = string.IsNullOrWhiteSpace(credentials) ? null : credentials })],
            store);
    }

    /// <summary>The repository root is a git repository, and the store and its key have no business in version control. Appended once, the first time the store is opened.</summary>
    private static void EnsureGitignored(string repoRoot)
    {
        if (!Directory.Exists(Path.Combine(repoRoot, ".git"))) return;
        const string entry = "secrets/";
        var path = Path.Combine(repoRoot, ".gitignore");
        var existing = File.Exists(path) ? File.ReadAllLines(path) : [];
        if (existing.Contains(entry)) return;

        using var writer = new StreamWriter(path, append: true);
        if (existing.Length > 0 && !string.IsNullOrEmpty(existing[^1])) writer.WriteLine();
        writer.WriteLine(entry);
    }

    /// <summary>For tests: these providers in this order, then the environment as in production; the first that can store is where <see cref="Store"/> writes.</summary>
    public static SecretStore ForProviders(params ISecretProvider[] providers) =>
        new([.. providers, new EnvSecretProvider()], providers.OfType<ISecretStore>().FirstOrDefault());

    public bool CanStore => _writable is not null;

    public IReadOnlyList<string> ProviderNames => _providerNames;

    /// <summary>The environment variable that answers <paramref name="name"/> when the store has nothing.</summary>
    public string EnvName(string name) => EnvSecretProvider.VariableNameFor(ParseName(name).ToString());

    public void Store(string name, string value)
    {
        if (_writable is null) throw new InvalidOperationException("No secret store that can be written to is available.");
        using var secret = SecretValue.From(value);
        _writable.SetAsync(ParseName(name), secret).GetAwaiter().GetResult();
    }

    public bool TryResolve(string name, out string value)
    {
        try { value = Resolve(name); return true; }
        catch (SecretNotFoundException) { value = ""; return false; }
    }

    /// <summary>Throws <see cref="SecretNotFoundException"/> when nothing has it.</summary>
    public string Resolve(string name)
    {
        using var value = _resolver.ResolveAsync(ParseName(name).ToRef()).AsTask().GetAwaiter().GetResult();
        return value.RevealString();
    }

    public void Delete(string name)
    {
        if (_writable is null) return;
        _writable.DeleteAsync(ParseName(name)).GetAwaiter().GetResult();
    }

    private static SecretName ParseName(string name) =>
        SecretName.TryParse(name, out var parsed) ? parsed
            : throw new FormatException($"'{name}' is not a secret name; expected 'dbdata:sync:<kind>:<name>'.");
}
