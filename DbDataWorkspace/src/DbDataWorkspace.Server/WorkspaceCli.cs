using System.Net.Http;
using DbDataKit.Cli;
using DbDataKit.Hosting.Cli;
using DbDataKit.Hosting.Startup;
using DbDataKit.Web.Trust;

namespace DbDataWorkspace.Server;

/// <summary>
/// The command line, in the grammar every DbData product shares (plan 13, C02): <c>dbdataworkspace server run|pair|clients|unpair</c>, <c>project add|create|list</c>, <c>workspace list</c>, <c>state</c>, <c>secret</c>, <c>service</c> and <c>health check</c>. <c>--repo</c> names the server's directory
/// (<see cref="WorkspaceSettings.DirectoryVariable"/> by default), as it does for the other products' pairing verbs.
/// </summary>
public static class WorkspaceCli
{
    public const string Program = "dbdataworkspace";

    /// <summary>Where <c>health check</c> asks by default: the server's own address, as a container sees it.</summary>
    public const string HealthUrl = "https://localhost:7860/health/live";

    public static Task<int> RunAsync(string[] args) => RunAsync(args, CliContext.Console());

    public static async Task<int> RunAsync(string[] args, CliContext context)
    {
        // Ctrl+C and the service manager's stop both end `server run` cleanly.
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += onCancel;
        using var terminate = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, c => { c.Cancel = true; stop.Cancel(); });
        try { return await Removed.TryAnswerAsync(args, context.Error) ?? await Router(invocation: args).RunAsync(args, context with { Cancellation = context.Cancellation.CanBeCanceled ? context.Cancellation : stop.Token }); }
        finally { Console.CancelKeyPress -= onCancel; }
    }

    /// <summary>
    /// The commands that are gone, each answering the sentence that names what replaced it (exit code 2): the server's publisher keys went with its
    /// publishing, since the console deploys a build and signs it with its own key.
    /// </summary>
    public static RemovedCommands Removed { get; } = new(Program, new Dictionary<string, string>
    {
        ["publisher"] = "this server publishes nothing and holds no publisher key; the console deploys a build from the Workspace and signs each version with its own publisher key, which every Runner and Gateway group it manages trusts.",
    });

    public static NounRouter Router(Func<string, string?>? environment = null, string[]? invocation = null) => new(Program,
    [
        ServerNoun.Create((args, context) => RunServerAsync(args, context, environment))
            .With([.. PairingVerbs.Create(args => WorkspaceState.Pairing(DirectoryOf(args, environment), environment))]),
        JoinPairCommand.Create(new PairCommandOptions(Program, args => DirectoryOf([.. args.Get("repo") is { } repo ? new[] { "--repo", repo } : []], environment),
            async _ =>
            {
                var (registered, problem) = await ServiceRestarter.RestartAsync(WorkspaceService.RegistrationName, SuiteServices.Workspace.Program);
                return new ServiceRestartResult(registered, problem);
            })),
        WorkspaceAdminCommand.Project(environment),
        WorkspaceAdminCommand.Workspace(environment),
        StateNoun(environment),
        // the store is opened (and its key made on first use) only for the secret verbs
        .. invocation is ["secret", ..] ? new[] { SecretVerbs(invocation) } : Array.Empty<Noun>(),
        WorkspaceService.Noun(WorkspaceServiceEnvironment.Real()),
        HealthNoun.Create(new HttpHealthProbe(LoopbackTolerantClient()), HealthUrl),
    ], WorkspaceServer.Version, () => DbDataKit.State.SuiteEnvironment.OldNameProblem(environment));

    /// <summary>
    /// The shared <c>secret</c> verbs over the server's own store (<c>dbdataworkspace secret set dbdatakit:server:state:&lt;name&gt; --stdin</c>, with <c>--repo</c> as for the pairing verbs), so a state connection a console names by secret reference can be resolved here (plan 29b, C06). The value of a
    /// secret comes from a hidden prompt or standard input, never an argument.
    /// </summary>
    private static Noun SecretVerbs(string[] invocation)
    {
        var i = Array.LastIndexOf(invocation, "--repo");
        var directory = Path.GetFullPath(i >= 0 && i + 1 < invocation.Length ? invocation[i + 1] : WorkspaceSettings.DefaultDirectory(null));
        var store = WorkspaceSecrets.Open(directory);
        var cli = new DbDataKit.Secrets.Cli.SecretCli(new DbDataKit.Secrets.SecretResolverChain([store]), store, store, () => []);
        return new Noun("secret", "The server's secrets: the logins its state connection refers to (dbdatakit:server:state:<name>)",
            DbDataKit.Secrets.Cli.SecretNoun.Create(cli, context => DbDataKit.Secrets.Cli.SecretNoun.Captured(context, new InputConsole(context.In))).Verbs
                .Select(v => v with { Run = (args, context) => v.Run(WithoutRepo(args), context) }).ToList());
    }

    private static string[] WithoutRepo(string[] args)
    {
        var i = Array.IndexOf(args, "--repo");
        return i < 0 ? args : [.. args.Take(i), .. args.Skip(i + 2)];
    }

    /// <summary>Where a secret's value is read from: the context's standard input when it is redirected (or not the process's own), a hidden prompt on the terminal otherwise.</summary>
    private sealed class InputConsole(TextReader input) : DbDataKit.Secrets.Cli.IConsole
    {
        private bool OwnTerminal => ReferenceEquals(input, Console.In) && !Console.IsInputRedirected;

        public void WriteLine(string text) => Console.Out.WriteLine(text);
        public void WriteError(string text) => Console.Error.WriteLine(text);
        public bool IsInputRedirected => !OwnTerminal;
        public string ReadStdin() => input.ReadToEnd();
        public string? ReadSecret(string prompt) => OwnTerminal ? new DbDataKit.Secrets.Cli.SystemConsole().ReadSecret(prompt) : null;
    }

    /// <summary>The shared <c>state show|test|grants|init</c> verbs over the server's state: its settings come from <c>--engine</c>, the environment (<see cref="WorkspaceState.EngineVariable"/>) and the <c>state:</c> section of <c>server.yaml</c> in its directory, else SQLite in <c>state.db</c>; built from <see cref="WorkspaceState.Modules"/>.</summary>
    private static Noun StateNoun(Func<string, string?>? environment) => DbDataKit.State.Cli.StateVerbs.Create(new DbDataKit.State.Cli.StateVerbContext(
        Program, "the server",
        args =>
        {
            var directory = Path.GetFullPath(args.Get("repo") ?? WorkspaceSettings.DefaultDirectory(environment));
            return DbDataKit.State.Cli.StateTarget.From(WorkspaceState.Resolve(directory, environment, args.Get("engine"), engineFlagName: "--engine"), Path.Combine(directory, WorkspaceState.FileName), () => WorkspaceState.Secrets(directory, environment), WorkspaceState.SettingsFile(directory));
        },
        WorkspaceState.Modules, WorkspaceState.Scope, ["engine", "repo"], "[--engine mssql|postgres] [--repo <server directory>]"));

    /// <summary>The server's certificate is self-signed, so a probe of its own address cannot validate it; it is accepted for a loopback address only, where the answer is this machine and the probe reads a status code and no body.</summary>
    private static HttpClient LoopbackTolerantClient() => new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (request, _, _, errors) => errors == System.Net.Security.SslPolicyErrors.None || request.RequestUri is { IsLoopback: true },
    });

    internal static string DirectoryOf(string[] args, Func<string, string?>? environment)
    {
        // the last one wins, so an option given to the service command after the registered default overrides it
        var i = Array.LastIndexOf(args, "--repo");
        return Path.GetFullPath(i >= 0 && i + 1 < args.Length ? args[i + 1] : WorkspaceSettings.DefaultDirectory(environment));
    }

    private static async Task<int> RunServerAsync(string[] args, CliContext context, Func<string, string?>? environment)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string> { FolderPermissions.FlagName }, new HashSet<string> { "repo", "listen", "state-engine", "state-connection" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0)
        {
            await context.Error.WriteLineAsync($"Unexpected: {string.Join(' ', parsed.Unknown.Concat(parsed.Words))}. Usage: server run [--repo <server directory>] [--listen https://<address>:<port>] [--state-engine sqlite|mssql|postgres --state-connection <connection string>] [--skip-folder-permissions-check].");
            return CliExit.Usage;
        }
        IReadOnlyList<Uri>? listenFlag = null;
        if (parsed.Get("listen") is { } listenText)
        {
            try { listenFlag = ListenPorts.ParseFlag(listenText); }
            catch (FormatException e)
            {
                await context.Error.WriteLineAsync($"--listen is an https address or a list of them, such as https://127.0.0.1:7860. {e.Message}");
                return CliExit.Usage;
            }
        }

        // every step of the start is reported the same way in every server: the step that failed and why, in the startup-failure file, and a non-zero exit
        var directory = DirectoryOf(args, environment);
        var service = SuiteServices.Workspace;
        WorkspaceSettings? settings = null;
        System.Security.Cryptography.X509Certificates.X509Certificate2? certificate = null;
        Microsoft.AspNetCore.Builder.WebApplication? app = null;
        ListenChoice? listen = null;
        var settingsExit = (int?)null;
        var result = await new StartupSequence(new StartupOptions
            {
                Service = service,
                DataFolder = directory,
                LogsRoot = SuiteServices.LogsRootFor(service, directory),
                Version = WorkspaceServer.Version,
                Warning = line => context.Error.WriteLine(line),
            })
            .Step(StartupStep.Folder, step =>
            {
                DbDataKit.State.StateSettings state;
                DbDataKit.State.StateSources sources;
                var settingsFile = Path.Combine(directory, "server.yaml");
                SkipFolderPermissions skip;
                try
                {
                    skip = SkipFolderPermissions.Read(parsed.Has(FolderPermissions.FlagName), settingsFile);
                    (state, sources) = WorkspaceState.Resolve(directory, environment, parsed.Get("state-engine"), parsed.Get("state-connection"));
                    // --listen, else the port pinned in server.yaml, else a free port of the Workspace's sub-block (pinned once the server listens on it)
                    listen = ListenPorts.ResolveAll(service, listenFlag, Path.Combine(directory, "server.yaml"), new Uri(WorkspaceSettings.DefaultListen), step.Options.ExcludedPorts());
                }
                catch (InvalidOperationException)
                {
                    settingsExit = DbDataKit.State.Cli.StateExit.Settings;
                    throw;
                }
                settings = new WorkspaceSettings(directory, listen.Address) { ListenAll = listen.Addresses, State = state, StateSources = sources, SkipFolderPermissionsCheck = skip.IsOn };
                step.CheckFolderPermissions(settings.Directory, skip, settingsFile);
                Directory.CreateDirectory(settings.Directory);
            })
            .Step(StartupStep.Certificate, _ => certificate = WorkspaceServer.Certificate(settings!.Directory))
            // building the server opens its state and runs the state's migrations
            .Step(StartupStep.State, _ => app = WorkspaceServer.Build(settings!, certificate!, line => context.Error.WriteLine(line)))
            .Step(StartupStep.Ports, (step, ct) => step.BindAsync(listen!, app!.StartAsync, cancellationToken: ct))
            .Step(StartupStep.Join, (_, ct) => WorkspaceServer.JoinAsync(app!, settings!, ct))
            .RunAsync(context.Cancellation);
        if (!result.Succeeded)
        {
            await context.Error.WriteLineAsync(result.Summary());
            if (app is not null) await app.DisposeAsync();
            return settingsExit ?? result.ExitCode;
        }

        try
        {
            await context.Out.WriteLineAsync($"Serving on {listen!.Address} (instance {WorkspaceServer.InstanceId(settings!.Directory, settings.State)}). Pair a client with: dbdataworkspace server pair --max-role <roles> [--repo <server directory>]");
            // the host's own lifetime: Ctrl+C, SIGTERM, systemd and the Windows service manager all stop it the same way
            await Microsoft.Extensions.Hosting.HostingAbstractionsHostExtensions.WaitForShutdownAsync(app!, context.Cancellation);
        }
        finally
        {
            await app!.DisposeAsync();
        }
        return CliExit.Ok;
    }
}
