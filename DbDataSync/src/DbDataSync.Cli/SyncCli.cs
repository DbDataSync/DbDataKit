using DbDataKit.Cli;
using DbDataKit.Web.Trust;
using DbDataSync.Api.Management;

namespace DbDataSync.Cli;

/// <summary>
/// Sync's command line as nouns and verbs on the shared router (plan 13, C07): the same grammar and the same <c>--format json</c> document as the other products. Each verb forwards to the
/// command class that has always done the work. Those classes write with <see cref="Console"/>, so while a verb runs the console is pointed at the command context (see
/// <see cref="ContextScope"/>) and the JSON form captures what the text form prints. <c>tool</c>, <c>update</c>, <c>launcher</c>, <c>invite</c>, <c>internal</c> and <c>version</c> are not verbs of a
/// noun and keep their own dispatch in <c>Program.cs</c> until the updater is reopened (plan 13, D5) and users move to the console.
/// </summary>
public static class SyncCli
{
    public const string Program = "dbdatasync";

    /// <summary>The commands that stay outside the router: flag-driven or retired from the suite's grammar for now.</summary>
    public static readonly IReadOnlySet<string> Legacy = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "tool", "update", "launcher", "invite", "internal", "version" };

    /// <summary>What <c>ui mcp</c> never offers: nothing that sets a secret or loads an installed library, and nothing that is long-running or interactive (<see cref="McpOptions.Always"/>).</summary>
    private static readonly string[] NotForAgents = ["secret set", "secret remove", "library validate", "server pair", "server unpair"];

    public static NounRouter Router(string version = "0")
    {
        NounRouter? router = null;
        router = Build(version, () => router!);
        return router;
    }

    private static NounRouter Build(string version, Func<NounRouter> self) => new(Program,
    [
        new Noun("server", "The long-running process and its first-run setup",
        [
            Forward("run", "Start the API, the scheduler and the web console in one foreground process (what the service runs)", EffectClass.OfflineOnly, "[--repo <path>] [--state-db <path>] [--url <url>]", ServeCommand.RunAsync),
            Forward("setup", "The interactive first-run walk-through, or a review screen for an existing install", EffectClass.RepoFilesOnly, "[--repo <path>]", SetupCommand.RunAsync),
        ]).With([.. PairingVerbs.Create(args => new PairingFiles(SyncManagement.ServerYamlIn(DbDataSyncRoot.Resolve(args))))]),
        new Noun("service", "Registration of 'server run' with Windows or systemd",
        [
            Forward("install", "Register the service (run it again to update it); needs elevation", EffectClass.TargetWrites, "[--repo <path>] [--url <url>] [--account <account>] [--password-stdin]", a => Task.FromResult(ServiceCommand.Run(["install", .. a]))),
            Forward("uninstall", "Remove the service registration; needs elevation", EffectClass.TargetWrites, "", a => Task.FromResult(ServiceCommand.Run(["uninstall", .. a]))),
            Forward("status", "Whether the service is installed and running", EffectClass.OfflineOnly, "", a => Task.FromResult(ServiceCommand.Run(["status", .. a]))),
            Forward("start", "Start the service; needs elevation", EffectClass.TargetWrites, "", a => Task.FromResult(ServiceCommand.Run(["start", .. a]))),
            Forward("stop", "Stop the service; needs elevation", EffectClass.TargetWrites, "", a => Task.FromResult(ServiceCommand.Run(["stop", .. a]))),
            Forward("restart", "Stop and start the service; needs elevation", EffectClass.TargetWrites, "", a => Task.FromResult(ServiceCommand.Run(["restart", .. a]))),
            Forward("doctor", "Check the registration, the account and the directory permissions", EffectClass.OfflineOnly, "", a => Task.FromResult(ServiceCommand.Run(["doctor", .. a]))),
        ]),
        new Noun("cert", "The server's TLS certificate",
        [
            Cert("use-pem", "Point Kestrel at a PEM certificate and key", EffectClass.RepoFilesOnly, "--cert <path> --key <path> [--repo <path>]"),
            Cert("use-pfx", "Point Kestrel at a PFX file", EffectClass.RepoFilesOnly, "--pfx <path> [--repo <path>]"),
            Cert("status", "The certificate in use", EffectClass.OfflineOnly, "[--account <account>]"),
            Cert("new-self-signed", "Create a self-signed certificate (a file anywhere, or in the Windows certificate store)", EffectClass.RepoFilesOnly, "[--file|--store] [--dns <names>] [--days <n>] [--repo <path>]"),
            Cert("list", "List certificates in the Windows certificate store (Windows only)", EffectClass.OfflineOnly, "[--location LocalMachine|CurrentUser]"),
            Cert("enroll", "Request a certificate from an AD CS CA (Windows only)", EffectClass.RepoFilesOnly, "--dns <names> [--ca <config>] [--template <name>] [--account <account>]"),
            Cert("renew", "Renew the certificate (Windows only)", EffectClass.RepoFilesOnly, "[--ca <config>] [--template <name>] [--account <account>] [--days <n>]"),
            Cert("retrieve", "Retrieve a pending request (Windows only)", EffectClass.RepoFilesOnly, "--request-id <id> [--account <account>]"),
            Cert("templates", "List the CA's templates (Windows only)", EffectClass.OfflineOnly, "[--ca <config>]"),
            Cert("discover", "Find the CA and template (Windows only)", EffectClass.RepoFilesOnly, "[--repo <path>]"),
            Cert("bind", "Bind a thumbprint to the listener (Windows only)", EffectClass.RepoFilesOnly, "--thumbprint <thumbprint> [--location LocalMachine|CurrentUser] [--allow-invalid|--no-allow-invalid]"),
        ]),
        new Noun("secret", "The encrypted secret store",
        [
            Forward("set", "Store a secret under a dbdata:sync:... name", EffectClass.RepoFilesOnly, "<ref> <value> [--repo <path>]", a => Task.FromResult(SecretCommand.Run(["set", .. a]))),
            Forward("list", "Whether each given reference is set, never the value", EffectClass.OfflineOnly, "[<ref> ...] [--repo <path>]", a => Task.FromResult(SecretCommand.Run(["list", .. a]))),
            Forward("remove", "Remove a stored secret", EffectClass.RepoFilesOnly, "<ref> [--repo <path>]", a => Task.FromResult(SecretCommand.Run(["remove", .. a]))),
        ]),
        new Noun("library", "ADO.NET libraries restored into the repository",
        [
            Forward("install", "Restore a package as a library", EffectClass.RepoFilesOnly, "<packageId> ... [--as <id>] --version <v> [--factory-type <type>] [--source <feed>]", a => LibraryCommand.RunAsync(["install", .. a])),
            Forward("sync", "Restore the libraries that are on record but not restored", EffectClass.RepoFilesOnly, "[<id>]", a => LibraryCommand.RunAsync(["sync", .. a])),
            Forward("list", "The installed libraries", EffectClass.OfflineOnly, "", a => LibraryCommand.RunAsync(["list", .. a])),
            Forward("uninstall", "Remove a library", EffectClass.RepoFilesOnly, "<id>", a => LibraryCommand.RunAsync(["uninstall", .. a])),
            Forward("validate", "Check that a library works against a connection (run by the API in a child process)", EffectClass.TargetReadOnly, "<id> --connection <name> [--repo <path>]", a => LibraryCommand.RunAsync(["validate", .. a])),
        ]),
        new Noun("driver", "Drivers added without a rebuild",
        [
            Forward("install", "Add a driver descriptor and its library", EffectClass.RepoFilesOnly, "<id> --library <name> --version <v> [--factory-type <type>] [--from mysql] [--display-name <name>]", a => DriverCommand.RunAsync(["install", .. a])),
            Forward("list", "The installed drivers", EffectClass.OfflineOnly, "", a => DriverCommand.RunAsync(["list", .. a])),
            Forward("uninstall", "Remove a driver", EffectClass.RepoFilesOnly, "<id>", a => DriverCommand.RunAsync(["uninstall", .. a])),
        ]),
        new Noun("config", "The server's own configuration",
        [
            Forward("check", "Readiness check: repository, state store, libraries and drivers, auth, binding, first admin", EffectClass.OfflineOnly, "[--repo <path>]", a => ConfigCommand.RunAsync(["check", .. a])),
            Forward("get", "One setting of dbdatasync.config.yaml", EffectClass.OfflineOnly, "<key> [--repo <path>]", a => ConfigCommand.RunAsync(["get", .. a])),
            Forward("set", "Change one setting of dbdatasync.config.yaml", EffectClass.RepoFilesOnly, "<key> <value> [--repo <path>]", a => ConfigCommand.RunAsync(["set", .. a])),
        ]),
        new Noun("health", "Is a running server answering",
        [
            new Verb("check", "Exit 0 when a running DbDataSync answers, 1 when it does not (what the container's health check runs)", EffectClass.OfflineOnly, "[--url <url>] [--repo <path>]",
                async (args, context) =>
                {
                    using var _ = ContextScope.Enter(context);
                    var exit = await HealthCommand.RunAsync(["check", .. args]);
                    context.Data["healthy"] = exit == 0;
                    return exit;
                },
                DataSchemas.Object([("healthy", DataSchemas.Boolean)], "healthy")),
        ]),
        UiNoun.Create(self, Program, version, NotForAgents),
    ], version);

    private static Verb Forward(string name, string summary, EffectClass effect, string usage, Func<string[], Task<int>> run) =>
        new(name, summary, effect, usage, async (args, context) =>
        {
            using var _ = ContextScope.Enter(context);
            return await run(args);
        }, DataSchemas.None);

    private static Verb Cert(string name, string summary, EffectClass effect, string usage) =>
        Forward(name, summary, effect, usage, a => Task.FromResult(CertCommand.Run([name, .. a])));
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
