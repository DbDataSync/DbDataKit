using DbDataKit.Runner;
using DbDataKit.Runner.Kit;
using DbDataKit.Runner.Protocol;

namespace DbDataSync.TaskRunner.Runner;

/// <summary>
/// What the engine does when a runner starts it (<c>DbDataSync.TaskRunner --runner</c>, the executable launch of the package, plan 28c ED3): connect to the runner with the token and endpoint in the environment, run
/// the one task it was started for, and say how it ended. The task is named by the runner (<c>DBDATAKIT_TASK_NAME</c>); <c>drain</c> is the only one so far.
/// </summary>
public static class RunnerEntry
{
    /// <summary>The line written after the run, so the runner's log says what crossed the socket (ED5 is a test of it).</summary>
    public const string TrafficPrefix = "runner socket traffic: ";

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length > 0)
        {
            Console.Error.WriteLine($"Argument error: a runner starts the engine with no arguments beyond --runner; got '{string.Join(' ', args)}'. The task's inputs come from its manifest.");
            return (int)ExitCode.ConfigError;
        }

        var task = Environment.GetEnvironmentVariable(RunnerProtocolNames.TaskVariable);
        if (!string.Equals(task, "drain", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"The engine runs the task 'drain' and the runner asked for '{task}'.");
            return (int)ExitCode.ConfigError;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var result = await RemoteJobHost.RunAsync<DrainJob>(cts.Token).ConfigureAwait(false);
        Console.Out.WriteLine($"{TrafficPrefix}sent {RunnerSocketTraffic.BytesSent} bytes, received {RunnerSocketTraffic.BytesReceived} bytes.");
        return result.End.State == TaskEndState.Failed ? (int)ExitCode.DataError : (int)ExitCode.Success;
    }
}
