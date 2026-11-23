using DbDataKit.Cli;

namespace DbDataSync.Cli;

/// <summary>
/// Sync's command line as nouns and verbs on the shared router (plan 13, C07): the same grammar and the same <c>--format json</c> document as the other products. Each verb forwards to the
/// command class that has always done the work. Those classes write with <see cref="Console"/>, so while a verb runs the console is pointed at the command context (see
/// <see cref="ContextScope"/>) and the JSON form captures what the text form prints. <c>tool</c> and <c>version</c> are not verbs of a noun and keep their own dispatch in <c>Program.cs</c>.
/// The Sync server's commands are gone (plan 33 SC23) and answer with where their feature went (<see cref="RemovedServer"/>).
/// </summary>
public static class SyncCli
{
    public const string Program = "dbdatasync";

    /// <summary>The commands that stay outside the router: flag-driven.</summary>
    public static readonly IReadOnlySet<string> Legacy = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "tool", "version" };

    /// <summary>What <c>ui mcp</c> never offers: nothing that loads an installed library or opens a connection with a login, and nothing that is long-running or interactive (<see cref="McpOptions.Always"/>).</summary>
    private static readonly string[] NotForAgents = ["library validate"];

    public static NounRouter Router(string version = "0", Func<string, string?>? environment = null)
    {
        NounRouter? router = null;
        router = Build(version, () => router!, environment);
        return router;
    }

    private static NounRouter Build(string version, Func<NounRouter> self, Func<string, string?>? environment) => new(Program,
    [
        new Noun("script", "The .csx scripts of the configuration",
        [
            new Verb("list", "The scripts and the slot each fills (--json: one JSON document, for the Workspace)", EffectClass.OfflineOnly, "[--repo <path>] [--json]", (args, context) => RunScript("list", args, context), ScriptCommand.ListSchema),
            new Verb("test", "Run a script against generated sample input and show what it did (--json: one JSON document, for the Workspace)", EffectClass.RepoFilesOnly, "<name> [--rows <n>] [--repo <path>] [--json]", (args, context) => RunScript("test", args, context), ScriptCommand.TestSchema),
        ]),
        new Noun("provisioning", "What a replication needs of its two databases",
        [
            new Verb("plan", "Plan the create or alter of the target tables of a replication offline, from the catalog cache of the repository (--from-cache; --json: one JSON document, for the Workspace). Applying is the set's provision task on the Runner", EffectClass.OfflineOnly, "<replication> --from-cache [--repo <path>] [--json]", async (args, context) => { using var _ = ContextScope.Enter(context); return await ProvisioningCommand.RunAsync(["plan", .. args], context.Data); }, ProvisioningCommand.PlanSchema),
        ]),
        new Noun("library", "ADO.NET libraries restored into the repository",
        [
            Forward("install", "Restore a package as a library", EffectClass.RepoFilesOnly, "<packageId> ... [--as <id>] --version <v> [--factory-type <type>] [--source <feed>]", a => LibraryCommand.RunAsync(["install", .. a])),
            Forward("sync", "Restore the libraries that are on record but not restored", EffectClass.RepoFilesOnly, "[<id>]", a => LibraryCommand.RunAsync(["sync", .. a])),
            Forward("list", "The installed libraries", EffectClass.OfflineOnly, "", a => LibraryCommand.RunAsync(["list", .. a])),
            Forward("uninstall", "Remove a library", EffectClass.RepoFilesOnly, "<id>", a => LibraryCommand.RunAsync(["uninstall", .. a])),
            Forward("validate", "Check that a library works against a connection", EffectClass.TargetReadOnly, "<id> --connection <name> [--repo <path>]", a => LibraryCommand.RunAsync(["validate", .. a])),
        ]),
        new Noun("driver", "Drivers added without a rebuild",
        [
            Forward("install", "Add a driver descriptor and its library", EffectClass.RepoFilesOnly, "<id> --library <name> --version <v> [--factory-type <type>] [--from mysql] [--display-name <name>]", a => DriverCommand.RunAsync(["install", .. a])),
            Forward("list", "The installed drivers", EffectClass.OfflineOnly, "", a => DriverCommand.RunAsync(["list", .. a])),
            Forward("uninstall", "Remove a driver", EffectClass.RepoFilesOnly, "<id>", a => DriverCommand.RunAsync(["uninstall", .. a])),
        ]),
        new Noun("package", "The repository as a definition package for a runner",
        [
            Forward("publish", "Make the replications of the repository a package for a runner (plan 28c E08): --out writes it, --check says what it would hold; nothing is compiled and no secret goes. The console deploys it to a runner", EffectClass.RepoFilesOnly,
                "--to runner (--out <directory> | --check) [--json] [--package <name>] [--platform linux-x64|win-x64] [--source <feed>] [--runtime-range <range>] [--repo <path>]",
                a => PackageCommand.RunAsync(["publish", .. a])),
        ]),
        LineageCommand.Noun(),
        UiNoun.Create(self, Program, version, NotForAgents),
    ], version, () => DbDataKit.State.SuiteEnvironment.OldNameProblem(environment));

    private static Verb Forward(string name, string summary, EffectClass effect, string usage, Func<string[], Task<int>> run) =>
        new(name, summary, effect, usage, async (args, context) =>
        {
            using var _ = ContextScope.Enter(context);
            return await run(args);
        }, DataSchemas.None);

    private static async Task<int> RunScript(string verb, string[] args, CliContext context)
    {
        using var _ = ContextScope.Enter(context);
        return await ScriptCommand.RunAsync([verb, .. args], context.Data);
    }

}

/// <summary>While a verb runs, <see cref="Console.Out"/>, <see cref="Console.Error"/> and <see cref="Console.In"/> are the command context's, so output goes where the router put it (the JSON form's <c>messages</c> and <c>errors</c>).</summary>
internal sealed class ContextScope : IDisposable
{
    private readonly TextWriter _out = Console.Out;
    private readonly TextWriter _error = Console.Error;
    private readonly TextReader _in = Console.In;

    private ContextScope(CliContext context)
    {
        Console.SetOut(context.Out);
        Console.SetError(context.Error);
        Console.SetIn(context.In);
    }

    public static ContextScope Enter(CliContext context) => new(context);

    public void Dispose()
    {
        Console.SetOut(_out);
        Console.SetError(_error);
        Console.SetIn(_in);
    }
}
