using System.Text;
using DbDataKit.Connections;
using DbDataKit.Secrets;
using DbDataKit.Secrets.Store;

namespace DbDataBuild.Cli.Server;

/// <summary>
/// The Build server's secrets (plan 13, R04): the shared encrypted store in the server's directory, and the logins taken from it for each command. A connection's logins are the secrets
/// <c>dbdata:build:connection:&lt;connection&gt;:read</c> and <c>...:write</c> (stored with <c>dbdatabuild secret set</c>, never as an argument); a command's child process gets each as the
/// variable the command line reads, <c>DBDATA_BUILD_&lt;CONNECTION&gt;_&lt;READ|WRITE&gt;</c>. The two roles are separate secrets and separate variables: a connection with only a read login gets only the read variable,
/// and nothing here ever copies one into the other, so Build's rule of no fallback from read to write holds.
/// <para>
/// The secrets belong to the server, not to a project, so projects served by one server share the logins of connections that have the same name.
/// </para>
/// </summary>
internal static class ServerSecrets
{
    public const string Product = "build";
    public const string Kind = "connection";

    public static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("DbDataBuild");

    public static EncryptedFileSecretStore Open(string serverDirectory, Func<string, string?>? environment = null)
    {
        var directory = Path.GetFullPath(serverDirectory);
        IKeyProtector? protector = OperatingSystem.IsWindows() ? new DbDataKit.Secrets.Windows.DpapiKeyProtector(DpapiEntropy) : null;
        return new EncryptedFileSecretStore(SecretKeys.StoreDirectory(directory), SecretKeys.Open(directory, new SecretKeyOptions(), protector, environment));
    }

    /// <summary>The name of a connection's login in the store.</summary>
    public static SecretName NameOf(string connection, LoginRole role) => new(Product, Kind, connection, role == LoginRole.Read ? "read" : "write");

    /// <summary>The login variables for a child process: one per stored login, none for a role that has no secret. A secret that cannot be read is left out, and the command then reports the missing login itself.</summary>
    public static async Task<IReadOnlyDictionary<string, string>> LoginsAsync(EncryptedFileSecretStore store, CancellationToken cancellationToken)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        await foreach (var name in store.ListAsync($"dbdata:{Product}:{Kind}:", cancellationToken).ConfigureAwait(false))
        {
            var role = name.Part switch { "read" => LoginRole.Read, "write" => LoginRole.Write, _ => (LoginRole?)null };
            if (role is null) continue;
            using var value = await store.TryResolveAsync(name.ToRef(), cancellationToken).ConfigureAwait(false);
            if (value is null || value.IsEmpty) continue;
            variables[LoginEnvironment.Name("build", name.Name, role.Value)] = value.RevealString();
        }
        return variables;
    }
}
