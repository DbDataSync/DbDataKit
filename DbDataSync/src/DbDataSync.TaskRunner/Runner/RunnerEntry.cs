using DbDataKit.Runner;
using DbDataKit.Runner.Kit;
using DbDataKit.Runner.Protocol;

namespace DbDataSync.TaskRunner.Runner;

/// <summary>
/// What the engine does when a runner starts it (<c>DbDataSync.TaskRunner --runner</c>, the executable launch of the package, plan 28c ED3): connect to the runner with the token and endpoint in the environment, run
/// the one task it was started for, and say how it ended. The task is named by the runner (<c>DBDATAKIT_TASK_NAME</c>): a published replication set has one task per replication, each named for its replication (<see cref="ReplicationSetPackage"/>, plan 28c E08), and every one runs the drain; the set's manual task <c>provision</c> runs <see cref="ProvisionJob"/> (E21), its manual task <c>preflight</c> runs <see cref="SetPreflightJob"/>, its manual tasks <c>preview</c> and <c>try-strategy</c> run <see cref="PreviewJob"/> and <see cref="TryStrategyJob"/>, and its manual tasks <c>discover</c> and <c>describe</c> run <see cref="SetDiscoverJob"/> and <see cref="SetDescribeJob"/>, and its manual task <c>script-test</c> runs <see cref="ScriptTestJob"/>.
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
        if (string.IsNullOrWhiteSpace(task))
        {
            Console.Error.WriteLine($"{RunnerProtocolNames.TaskVariable} is not set: the runner tells the engine which task it is run for.");
            return (int)ExitCode.ConfigError;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        // the set's provisioning task runs the database setup (plan 28c E21), its preflight the read-only checks before a version is served; every other task is a replication's drain
        var result = task switch
        {
            ReplicationSetPackage.ProvisionTask => await RemoteJobHost.RunAsync<ProvisionJob>(cts.Token, new RemoteRunOptions { Client = "DbDataSync" }).ConfigureAwait(false),
            ReplicationSetPackage.PreflightTask => await RemoteJobHost.RunAsync<SetPreflightJob>(cts.Token, new RemoteRunOptions { Client = "DbDataSync" }).ConfigureAwait(false),
            PreviewTasks.Preview => await RemoteJobHost.RunAsync<PreviewJob>(cts.Token, new RemoteRunOptions { Client = "DbDataSync" }).ConfigureAwait(false),
            PreviewTasks.TryStrategy => await RemoteJobHost.RunAsync<TryStrategyJob>(cts.Token, new RemoteRunOptions { Client = "DbDataSync" }).ConfigureAwait(false),
            ScriptTestJob.TaskName => await RemoteJobHost.RunAsync<ScriptTestJob>(cts.Token, new RemoteRunOptions { Client = "DbDataSync" }).ConfigureAwait(false),
            SetCatalogSteps.Discover => await RemoteJobHost.RunAsync<SetDiscoverJob>(cts.Token, new RemoteRunOptions { Client = "DbDataSync" }).ConfigureAwait(false),
            SetCatalogSteps.Describe => await RemoteJobHost.RunAsync<SetDescribeJob>(cts.Token, new RemoteRunOptions { Client = "DbDataSync" }).ConfigureAwait(false),
            _ => await RemoteJobHost.RunAsync<DrainJob>(cts.Token, new RemoteRunOptions { Client = "DbDataSync", AnyTaskName = true }).ConfigureAwait(false),
        };
        Console.Out.WriteLine($"{TrafficPrefix}sent {RunnerSocketTraffic.BytesSent} bytes, received {RunnerSocketTraffic.BytesReceived} bytes.");
        return result.End.State == TaskEndState.Failed ? (int)ExitCode.DataError : (int)ExitCode.Success;
    }
}
