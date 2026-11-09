using System.Text;
using DbDataKit.Secrets;
using DbDataKit.Secrets.Store;

namespace DbDataWorkspace.Server;

/// <summary>The server's own encrypted secret store, in its directory: what a state connection's password and the publisher's key refer to (<c>secret:dbdata:workspace:...</c>). Never read through the management API.</summary>
public static class WorkspaceSecrets
{
    public static EncryptedFileSecretStore Open(string directory)
    {
        IKeyProtector? protector = OperatingSystem.IsWindows() ? new DbDataKit.Secrets.Windows.DpapiKeyProtector(Encoding.UTF8.GetBytes("DbDataWorkspace")) : null;
        return new EncryptedFileSecretStore(SecretKeys.StoreDirectory(directory), SecretKeys.Open(directory, new SecretKeyOptions(), protector));
    }
}
