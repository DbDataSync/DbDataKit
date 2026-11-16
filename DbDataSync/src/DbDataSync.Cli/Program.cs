using System.Reflection;
using DbDataSync.Cli;

if (args.Length == 0 || CliOptions.Has(args, "--help") || CliOptions.Has(args, "-h"))
{
    Help.Print();
    return args.Length == 0 ? 1 : 0;
}

GitOwnershipValidation.DisableIfInteractive();

var command = args[0].ToLowerInvariant();
var rest = args.Skip(1).ToArray();

// The Sync server's commands are gone (plan 33 SC23): each answers with where its feature went (exit 2).
if (await RemovedServer.Commands.TryAnswerAsync(args, Console.Error) is { } removed)
    return removed;

// The suite's grammar (<noun> <verb>, --format json) is the shared router's; the few commands that are not verbs of a noun keep the switch below.
if (!SyncCli.Legacy.Contains(command))
    return await SyncCli.Router(InformationalVersion()).RunAsync(args, DbDataKit.Cli.CliContext.Console());

return command switch
{
    "tool" => ToolCommand.Run(rest),
    "version" => Version(),
    _ => Unknown(command),
};

// AssemblyName.Version, not this — it's a strict 4-part numeric System.Version, and the SDK
// derives it from <Version>'s numeric core alone, silently dropping any prerelease label
// (-alpha.<seconds> on every dev build since the local-tool version convention moved into MSBuild;
// -beta on a release.yml prerelease tag). AssemblyInformationalVersionAttribute is what SDK-style
// projects stamp with the *whole* <Version> string, prerelease label included, precisely so a tool
// asked "what version are you" can answer honestly rather than truncating the one part of the
// string that says "this build isn't the real thing."
static string InformationalVersion() => typeof(Help).Assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? typeof(Help).Assembly.GetName().Version?.ToString() ?? "unknown";

static int Version()
{
    Console.WriteLine(InformationalVersion());
    return 0;
}

static int Unknown(string command)
{
    Console.Error.WriteLine($"Unknown command '{command}'.");
    Help.Print();
    return 1;
}
