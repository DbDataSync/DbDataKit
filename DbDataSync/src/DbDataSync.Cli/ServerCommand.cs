namespace DbDataSync.Cli;

/// <summary>
/// <c>dbdatasync server run|setup</c> (plan 13, C06). <c>run</c> is what <c>serve</c> was: the API, the scheduler and the web console in one foreground process, which is
/// also what the Windows service and the systemd unit run. <c>setup</c> is the interactive first-run walk-through that was top-level <c>setup</c>.
/// </summary>
public static class ServerCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            PrintUsage(args.Length == 0 ? Console.Error : Console.Out);
            return args.Length == 0 ? 2 : 0;
        }

        var rest = args[1..];
        switch (args[0].ToLowerInvariant())
        {
            case "run": return await ServeCommand.RunAsync(rest);
            case "setup": return await SetupCommand.RunAsync(rest);
            default:
                Console.Error.WriteLine($"dbdatasync server: '{args[0]}' is not a verb.");
                PrintUsage(Console.Error);
                return 2;
        }
    }

    private static void PrintUsage(TextWriter writer) =>
        writer.WriteLine(
            """
            Usage:
              dbdatasync server run [--repo <path>] [--state-db <path>] [--url <url>]
                  Start the API, the scheduler and the web console in one foreground process (what the service runs).
              dbdatasync server setup [--repo <path>]
                  The interactive first-run walk-through, or a review screen for an existing install.
            """);
}
