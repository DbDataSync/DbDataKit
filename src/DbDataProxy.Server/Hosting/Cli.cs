using DbDataProxy.Server.Auth;

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
            case "query":
                return await QueryCommand.RunAsync(rest);
            case "install":
                return ServiceInstaller.Install(rest);
            case "uninstall":
                return ServiceInstaller.Uninstall(rest);
            case "help" or "--help" or "-h":
                PrintHelp();
                return 0;
            case "run":
                var app = ProxyHost.Build(rest);
                await app.RunAsync();
                return 0;
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
          DbDataProxy query --user U --password P --sql "..." [--address URL] [--datasource N]
                                           [--compression none|gzip|zstd|lz4] [--insecure]
                                           Run a query against a proxy (smoke test)
          DbDataProxy install [--name N] [--account A --password P]
                                           Register the Windows service (run elevated)
          DbDataProxy uninstall [--name N] Remove the Windows service
          DbDataProxy help                 Show this help

        Configuration is read from appsettings.json beside the executable, environment variables
        prefixed DBPROXY_ (use __ for nesting) and command line options (--Proxy:Listen:Port=9000).
        """);
}
