using DbDataBuild.Core;
using DbDataBuild.Tui.Model;
using DbDataKit.Cli;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;

namespace DbDataBuild.Cli.Server;

/// <summary>
/// <c>dbdatabuild server run|pair|clients|unpair</c> (plan 13, R01 and T03): the Build server and the verbs that pair clients with it, in the grammar every DbData product shares. Handled before the command-line parser of the other commands,
/// because these are the shared nouns, not Build's own. <c>--repo</c> names the server's directory, as it does for the other products' pairing verbs.
/// </summary>
internal static class ServerCli
{
    public static bool Handles(string[] args) => args.Length > 0 && args[0] == "server";

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, TextReader input)
    {
        // Ctrl+C and the service manager's stop both end `server run` cleanly.
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += onCancel;
        using var terminate = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, context => { context.Cancel = true; stop.Cancel(); });
        try { return await Router().RunAsync(args, new CliContext(output, error, input, stop.Token)); }
        finally { Console.CancelKeyPress -= onCancel; }
    }

    private static NounRouter Router() => new("dbdatabuild",
    [
        ServerNoun.Create(RunServerAsync).With(PairingVerbs.Create(args => new PairingFiles(Path.Combine(DirectoryOf(args), "server.yaml")))),
    ], ProductInfo.Version);

    private static string DirectoryOf(string[] args)
    {
        var i = Array.IndexOf(args, "--repo");
        return Path.GetFullPath(i >= 0 && i + 1 < args.Length ? args[i + 1] : BuildServerSettings.DefaultDirectory());
    }

    private static async Task<int> RunServerAsync(string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "project", "repo", "listen" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0)
        {
            await context.Error.WriteLineAsync($"Unexpected: {string.Join(' ', parsed.Unknown.Concat(parsed.Words))}. Usage: server run [--project <folder>] [--repo <server directory>] [--listen https://<address>:<port>]");
            return CliExit.Usage;
        }

        var project = Path.GetFullPath(parsed.Get("project") ?? ".");
        if (!Directory.Exists(project))
        {
            await context.Error.WriteLineAsync($"The project folder '{project}' does not exist.");
            return CliExit.Usage;
        }

        if (!Uri.TryCreate(parsed.Get("listen") ?? BuildServerSettings.DefaultListen, UriKind.Absolute, out var listen) || listen.Scheme != Uri.UriSchemeHttps)
        {
            await context.Error.WriteLineAsync("--listen is an https address, such as https://127.0.0.1:8470.");
            return CliExit.Usage;
        }

        var settings = new BuildServerSettings(project, DirectoryOf(args), listen);
        Directory.CreateDirectory(settings.Directory);
        var host = new TuiCommand.CliHost(Environment.GetEnvironmentVariable);
        var app = BuildServer.Build(settings, BuildServer.Certificate(settings.Directory), new ChildProcessCommandRunner(), BuildManagementApi.ReadOnlySurface(project, host.Commands), line => context.Error.WriteLine(line));
        await context.Out.WriteLineAsync($"Serving '{settings.ProjectId}' on {listen} (instance {BuildServer.InstanceId(settings.ServerYaml)}). Pair a client with: dbdatabuild server pair");
        await app.StartAsync(context.Cancellation);
        try { await Task.Delay(Timeout.Infinite, context.Cancellation); }
        catch (OperationCanceledException) { /* asked to stop */ }
        await app.StopAsync(CancellationToken.None);
        return CliExit.Ok;
    }
}
