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
    public static bool Handles(string[] args) => args.Length > 0 && args[0] is "server" or "secret" or "service" or "health";

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

    private static NounRouter Router(string[] invocation) => new("dbdatabuild",
    [
        ServerNoun.Create(RunServerAsync).With([.. PairingVerbs.Create(args => new PairingFiles(Path.Combine(DirectoryOf(args), "server.yaml"))), new Verb("project", "The projects this server hosts: add, remove, list, pull", EffectClass.RepoFilesOnly, ProjectUsage, ProjectAsync, DataSchemas.None)]),
        // the store is opened (and its key made on first use) only for the secret verbs
        .. invocation is ["secret", ..] ? new[] { SecretVerbs(invocation) } : Array.Empty<Noun>(),
        .. invocation is ["health", ..] ? new[] { HealthNoun.Create(new HttpHealthProbe(LoopbackTolerantClient()), HealthUrl) } : Array.Empty<Noun>(),
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

    private static string DirectoryOf(string[] args)
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
        var app = BuildServer.Build(settings, BuildServer.Certificate(settings.Directory), runner, projects, host.Commands, line => context.Error.WriteLine(line), parsed.Has("read-only") ? BuildManagementApi.ReadOnlySurface : null, scheduler, sessions);
        await context.Out.WriteLineAsync($"Serving {projects.All().Count} project(s) on {listen} (instance {BuildServer.InstanceId(settings.ServerYaml)}){(scheduler is null ? "" : ", running scheduled refresh plans")}. Pair a client with: dbdatabuild server pair");
        // the host's own lifetime: Ctrl+C, SIGTERM, systemd and the Windows service manager all stop it the same way
        await Microsoft.Extensions.Hosting.HostingAbstractionsHostExtensions.RunAsync(app, context.Cancellation);
        return CliExit.Ok;
    }

    private const string ProjectUsage = "add <id> (--path <folder> | --git <address> [--branch <name>]) | remove <id> | list | pull <id>   [--repo <server directory>]";

    /// <summary><c>server project add|remove|list|pull</c>: the projects this server hosts. A running server notices a change on its next call, so none of these needs it stopped.</summary>
    private static async Task<int> ProjectAsync(string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "repo", "path", "git", "branch" });
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
                    await context.Out.WriteLineAsync($"{p.Id,-20} {(p.IsRemote ? $"git {p.Git}" + (p.Branch is not null ? $" ({p.Branch})" : "") : $"path {p.Path}")}");
                context.Data["projects"] = projects.All().Select(p => new Dictionary<string, object?> { ["id"] = p.Id, ["path"] = p.Path, ["git"] = p.Git, ["branch"] = p.Branch }).ToList();
                return CliExit.Ok;

            case "add" when id is not null && parsed.Words.Count == 2:
                try
                {
                    var path = parsed.Get("path") is { } given ? Path.GetFullPath(given) : null;
                    var project = new HostedProject(id, path, parsed.Get("git"), parsed.Get("branch"));
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
