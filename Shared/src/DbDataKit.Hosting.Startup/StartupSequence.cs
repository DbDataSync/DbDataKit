using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace DbDataKit.Hosting.Startup;

/// <summary>
/// The steps every server runs before it serves, in one order: folder, keys, certificate, state, ports, join. A server fills in the steps it has;
/// the sequence makes the logs subfolder and the failure file first, rewrites the file as each step begins, deletes it when every step succeeded, and
/// at the first step that fails writes the full reason (the account, the folders, what the step names, the exception chain) and stops. The caller
/// exits with <see cref="StartupResult.ExitCode"/>, so the service manager records a failed start.
/// <code>
/// var result = await new StartupSequence(new StartupOptions { Service = SuiteServices.Runner, DataFolder = folder })
///     .Step(StartupStep.Folder, c => folders.Check())
///     .Step(StartupStep.State, (c, ct) => state.MigrateAsync(ct))
///     .RunAsync(ct);
/// if (!result.Succeeded) return result.ExitCode;
/// </code>
/// Design: docs/install.md#one-startup-sequence
/// </summary>
public sealed class StartupSequence(StartupOptions options)
{
    /// <summary>The exit code of a process whose start failed.</summary>
    public const int FailureExitCode = 1;

    private readonly Dictionary<StartupStep, Func<StartupContext, CancellationToken, Task>> _steps = [];

    public StartupOptions Options { get; } = options;

    /// <summary>Fills in a step. Each step is filled in at most once; the order they run in is the steps' order, whatever order they are filled in.</summary>
    public StartupSequence Step(StartupStep step, Func<StartupContext, CancellationToken, Task> run)
    {
        if (!_steps.TryAdd(step, run)) throw new InvalidOperationException($"The {step.Name()} step is filled in twice.");
        return this;
    }

    /// <summary>Fills in a step that does not wait.</summary>
    public StartupSequence Step(StartupStep step, Action<StartupContext> run) =>
        Step(step, (context, _) =>
        {
            run(context);
            return Task.CompletedTask;
        });

    /// <summary>Runs the steps that are filled in. Never throws for a failed step: the result says which failed and why.</summary>
    public async Task<StartupResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var file = new StartupFailureFile(Options.LogsRoot, Options.Service, Options.Unreportable);
        file.Prepare();
        var since = Options.Time.GetUtcNow();
        var context = new StartupContext(Options, file.LogFolder);
        foreach (var step in StartupSteps.InOrder)
        {
            if (!_steps.TryGetValue(step, out var run)) continue;
            file.WriteProgress(since, step);
            try
            {
                await run(context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                // a server that joins the console before its listeners start (the Gateway) joins inside the ports step: a refused join is still the join step's
                var failed = Find<StartupStepException>(error) is { Step: StartupStep.Join } ? StartupStep.Join : step;
                var report = Describe(failed, error, since, Options.Time.GetUtcNow(), file);
                file.WriteFailure(report);
                if (file.IsUsable) Options.Failed(report);
                else Options.Unreportable(report);
                return new StartupResult(failed, error, report, file.FilePath) { FailureFileWritten = file.IsUsable };
            }
        }
        file.Delete();
        return new StartupResult(null, null, null, file.FilePath);
    }

    /// <summary>The text of the failure file.</summary>
    internal string Describe(StartupStep step, Exception error, DateTimeOffset since, DateTimeOffset failedAt, StartupFailureFile file)
    {
        var named = Find<StartupStepException>(error);
        var text = new StringBuilder();
        var program = Options.Version is null ? Options.Service.Program : $"{Options.Service.Program} {Options.Version}";
        text.AppendLine($"Startup of {program} failed at the {step.Name()} step, at {Utc(failedAt)} (starting since {Utc(since)}).");
        text.AppendLine();
        text.AppendLine(named?.Message ?? error.Message);
        text.AppendLine();
        text.AppendLine($"Step: {step.Name()}");
        text.AppendLine($"Account: {StartupAccount.Current()}");
        text.AppendLine($"Data folder: {Options.DataFolder ?? "(not resolved)"}");
        text.AppendLine($"Logs folder: {file.LogFolder}");
        foreach (var folder in Options.Folders) text.AppendLine($"{folder.Label}: {folder.Value}");
        if (named is not null)
            foreach (var fact in named.Facts) text.AppendLine($"{fact.Label}: {fact.Value}");

        var socket = Find<SocketException>(error);
        var portRefused = socket is { SocketErrorCode: SocketError.AccessDenied or SocketError.AddressAlreadyInUse };
        if (portRefused && named?.Step != StartupStep.Ports)
            text.AppendLine($"Excluded port ranges: {ExcludedPortRanges.Describe(Options.ExcludedPorts())}");

        text.AppendLine();
        // a server's host starts its background services together with its listeners, in the ports step: a failure there that is not a refused port is
        // one of those services', and the message above names it
        var whatToCheck = step == StartupStep.Ports && named is null && !portRefused
            ? "The server's services start together with its listeners, and one of them failed: the message above says which and why."
            : step.WhatToCheck();
        text.AppendLine($"What to check: {whatToCheck}");
        if (portRefused)
            text.AppendLine("Hint: a listening port could not be bound. On Windows, Hyper-V, WSL and Docker reserve ranges of ports that change at each boot "
                            + $"(`{ExcludedPortRanges.Command}`), and a listener inside one is refused although no program holds it; "
                            + "`netstat -ano` shows a program that holds the port.");

        text.AppendLine();
        text.AppendLine("Exception chain:");
        foreach (var e in Chain(error)) text.AppendLine($"  {e.GetType().FullName}: {e.Message}");
        text.AppendLine();
        text.AppendLine(error.ToString());
        return text.ToString();
    }

    private static string Utc(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    internal static IEnumerable<Exception> Chain(Exception error)
    {
        for (var e = error; e is not null; e = e.InnerException)
        {
            yield return e;
            if (e is AggregateException aggregate && aggregate.InnerExceptions.Count > 1)
            {
                foreach (var inner in aggregate.InnerExceptions.Skip(1))
                    foreach (var nested in Chain(inner))
                        yield return nested;
            }
        }
    }

    internal static T? Find<T>(Exception error) where T : Exception => Chain(error).OfType<T>().FirstOrDefault();
}
