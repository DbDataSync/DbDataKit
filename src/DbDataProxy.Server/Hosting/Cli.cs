using DbDataProxy.Server.Auth;
using DbDataProxy.Server.Configuration;

namespace DbDataProxy.Server.Hosting;

/// <summary>Command line entry point: runs the proxy by default, plus a few administrative commands.</summary>
public static class Cli
{
    public static async Task<int> RunAsync(string[] args)
    {
        string command = args.Length > 0 && !args[0].StartsWith('-') ? args[0].ToLowerInvariant() : "run";
        string[] rest = command == "run" && (args.Length == 0 || args[0].StartsWith('-')) ? args : args.Skip(1).ToArray();

        switch (command)
        {
            case "hash-password":
                return HashPassword(rest);
            case "protect-secret":
                return ProtectSecret(rest);
            case "query":
                return await QueryCommand.RunAsync(rest);
            case "config":
                return ConfigCommand.Run(rest);
            case "service":
                return await ServiceCli.RunAsync(rest, ServiceCliEnvironment.Real());
            case "install" or "uninstall":
                // Kept for scripts/install.ps1: the same as "service install" / "service uninstall".
                return await ServiceCli.RunAsync([command, .. rest], ServiceCliEnvironment.Real());
            case "cert":
                var proxyOptions = ProxyConfiguration.LoadOptions();
                return CertCli.Run(rest, proxyOptions, ProxyHost.ResolveDataDirectory(proxyOptions), new DbDataKit.Hosting.Cli.SystemConsole());
            case "help" or "--help" or "-h":
                PrintHelp();
                return 0;
            case "run":
                try
                {
                    var app = ProxyHost.Build(rest);
                    await app.RunAsync();
                    return 0;
                }
                catch (Exception ex) when (ex is ProxyConfigException or Secrets.PlainSecretNotAllowedException)
                {
                    // Invalid configuration: one precise message (key paths and sources, no secret values) and the documented exit code.
                    Console.Error.WriteLine(ex.Message);
                    return ProxyConfigException.ExitCode;
                }
            default:
                Console.Error.WriteLine($"Unknown command '{command}'.");
                PrintHelp();
                return 2;
        }
    }

    private static int HashPassword(string[] args)
    {
        string? password = args.Length > 0 ? args[0] : ReadPasswordFromConsole();
        if (string.IsNullOrEmpty(password))
        {
            Console.Error.WriteLine("A password is required.");
            return 2;
        }
        Console.WriteLine(PasswordHasher.Hash(password));
        return 0;
    }

    /// <summary>Prints a <c>dpapi:</c> reference (machine scope) for a secret, to paste into a config value. Windows only.</summary>
    private static int ProtectSecret(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("protect-secret needs Windows DPAPI; use env: or file: references on this platform.");
            return 2;
        }
        string? secret = args.Length > 0 ? args[0] : ReadPasswordFromConsole();
        if (string.IsNullOrEmpty(secret))
        {
            Console.Error.WriteLine("A secret is required.");
            return 2;
        }
        using var value = DbDataKit.Secrets.SecretValue.From(secret);
        Console.WriteLine(DbDataKit.Secrets.Windows.DpapiProtector.ToInlineRef(value, Secrets.SecretsRuntime.DpapiEntropy, System.Security.Cryptography.DataProtectionScope.LocalMachine));
        return 0;
    }

    private static string? ReadPasswordFromConsole()
    {
        if (Console.IsInputRedirected) return Console.In.ReadLine();
        Console.Error.Write("Password: ");
        var sb = new System.Text.StringBuilder();
        while (Console.ReadKey(intercept: true) is { Key: not ConsoleKey.Enter } key)
        {
            if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; }
            else sb.Append(key.KeyChar);
        }
        Console.Error.WriteLine();
        return sb.ToString();
    }

    private static void PrintHelp() => Console.WriteLine("""
        DbDataProxy - Arrow Flight SQL proxy for relational databases

        Usage:
          DbDataProxy [run]                Run the proxy (as a Windows service/systemd unit when started by one)
          DbDataProxy hash-password [pw]   Print a password hash for the Proxy:Users configuration
          DbDataProxy protect-secret [s]   Print a dpapi: reference (Windows, machine scope) for a config value
          DbDataProxy query --user U --password P --sql "..." [--address URL] [--datasource N]
                                           [--compression none|gzip|zstd|lz4] [--insecure]
                                           Run a query against a proxy (smoke test)
          DbDataProxy service install [--name N] [--account A] [--password-secret env:NAME|file:PATH]
                                           Register the Windows service (run elevated). It runs as NT SERVICE\<name>
                                           unless --account says otherwise (LocalSystem needs --allow-localsystem).
                                           --password still works but warns; prefer --password-secret or the prompt.
          DbDataProxy service uninstall|start|stop|restart|status|doctor [--name N]
          DbDataProxy service unit         Print a hardened systemd unit (also: service --systemd)
          DbDataProxy install|uninstall    Same as "service install|uninstall"
          DbDataProxy cert show|ensure|renew|check
                                           Show, create or replace the self-signed TLS certificate
          DbDataProxy --healthcheck [url]  Exit 0 when the running proxy answers /health/live (for Docker HEALTHCHECK)
          DbDataProxy config check [--file F]  Validate the configuration (exit code 0 valid, 2 invalid)
          DbDataProxy config show [--origin] [--file F]
                                           Print the effective configuration, secrets masked; --origin adds where each value came from
          DbDataProxy help                 Show this help

        Configuration is read from appsettings.json beside the executable, environment variables
        prefixed DBPROXY_ (use __ for nesting) and command line options (--Proxy:Listen:Port=9000).
        """);
}
