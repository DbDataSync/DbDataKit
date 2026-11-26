using System.Text;
using DbDataKit.Secrets;
using DbDataKit.Secrets.Store;

namespace DbDataWorkspace.Server;

/// <summary>The server's own encrypted secret store, in its directory: what a state connection's password refers to (<c>secret:dbdatakit:server:state:...</c>). Never read through the management API.</summary>
public static class WorkspaceSecrets
{
    /// <summary>The names a publisher key was kept under, when this server still published to Runners and Gateways itself.</summary>
    public const string PublisherKeyPrefix = "dbdatakit:workspace:publisher-key:";

    /// <summary>The file that listed the Runners and Gateways those keys were paired with.</summary>
    public const string PublishTargetsFile = "publish-targets.json";

    public static EncryptedFileSecretStore Open(string directory)
    {
        IKeyProtector? protector = OperatingSystem.IsWindows() ? new DbDataKit.Secrets.Windows.DpapiKeyProtector(Encoding.UTF8.GetBytes("DbDataWorkspace")) : null;
        return new EncryptedFileSecretStore(SecretKeys.StoreDirectory(directory), SecretKeys.Open(directory, new SecretKeyOptions(), protector));
    }

    /// <summary>
    /// Deletes what is left of the publisher keys a server of an earlier version paired: each key in the secret store and the list of the targets it was
    /// paired with. The server calls no Runner or Gateway, so nothing reads them any more; the console signs what it deploys with its own key. A store that
    /// does not exist is not made. Says what it deleted through <paramref name="log"/>; returns how many keys went.
    /// </summary>
    public static async Task<int> ForgetPublisherKeysAsync(string directory, Action<string> log, CancellationToken ct = default)
    {
        var targets = Path.Combine(directory, PublishTargetsFile);
        if (File.Exists(targets))
        {
            File.Delete(targets);
            log($"Deleted {PublishTargetsFile}: this server no longer publishes to the targets it lists; the console deploys.");
        }
        if (!Directory.Exists(SecretKeys.StoreDirectory(directory))) return 0;
        var store = Open(directory);
        var names = new List<SecretName>();
        await foreach (var name in store.ListAsync(PublisherKeyPrefix, ct)) names.Add(name);
        foreach (var name in names) await store.DeleteAsync(name, ct);
        if (names.Count > 0) log($"Deleted {names.Count} publisher key(s) from the secret store ({string.Join(", ", names)}): this server no longer publishes; the console signs what it deploys with its own key.");
        return names.Count;
    }
}
