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

// The suite's grammar (<noun> <verb>, --format json) is the shared router's; the few commands that are not verbs of a noun keep the switch below.
if (!SyncCli.Legacy.Contains(command))
{
    // A command that was renamed says so: a service or a script that still runs the old form should be told the new one, not just "unknown".
    if (RetiredCommands.ReplacementFor(command) is { } replacement)
    {
        Console.Error.WriteLine($"'dbdatasync {command}' is now 'dbdatasync {replacement}'.");
        return 2;
    }
    if (command == "config" && rest.Length > 0 && RetiredCommands.ConfigReplacementFor(rest[0]) is { } moved)
    {
        Console.Error.WriteLine($"'dbdatasync config {rest[0]}' is now 'dbdatasync {moved}'.");
        return 2;
    }
    // A command that is gone and has no new form says what replaced it.
    if (RetiredCommands.RemovedBecause(command) is { } removed)
    {
        Console.Error.WriteLine(removed);
        return 2;
    }
    return await SyncCli.Router(InformationalVersion()).RunAsync(args, DbDataKit.Cli.CliContext.Console());
}

return command switch
{
    "tool" => ToolCommand.Run(rest),
    "update" => await UpdateCommand.RunAsync(rest),
    "launcher" => await LauncherCommand.RunAsync(rest),
    "internal" => await InternalCommand.RunAsync(rest),
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
