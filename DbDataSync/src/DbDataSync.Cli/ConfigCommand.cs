using System.Text.Json;

namespace DbDataSync.Cli;

/// <summary>
/// <c>dbdatasync config check|get|set</c>: the server's own configuration. Certificates, secrets, libraries and drivers were verbs of <c>config</c> from phase 115 to plan 13 (C06) and are
/// nouns of their own again (<c>cert</c>, <c>secret</c>, <c>library</c>, <c>driver</c>), the same names every DbData command line uses.
/// <para>
/// <c>check</c> is new here: it's <c>doctor</c>'s old non-interactive entry point, unchanged in
/// behaviour, reached one level down. <c>setup</c> is deliberately not nested under <c>config</c> —
/// it stays a purely interactive, top-level experience; its review screen calls
/// <see cref="ReadinessChecks"/> in-process the same way <c>check</c> does here.
/// </para>
/// </summary>
public static class ConfigCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        var rest = args[1..];
        return args[0].ToLowerInvariant() switch
        {
            "check" => await CheckAsync(rest),
            "get" => ConfigValueCommand.Get(rest),
            "set" => ConfigValueCommand.Set(rest),
            var other => Unknown(other),
        };
    }

    /// <summary>Read-only. Exits 0 when nothing is <see cref="CheckStatus.Fail"/>, 1 otherwise — for a
    /// CI smoke test or a service wrapper that cannot answer an interactive prompt.</summary>
    private static async Task<int> CheckAsync(string[] args)
    {
        var asJson = CliOptions.Has(args, "--json");
        var context = ReadinessChecks.BuildContext(args);
        var results = await ReadinessChecks.RunChecksAsync(context);

        if (asJson)
        {
            // No free text mixed into machine-readable output — a CI reader parses this as JSON.
            Console.WriteLine(JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            foreach (var result in results)
            foreach (var line in ReadinessChecks.FormatResult(result))
                Console.WriteLine(line);
        }

        return results.Any(r => r.Status == CheckStatus.Fail) ? 1 : 0;
    }

    private static int Unknown(string sub)
    {
        if (RetiredCommands.ConfigReplacementFor(sub) is { } replacement)
        {
            Console.Error.WriteLine($"'dbdatasync config {sub}' is now 'dbdatasync {replacement}'.");
            return 2;
        }
        Console.Error.WriteLine($"Unknown config subcommand '{sub}'.");
        PrintUsage();
        return 1;
    }

    private static void PrintUsage() =>
        Console.Error.WriteLine(
            """
            Usage:
              dbdatasync config check [--repo <path>] [--json]
              dbdatasync config get <key> [--repo <path>]
              dbdatasync config set <key> <value> [--repo <path>]
            (certificates, secrets, libraries and drivers are commands of their own: `dbdatasync cert`, `secret`, `library`, `driver`.)
            """);
}
