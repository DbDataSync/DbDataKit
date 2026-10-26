using System.Diagnostics;
using System.Text;
using DbDataBuild.Tui.Model;

namespace DbDataBuild.Cli.Server;

/// <summary>Runs one command of a project and returns what it printed. The server runs every command this way, in a process of its own (plan 13, D12), so a command that crashes, hangs or leaves the native library in a bad state takes nothing else with it.</summary>
internal interface IProjectCommandRunner
{
    Task<CommandResult> RunAsync(IReadOnlyList<string> argv, string projectRoot, CancellationToken cancellationToken);

    /// <summary>Runs the command for a person (<paramref name="actor"/>, see <see cref="Actor"/>), who is then the one the tracking tables name. A runner that has no use for the name ignores it.</summary>
    Task<CommandResult> RunAsync(IReadOnlyList<string> argv, string projectRoot, string? actor, CancellationToken cancellationToken) => RunAsync(argv, projectRoot, cancellationToken);
}

/// <summary>
/// Starts this same executable as a child with the command's arguments and <c>--format json</c>, and reads its standard output and error. When the running program is not <c>dbdatabuild</c> itself (a test host, <c>dotnet</c> running the assembly) it runs the assembly with <c>dotnet exec</c> instead, like Sync's workers. A command that runs longer than <c>timeout</c> is killed with its children and reports exit 124.
/// <para>
/// The child gets the server's environment plus the logins <paramref name="logins"/> supplies (the server's stored secrets as the <c>DBDATA_BUILD_&lt;CONNECTION&gt;_&lt;READ|WRITE&gt;</c> variables, see <see cref="ServerSecrets"/>), read afresh for every command
/// so a secret set while the server runs applies to the next command. A login from the store replaces a variable of the same name the server was started with.
/// </para>
/// <para>
/// A command run for a person gets <see cref="Actor.Variable"/> with their name and the console's, so the tracking tables name them; without one the variable is removed. The child's input is closed, so nothing waits for an answer.
/// </para>
/// </summary>
internal sealed class ChildProcessCommandRunner(TimeSpan? timeout = null, int maxParallel = 4, Func<CancellationToken, Task<IReadOnlyDictionary<string, string>>>? logins = null) : IProjectCommandRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(30);

    /// <summary>Exit code of a command the server stopped for running too long (the one <c>timeout(1)</c> uses).</summary>
    public const int TimedOut = 124;

    private readonly SemaphoreSlim _slots = new(maxParallel, maxParallel);

    public Task<CommandResult> RunAsync(IReadOnlyList<string> argv, string projectRoot, CancellationToken cancellationToken) => RunAsync(argv, projectRoot, null, cancellationToken);

    public async Task<CommandResult> RunAsync(IReadOnlyList<string> argv, string projectRoot, string? actor, CancellationToken cancellationToken)
    {
        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (file, prefix) = Executable();
            var start = new ProcessStartInfo(file)
            {
                WorkingDirectory = projectRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true, // a command that would ask a question reads the end of its input, never the server's
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            if (logins is not null)
                foreach (var (name, value) in await logins(cancellationToken).ConfigureAwait(false))
                    start.Environment[name] = value;
            if (actor is { Length: > 0 }) start.Environment[Actor.Variable] = actor;
            else start.Environment.Remove(Actor.Variable);
            foreach (var a in prefix) start.ArgumentList.Add(a);
            foreach (var a in argv) start.ArgumentList.Add(a);
            start.ArgumentList.Add("--format");
            start.ArgumentList.Add("json");

            using var process = Process.Start(start) ?? throw new InvalidOperationException("The command process did not start.");
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout ?? DefaultTimeout);
            try
            {
                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* it ended as we looked */ }
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return new CommandResult(TimedOut, "", "The command ran longer than the server allows and was stopped.");
            }

            return new CommandResult(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
        finally
        {
            _slots.Release();
        }
    }

    /// <summary>This executable when it is <c>dbdatabuild</c> itself; otherwise the program is a host running the assembly (<c>dotnet</c>, or a test host such as <c>testhost.exe</c>), so the assembly is run with <c>dotnet exec</c>.</summary>
    internal static (string File, string[] Prefix) Executable()
    {
        var running = Environment.ProcessPath ?? throw new InvalidOperationException("The path of the running program is not known.");
        if (string.Equals(Path.GetFileNameWithoutExtension(running), "dbdatabuild", StringComparison.OrdinalIgnoreCase)) return (running, []);
        var dotnet = string.Equals(Path.GetFileNameWithoutExtension(running), "dotnet", StringComparison.OrdinalIgnoreCase)
            ? running
            : Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";
        return (dotnet, ["exec", Path.Combine(AppContext.BaseDirectory, "dbdatabuild.dll")]);
    }
}
