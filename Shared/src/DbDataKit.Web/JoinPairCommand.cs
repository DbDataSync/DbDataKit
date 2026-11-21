using System.Diagnostics;
using DbDataKit.Cli;

namespace DbDataKit.Web.Trust;

/// <summary>What restarting a server's registered service did: <see cref="Registered"/> is false when no service is registered for it on this machine; <see cref="Problem"/> says why a restart failed.</summary>
public sealed record ServiceRestartResult(bool Registered, string? Problem = null);

/// <summary>What a product tells the <c>pair</c> command about itself.</summary>
/// <param name="Program">The program as typed (<c>dbdatarunner</c>).</param>
/// <param name="DataFolder">The server's data folder for these arguments (<c>--repo</c>, else the program's default).</param>
/// <param name="Restart">Restarts the server's registered service, or says none is registered.</param>
public sealed record PairCommandOptions(string Program, Func<CliArgs, string> DataFolder, Func<CancellationToken, Task<ServiceRestartResult>> Restart)
{
    /// <summary>How long the command waits for the restarted service to join before it says where to look.</summary>
    public TimeSpan JoinWait { get; init; } = TimeSpan.FromSeconds(90);
}

/// <summary>
/// <c>&lt;program&gt; pair --host &lt;console&gt; --token &lt;token&gt; --console-thumbprint sha256:&lt;hex&gt; [--address &lt;https address&gt;] [--repo &lt;data folder&gt;]</c>,
/// the command the console prints for a server to join it. It writes <c>join-request.yaml</c> into the server's data folder and restarts the server's
/// service, which joins at its start as its own account (<see cref="ServerJoin"/>). With no service registered it says how to start the server.
/// <c>--token -</c> reads the token from standard input.
/// Design: docs/install.md#pairing
/// </summary>
public static class JoinPairCommand
{
    public const string Name = "pair";

    public const string Usage = "--host <console address> --token <token|-> --console-thumbprint sha256:<hex> [--address <https address>] [--repo <data folder>]";

    public static Noun Create(PairCommandOptions options) => Noun.Command(Name, new Verb(Name,
        "Join the console that printed this command: writes the join request into the data folder and restarts the service, which joins at its start",
        EffectClass.RepoFilesOnly, Usage, (args, context) => RunAsync(args, context, options),
        DataSchemas.Object([("request", DataSchemas.String), ("console", DataSchemas.String), ("restarted", DataSchemas.Boolean), ("joined", DataSchemas.Boolean)], "request", "console", "restarted", "joined")));

    private static readonly IReadOnlySet<string> Valued = new HashSet<string> { "host", "token", "console-thumbprint", "address", "repo" };

    public static async Task<int> RunAsync(string[] args, CliContext context, PairCommandOptions options)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), Valued);
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0 || parsed.Get("host") is not { } host || parsed.Get("token") is not { } token || parsed.Get("console-thumbprint") is not { } thumbprint)
        {
            await context.Error.WriteLineAsync($"Usage: {options.Program} {Name} {Usage}");
            await context.Error.WriteLineAsync("Run the command the console printed (dbdatakit setup invite, or the console's Add a server screen) as it is.");
            return CliExit.Usage;
        }
        if (token == "-") token = (await context.In.ReadLineAsync(context.Cancellation))?.Trim() ?? "";

        JoinRequestFile request;
        try { request = JoinRequestFile.From(host, thumbprint, token, parsed.Get("address")); }
        catch (ArgumentException ex)
        {
            await context.Error.WriteLineAsync(ex.Message.Split(" (Parameter")[0]);
            return CliExit.Usage;
        }

        var folder = options.DataFolder(parsed);
        var path = ServerJoin.RequestPath(folder);
        try
        {
            request.Write(path);
            GiveToFolderOwner(path, folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await context.Error.WriteLineAsync($"The join request '{path}' cannot be written: {ex.Message} Run the command as an administrator of this machine, or as the account the server runs as.");
            return CliExit.Failure;
        }
        var console = JoinRequestFile.Text(request.Console);
        context.Data["request"] = path;
        context.Data["console"] = console;
        context.Data["restarted"] = false;
        context.Data["joined"] = false;
        await context.Out.WriteLineAsync($"Wrote the join request {path}; the server joins {console} at its next start.");

        var restart = await options.Restart(context.Cancellation);
        if (!restart.Registered)
        {
            var repo = parsed.Get("repo") is { } given ? $" --repo {given}" : "";
            await context.Out.WriteLineAsync($"No {options.Program} service is registered on this machine. Start the server to join: {options.Program} server run{repo}");
            return CliExit.Ok;
        }
        if (restart.Problem is { } problem)
        {
            await context.Error.WriteLineAsync($"The service could not be restarted: {problem} Restart it yourself; it joins at its start.");
            return CliExit.Failure;
        }
        context.Data["restarted"] = true;
        await context.Out.WriteLineAsync("Restarted the service; waiting for it to join ...");

        // the service deletes the request once it joined; a refused join leaves it, and the start fails naming the console and its answer
        var until = DateTime.UtcNow + options.JoinWait;
        while (DateTime.UtcNow < until && File.Exists(path)) await Task.Delay(500, context.Cancellation);
        if (File.Exists(path))
        {
            await context.Error.WriteLineAsync($"The service has not joined yet. `{options.Program} service check` prints the reason of a failed start.");
            return CliExit.Failure;
        }
        context.Data["joined"] = true;
        await context.Out.WriteLineAsync($"Joined {console}.");
        return CliExit.Ok;
    }

    // On Linux the command runs as root or as the server's user; the service reads the request as its own user, so a request root wrote is given the
    // owner of the data folder (it is readable by its owner only)
    private static void GiveToFolderOwner(string path, string folder)
    {
        if (OperatingSystem.IsWindows() || Environment.UserName != "root" || !Directory.Exists(folder)) return;
        try
        {
            using var chown = Process.Start(new ProcessStartInfo("chown", ["--reference=" + folder, path]) { UseShellExecute = false, RedirectStandardError = true });
            chown?.WaitForExit(10_000);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { /* the request stays root's; the join step then names the file */ }
    }
}
