using DbDataKit.Secrets;
using DbDataKit.Secrets.Cli;
using DbDataProxy.Server.Configuration;
using DbDataProxy.Server.Secrets;

namespace DbDataProxy.Server.Hosting;

/// <summary>
/// <c>DbDataProxy secret ...</c>: the shared secret commands over this installation's encrypted store, resolving the way the server does (the store, inline DPAPI on Windows, the environment,
/// the credentials directory). <c>check</c> without arguments resolves every reference the settings hold.
/// </summary>
public static class SecretCommand
{
    public static async Task<int> RunAsync(string[] args, ProxyOptions options, IConsole console, Func<string, string?>? environment = null)
    {
        DbDataKit.Secrets.Store.EncryptedFileSecretStore store;
        try { store = SecretsRuntime.OpenStore(options, environment); }
        catch (Exception ex) when (ex is DbDataKit.Secrets.Store.KeyPermissionException or InvalidDataException or FileNotFoundException or UnauthorizedAccessException)
        {
            console.WriteError(ex.Message);
            return 1;
        }

        var resolver = new SecretResolverChain(SecretsRuntime.CreateProviders(options, store));
        Func<SecretValue, string>? protect = OperatingSystem.IsWindows() ? ProtectInline : null;

        var cli = new SecretCli(resolver, store, store, () => SecretsRuntime.ConfiguredReferences(options), protect);
        return await cli.ExecuteAsync(args, console);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string ProtectInline(SecretValue value) =>
        DbDataKit.Secrets.Windows.DpapiProtector.ToInlineRef(value, SecretsRuntime.DpapiEntropy, System.Security.Cryptography.DataProtectionScope.LocalMachine);
}
