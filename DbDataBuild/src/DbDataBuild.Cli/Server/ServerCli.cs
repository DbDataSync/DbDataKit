using DbDataBuild.Core;
using DbDataBuild.Tui.Model;
using DbDataKit.Cli;
using DbDataKit.Config.Git;
using DbDataKit.Secrets;
using DbDataKit.Secrets.Cli;
using DbDataKit.Secrets.Store;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;

namespace DbDataBuild.Cli.Server;

/// <summary>
/// <c>dbdatabuild server run|pair|clients|unpair</c> (plan 13, R01 and T03): the Build server and the verbs that pair clients with it, in the grammar every DbData product shares. Handled before the command-line parser of the other commands,
/// because these are the shared nouns, not Build's own. <c>--repo</c> names the server's directory, as it does for the other products' pairing verbs.
/// </summary>
internal static class ServerCli
{
    public static bool Handles(string[] args) => args.Length > 0 && args[0] is "server" or "secret" or "service" or "health" or "lineage";

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, TextReader input)
    {
        // Ctrl+C and the service manager's stop both end `server run` cleanly.
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += onCancel;
        using var terminate = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, context => { context.Cancel = true; stop.Cancel(); });
        try { return await Router(args).RunAsync(args, new CliContext(output, error, input, stop.Token)); }
        finally { Console.CancelKeyPress -= onCancel; }
    }

    /// <summary>The JSON Schema of the documents the server's nouns write (for the tests that check them).</summary>
    internal static string OutputSchema(string[] invocation) => Router(invocation).OutputSchema();

    private static NounRouter Router(string[] invocation) => new("dbdatabuild",
    [
        ServerNoun.Create(RunServerAsync).With([.. PairingVerbs.Create(args => new PairingFiles(Path.Combine(DirectoryOf(args), "server.yaml"))), new Verb("project", "The projects this server hosts: add, remove, list, pull", EffectClass.RepoFilesOnly, ProjectUsage, ProjectAsync, DataSchemas.None), new Verb("proxy", "The proxies this server publishes data sources to: add, remove, list", EffectClass.RepoFilesOnly, ProxyUsage, ProxyAsync, DataSchemas.None)]),
        // the store is opened (and its key made on first use) only for the secret verbs
        .. invocation is ["secret", ..] ? new[] { SecretVerbs(invocation) } : Array.Empty<Noun>(),
        .. invocation is ["health", ..] ? new[] { HealthNoun.Create(new HttpHealthProbe(LoopbackTolerantClient()), HealthUrl) } : Array.Empty<Noun>(),
        .. invocation is ["lineage", ..] ? new[] { ServerLineage.Create(invocation) } : Array.Empty<Noun>(),
        .. invocation is ["service", ..] ? new[] { BuildService.Noun(BuildServiceEnvironment.Real()) } : Array.Empty<Noun>(),
    ], ProductInfo.Version);

    /// <summary>The shared <c>secret</c> verbs over the server's own store (<c>dbdatabuild secret ...</c>, with <c>--repo</c> as for the pairing verbs). The value of a secret comes from a hidden prompt or standard input, never an argument.</summary>
    private static Noun SecretVerbs(string[] invocation)
    {
        var store = ServerSecrets.Open(DirectoryOf(invocation));
        var cli = new SecretCli(new SecretResolverChain([store]), store, store, () => []);
        return new Noun("secret", "The server's secrets: the logins its commands use (dbdata:build:connection:<connection>:<read|write>)",
            SecretNoun.Create(cli, context => SecretNoun.Captured(context, new InputConsole(context.In))).Verbs
                .Select(v => v with { Run = (args, context) => v.Run(WithoutRepo(args), context) }).ToList());
    }

    /// <summary>Where <c>health check</c> asks by default: the server's own address, as a container sees it.</summary>
    public const string HealthUrl = "https://localhost:8470/health/live";

    /// <summary>
    /// The server's certificate is self-signed, so a probe of its own address cannot validate it; it is accepted for a loopback address only, where the answer is the server on this machine and nothing the probe
    /// reads is repeated (<see cref="HttpHealthProbe"/> reads a status code and no body). Any other address is validated as usual.
    /// </summary>
    private static HttpClient LoopbackTolerantClient() => new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (request, _, _, errors) => errors == System.Net.Security.SslPolicyErrors.None || request.RequestUri is { IsLoopback: true },
    });

    private static string[] WithoutRepo(string[] args)
    {
        var i = Array.IndexOf(args, "--repo");
        return i < 0 ? args : [.. args.Take(i), .. args.Skip(i + 2)];
    }

    internal static string DirectoryOf(string[] args)
    {
        // the last one wins, so an option given to the service command after the registered default overrides it
        var i = Array.LastIndexOf(args, "--repo");
        return Path.GetFullPath(i >= 0 && i + 1 < args.Length ? args[i + 1] : BuildServerSettings.DefaultDirectory());
    }

    private static async Task<int> RunServerAsync(string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string> { "scheduler", "read-only" }, new HashSet<string> { "repo", "listen" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0)
        {
            await context.Error.WriteLineAsync($"Unexpected: {string.Join(' ', parsed.Unknown.Concat(parsed.Words))}. Usage: server run [--repo <server directory>] [--listen https://<address>:<port>] [--scheduler] [--read-only]. Projects are added with 'server project add'.");
            return CliExit.Usage;
        }

        if (!Uri.TryCreate(parsed.Get("listen") ?? BuildServerSettings.DefaultListen, UriKind.Absolute, out var listen) || listen.Scheme != Uri.UriSchemeHttps)
        {
            await context.Error.WriteLineAsync("--listen is an https address, such as https://127.0.0.1:8470.");
            return CliExit.Usage;
        }

        if (parsed.Has("read-only") && parsed.Has("scheduler"))
        {
            await context.Error.WriteLineAsync("--read-only offers only the commands that change nothing, and --scheduler runs refresh plans that write data; choose one.");
            return CliExit.Usage;
        }

        var settings = new BuildServerSettings(DirectoryOf(args), listen);
        Directory.CreateDirectory(settings.Directory);
        var projects = new ProjectsFile(settings.Directory);
        foreach (var problem in projects.Problems) await context.Error.WriteLineAsync(problem);
        foreach (var project in projects.All().Where(p => p.IsRemote))
        {
            // a remote project is brought up to date as the server starts; one that cannot be reached is served as it was last seen, or not at all if it was never cloned
            try { projects.Pull(project); }
            catch (GitRemoteException ex) { await context.Error.WriteLineAsync($"Project '{project.Id}': {ex.Message}"); }
        }

        var host = new TuiCommand.CliHost(Environment.GetEnvironmentVariable);
        var secrets = ServerSecrets.Open(settings.Directory);
        var runner = new ChildProcessCommandRunner(logins: token => ServerSecrets.LoginsAsync(secrets, token));
        using var sessions = new WarmSessionRunner(logins: token => ServerSecrets.LoginsAsync(secrets, token));
        // running plans on their schedules is opt-in: a server that was only meant to answer reads never writes a target by itself
        BuildScheduler? scheduler = parsed.Has("scheduler")
            ? new BuildScheduler(projects, runner, BuildSchedulerSchema.Open(settings.Directory), TimeProvider.System, line => context.Error.WriteLine(line))
            : null;
        var publisher = new ProxyPublisher(new ProxiesFile(settings.Directory), new ConsoleKeyStore(secrets, secrets), secrets);
        var app = BuildServer.Build(settings, BuildServer.Certificate(settings.Directory), runner, projects, host.Commands, line => context.Error.WriteLine(line), parsed.Has("read-only") ? BuildManagementApi.ReadOnlySurface : null, scheduler, sessions, publisher);
        await context.Out.WriteLineAsync($"Serving {projects.All().Count} project(s) on {listen} (instance {BuildServer.InstanceId(settings.ServerYaml)}){(scheduler is null ? "" : ", running scheduled refresh plans")}. Pair a client with: dbdatabuild server pair");
        // the host's own lifetime: Ctrl+C, SIGTERM, systemd and the Windows service manager all stop it the same way
        await Microsoft.Extensions.Hosting.HostingAbstractionsHostExtensions.RunAsync(app, context.Cancellation);
        return CliExit.Ok;
    }

    private const string ProxyUsage = "add <name> <https://address:port> --code <pairing code> | remove <name> | list   [--repo <server directory>]";

    /// <summary>
    /// <c>server proxy add|remove|list</c> (plan 13, T10): the Proxies this server publishes data sources to. <c>add</c> pairs with the Proxy using the code its administrator made with the Proxy's <c>server pair --max-role publisher</c>, as a server client
    /// (not a console): the Proxy applies its owner rules to everything this server sends. The key it signs with is kept in this server's secret store.
    /// </summary>
    private static async Task<int> ProxyAsync(string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "repo", "code" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count == 0)
        {
            await context.Error.WriteLineAsync($"Usage: server proxy {ProxyUsage}");
            return CliExit.Usage;
        }

        var directory = DirectoryOf(args);
        var proxies = new ProxiesFile(directory);
        var (action, name) = (parsed.Words[0], parsed.Words.Count > 1 ? parsed.Words[1] : null);
        switch (action)
        {
            case "list" when parsed.Words.Count == 1:
                if (proxies.All().Count == 0) await context.Out.WriteLineAsync("No proxy is paired. Add one with 'server proxy add'.");
                foreach (var p in proxies.All()) await context.Out.WriteLineAsync($"{p.Name,-20} {p.Url}  (instance {p.InstanceId})");
                context.Data["proxies"] = proxies.All().Select(p => new Dictionary<string, object?> { ["name"] = p.Name, ["url"] = p.Url, ["instance_id"] = p.InstanceId }).ToList();
                return CliExit.Ok;

            case "add" when name is not null && parsed.Words.Count == 3 && parsed.Get("code") is { } code:
                if (!ProjectsFile.IsValidId(name) || !Uri.TryCreate(parsed.Words[2], UriKind.Absolute, out var url))
                {
                    await context.Error.WriteLineAsync("A proxy is called by lower-case letters, digits, '-' and '_', and its address is an https address such as https://proxy.example:8470.");
                    return CliExit.Usage;
                }
                try
                {
                    var store = ServerSecrets.Open(directory);
                    var (paired, ceiling) = await new ProxyPublisher(proxies, new ConsoleKeyStore(store, store), store).PairAsync(name, url, code, $"Build server {BuildServer.InstanceId(Path.Combine(directory, "server.yaml"))}", context.Cancellation);
                    await context.Out.WriteLineAsync($"Paired with the proxy '{name}' ({paired.InstanceId}); it lets this server publish as {string.Join(", ", ceiling.ToString().ToLowerInvariant())}.");
                    context.Data["added"] = name;
                    return CliExit.Ok;
                }
                catch (Exception ex) when (ex is PairingFailedException or IncompatibleServerException)
                {
                    await context.Error.WriteLineAsync(ex.Message);
                    return CliExit.Failure;
                }

            case "remove" when name is not null && parsed.Words.Count == 2:
                if (!proxies.Remove(name))
                {
                    await context.Error.WriteLineAsync($"No proxy '{name}' is paired.");
                    return CliExit.Failure;
                }
                await context.Out.WriteLineAsync($"'{name}' is forgotten here. End the pairing on the proxy with its own 'server unpair'.");
                context.Data["removed"] = name;
                return CliExit.Ok;

            default:
                await context.Error.WriteLineAsync($"Usage: server proxy {ProxyUsage}");
                return CliExit.Usage;
        }
    }

    private const string ProjectUsage = "add <id> (--path <folder> | --git <address> [--branch <name>] [--git-credential <name>]) | remove <id> | list | pull <id>   [--repo <server directory>]";

    /// <summary><c>server project add|remove|list|pull</c>: the projects this server hosts. A running server notices a change on its next call, so none of these needs it stopped.</summary>
    private static async Task<int> ProjectAsync(string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "repo", "path", "git", "branch", "git-credential" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count == 0)
        {
            await context.Error.WriteLineAsync($"Usage: server project {ProjectUsage}");
            return CliExit.Usage;
        }

        var projects = new ProjectsFile(DirectoryOf(args));
        var (action, id) = (parsed.Words[0], parsed.Words.Count > 1 ? parsed.Words[1] : null);
        switch (action)
        {
            case "list" when parsed.Words.Count == 1:
                foreach (var problem in projects.Problems) await context.Error.WriteLineAsync(problem);
                if (projects.All().Count == 0) await context.Out.WriteLineAsync("No project is hosted. Add one with 'server project add'.");
                foreach (var p in projects.All())
                    await context.Out.WriteLineAsync($"{p.Id,-20} {(p.IsRemote ? $"git {p.Git}" + (p.Branch is not null ? $" ({p.Branch})" : "") + (p.GitCredential is not null ? $", credential {p.GitCredential}" : "") : $"path {p.Path}")}");
                context.Data["projects"] = projects.All().Select(p => new Dictionary<string, object?> { ["id"] = p.Id, ["path"] = p.Path, ["git"] = p.Git, ["branch"] = p.Branch, ["git_credential"] = p.GitCredential }).ToList();
                return CliExit.Ok;

            case "add" when id is not null && parsed.Words.Count == 2:
                try
                {
                    var path = parsed.Get("path") is { } given ? Path.GetFullPath(given) : null;
                    var project = new HostedProject(id, path, parsed.Get("git"), parsed.Get("branch"), parsed.Get("git-credential") is { } named ? ProjectsFile.ReferenceFor(named) : null);
                    if (project.Branch is not null && !project.IsRemote) throw new ArgumentException("--branch goes with --git.");
                    projects.Add(project);
                    if (project.IsRemote)
                    {
                        // cloned now, so a bad address or branch is the answer to this command and not to the first call
                        try
                        {
                            var pulled = projects.Pull(project)!;
                            await context.Out.WriteLineAsync($"Cloned '{id}' at {pulled.Head[..Math.Min(8, pulled.Head.Length)]} ({pulled.Branch}).");
                        }
                        catch (GitRemoteException ex)
                        {
                            projects.Remove(id);
                            await context.Error.WriteLineAsync(ex.Message);
                            return CliExit.Failure;
                        }
                    }
                    else await context.Out.WriteLineAsync($"Hosting '{id}' from {path}.");
                    context.Data["added"] = id;
                    return CliExit.Ok;
                }
                catch (ArgumentException ex)
                {
                    await context.Error.WriteLineAsync(ex.Message);
                    return CliExit.Usage;
                }

            case "remove" when id is not null && parsed.Words.Count == 2:
                if (!projects.Remove(id))
                {
                    await context.Error.WriteLineAsync($"No project '{id}' is hosted.");
                    return CliExit.Failure;
                }
                await context.Out.WriteLineAsync($"'{id}' is no longer served. Its files are untouched.");
                context.Data["removed"] = id;
                return CliExit.Ok;

            case "pull" when id is not null && parsed.Words.Count == 2:
                if (projects.Find(id) is not { } found)
                {
                    await context.Error.WriteLineAsync($"No project '{id}' is hosted.");
                    return CliExit.Failure;
                }
                if (!found.IsRemote)
                {
                    await context.Out.WriteLineAsync($"'{id}' is a folder on this machine; there is nothing to pull.");
                    return CliExit.Ok;
                }
                try
                {
                    var pulled = projects.Pull(found)!;
                    await context.Out.WriteLineAsync($"'{id}' is at {pulled.Head[..Math.Min(8, pulled.Head.Length)]} ({pulled.Branch}).");
                    return CliExit.Ok;
                }
                catch (GitRemoteException ex)
                {
                    await context.Error.WriteLineAsync(ex.Message);
                    return CliExit.Failure;
                }

            default:
                await context.Error.WriteLineAsync($"Usage: server project {ProjectUsage}");
                return CliExit.Usage;
        }
    }

    /// <summary>Where a secret's value is read from: the context's standard input when it is redirected (or not the process's own), a hidden prompt on the terminal otherwise.</summary>
    private sealed class InputConsole(TextReader input) : IConsole
    {
        private bool OwnTerminal => ReferenceEquals(input, Console.In) && !Console.IsInputRedirected;

        public void WriteLine(string text) => Console.Out.WriteLine(text);
        public void WriteError(string text) => Console.Error.WriteLine(text);
        public bool IsInputRedirected => !OwnTerminal;
        public string ReadStdin() => input.ReadToEnd();
        public string? ReadSecret(string prompt) => OwnTerminal ? new SystemConsole().ReadSecret(prompt) : null;
    }
}
