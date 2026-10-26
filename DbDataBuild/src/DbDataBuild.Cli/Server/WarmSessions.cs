using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataBuild.Tui.Model;

namespace DbDataBuild.Cli.Server;

/// <summary>
/// The child side of a warm session (plan 13, W03): <c>dbdatabuild --session</c> reads one JSON request per line from standard input (<c>argv</c>, the working directory <c>cwd</c>, and <c>env</c>, where a null value removes the variable) and answers
/// one JSON line with <c>exit</c>, <c>out</c> and <c>err</c>. Commands run in this process one after another, so the parser, the native library and DuckDB are loaded once and the next compile starts warm. Nothing else may write to standard output,
/// so the protocol keeps the real stream and the console's output is thrown away.
/// </summary>
internal static class SessionHost
{
    public const string Argument = "--session";

    public static async Task<int> RunAsync(TextReader input, TextWriter protocol)
    {
        Console.SetOut(TextWriter.Null);
        while (await input.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0) continue;
            var request = JsonNode.Parse(line)!.AsObject();
            var argv = request["argv"]!.AsArray().Select(a => a!.GetValue<string>()).ToArray();
            var cwd = request["cwd"]!.GetValue<string>();
            var changed = new Dictionary<string, string?>();
            foreach (var (name, value) in request["env"]?.AsObject() ?? [])
            {
                changed[name] = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, value?.GetValue<string>());
            }
            var output = new StringWriter { NewLine = "\n" };
            var error = new StringWriter { NewLine = "\n" };
            int exit;
            try
            {
                Environment.CurrentDirectory = cwd;
                exit = DbDataBuild.Cli.CliApp.Run(argv, output, error, TextReader.Null);
            }
            catch (Exception ex)
            {
                exit = 70;
                error.WriteLine($"The command stopped with {ex.GetType().Name}.");
            }
            finally
            {
                foreach (var (name, was) in changed) Environment.SetEnvironmentVariable(name, was);
            }
            await protocol.WriteLineAsync(new JsonObject { ["exit"] = exit, ["out"] = output.ToString(), ["err"] = error.ToString() }.ToJsonString()).ConfigureAwait(false);
            await protocol.FlushAsync().ConfigureAwait(false);
        }
        return 0;
    }
}

/// <summary>
/// Runs a project's commands in one long-lived child per project root, which for the server is a workspace (plan 13, W03, D12): the feedback of an authoring session does not pay for starting the program and loading the parser each time.
/// It is still a child process, so a native crash, a hang or a timeout loses only that session: the process is killed, the call answers as <see cref="ChildProcessCommandRunner"/> would, and the next call starts a fresh one. A root runs one command
/// at a time. A session idle for <c>idle</c> is ended when the next call arrives, so a forgotten workspace costs nothing but a start.
/// </summary>
internal sealed class WarmSessionRunner(TimeSpan? timeout = null, TimeSpan? idle = null, Func<CancellationToken, Task<IReadOnlyDictionary<string, string>>>? logins = null) : IProjectCommandRunner, IDisposable
{
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly TimeSpan _idle = idle ?? TimeSpan.FromMinutes(10);

    /// <summary>The process id of the live session for a root, or null: how a test sees that a call was warm.</summary>
    public int? SessionOf(string projectRoot)
    {
        lock (_gate) return _sessions.TryGetValue(Path.GetFullPath(projectRoot), out var s) && s.Alive ? s.Pid : null;
    }

    public Task<CommandResult> RunAsync(IReadOnlyList<string> argv, string projectRoot, CancellationToken cancellationToken) => RunAsync(argv, projectRoot, null, cancellationToken);

    public async Task<CommandResult> RunAsync(IReadOnlyList<string> argv, string projectRoot, string? actor, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(projectRoot);
        var environment = new JsonObject();
        if (logins is not null)
            foreach (var (name, value) in await logins(cancellationToken).ConfigureAwait(false)) environment[name] = value;
        environment[Actor.Variable] = actor is { Length: > 0 } ? actor : null;
        var request = new JsonObject
        {
            ["argv"] = new JsonArray([.. argv.Concat(["--format", "json"]).Select(a => (JsonNode?)a)]),
            ["cwd"] = root,
            ["env"] = environment,
        }.ToJsonString();

        Session session;
        lock (_gate)
        {
            foreach (var stale in _sessions.Where(p => !p.Value.Alive || p.Value.IdleSince(DateTime.UtcNow) > _idle).ToList())
            {
                stale.Value.Kill();
                _sessions.Remove(stale.Key);
            }
            if (!_sessions.TryGetValue(root, out session!)) _sessions[root] = session = Session.Start(root);
        }

        await session.Turn.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout ?? ChildProcessCommandRunner.DefaultTimeout);
            try
            {
                await session.Input.WriteLineAsync(request.AsMemory(), limit.Token).ConfigureAwait(false);
                await session.Input.FlushAsync(limit.Token).ConfigureAwait(false);
                var line = await session.Output.ReadLineAsync(limit.Token).ConfigureAwait(false);
                if (line is null) return Lost(session, root, "The session ended while the command ran.");
                var answer = JsonNode.Parse(line)!.AsObject();
                session.Touch();
                return new CommandResult(answer["exit"]!.GetValue<int>(), answer["out"]!.GetValue<string>(), answer["err"]!.GetValue<string>());
            }
            catch (OperationCanceledException)
            {
                Lost(session, root, "");
                cancellationToken.ThrowIfCancellationRequested();
                return new CommandResult(ChildProcessCommandRunner.TimedOut, "", "The command ran longer than the server allows and was stopped.");
            }
            catch (IOException)
            {
                return Lost(session, root, "The session ended while the command ran.");
            }
        }
        finally
        {
            session.Turn.Release();
        }
    }

    private CommandResult Lost(Session session, string root, string message)
    {
        session.Kill();
        lock (_gate)
            if (_sessions.TryGetValue(root, out var current) && ReferenceEquals(current, session)) _sessions.Remove(root);
        return new CommandResult(70, "", message);
    }

    /// <summary>Ends the session of a root now (a workspace was abandoned).</summary>
    public void End(string projectRoot)
    {
        lock (_gate)
            if (_sessions.Remove(Path.GetFullPath(projectRoot), out var session)) session.Kill();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var s in _sessions.Values) s.Kill();
            _sessions.Clear();
        }
    }

    private sealed class Session
    {
        private readonly Process _process;
        private long _lastUsed = DateTime.UtcNow.Ticks;

        private Session(Process process)
        {
            _process = process;
            Input = process.StandardInput;
            Output = process.StandardOutput;
        }

        public StreamWriter Input { get; }
        public StreamReader Output { get; }
        public SemaphoreSlim Turn { get; } = new(1, 1);
        public int Pid => _process.Id;
        public bool Alive => !_process.HasExited;
        public void Touch() => Interlocked.Exchange(ref _lastUsed, DateTime.UtcNow.Ticks);
        public TimeSpan IdleSince(DateTime now) => Turn.CurrentCount == 0 ? TimeSpan.Zero : now - new DateTime(Interlocked.Read(ref _lastUsed), DateTimeKind.Utc);

        public static Session Start(string root)
        {
            var (file, prefix) = ChildProcessCommandRunner.Executable();
            var start = new ProcessStartInfo(file)
            {
                WorkingDirectory = root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in prefix) start.ArgumentList.Add(a);
            start.ArgumentList.Add(SessionHost.Argument);
            var process = Process.Start(start) ?? throw new InvalidOperationException("The session process did not start.");
            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();    // read and dropped, so a chatty child never blocks on a full pipe
            return new Session(process);
        }

        public void Kill()
        {
            try { _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* it ended as we looked */ }
        }
    }
}
