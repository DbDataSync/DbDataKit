namespace DbDataKit.Secrets.Store;

/// <summary>Where the store's key comes from.</summary>
public sealed record SecretKeyOptions
{
    /// <summary>The environment variable that names a key file provided from outside (it takes the place of <c>secrets.keyFile</c> when that is empty).</summary>
    public const string KeyFileVariable = "DBDATA_SECRETS_KEY_FILE";

    /// <summary>A key file provided by the platform (<c>secrets.keyFile</c> in <c>server.yaml</c>); empty means the key ring under the data directory.</summary>
    public string? KeyFile { get; init; }
}

public static class SecretKeys
{
    /// <summary>The folder the store keeps its files in, under the data directory.</summary>
    public static string StoreDirectory(string dataDirectory) => Path.Combine(dataDirectory, "secrets");

    /// <summary>
    /// The key source for a data directory: the key file the options (or <see cref="SecretKeyOptions.KeyFileVariable"/>) name, otherwise the key ring in <c>&lt;data&gt;/secrets/keys</c>,
    /// created with its first key on first use. On Windows pass the DPAPI protector from <c>DbDataKit.Secrets.Windows</c>; elsewhere the default (a restricted file) is the protection.
    /// </summary>
    public static IKeySource Open(string dataDirectory, SecretKeyOptions? options = null, IKeyProtector? protector = null, Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var external = !string.IsNullOrWhiteSpace(options?.KeyFile) ? options!.KeyFile : environment(SecretKeyOptions.KeyFileVariable);
        if (!string.IsNullOrWhiteSpace(external)) return new ExternalKeyFile(external);

        var ring = new KeyRing(Path.Combine(StoreDirectory(dataDirectory), "keys"), protector);
        ring.EnsureActiveKey();
        ring.VerifyAll();
        return ring;
    }
}
