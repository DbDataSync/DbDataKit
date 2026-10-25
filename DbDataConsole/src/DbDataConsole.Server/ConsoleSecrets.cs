using System.Text;
using DbDataKit.Secrets;
using DbDataKit.Secrets.Store;

namespace DbDataConsole.Server;

/// <summary>The console's secrets: the shared encrypted store in its directory, where the key that signs for every server it manages is kept (never in a file a person edits, and never in a log).</summary>
public static class ConsoleSecrets
{
    public static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("DbDataKitConsole");

    public static EncryptedFileSecretStore Open(string consoleDirectory, Func<string, string?>? environment = null)
    {
        var directory = Path.GetFullPath(consoleDirectory);
        IKeyProtector? protector = OperatingSystem.IsWindows() ? new DbDataKit.Secrets.Windows.DpapiKeyProtector(DpapiEntropy) : null;
        return new EncryptedFileSecretStore(SecretKeys.StoreDirectory(directory), SecretKeys.Open(directory, new SecretKeyOptions(), protector, environment));
    }
}
