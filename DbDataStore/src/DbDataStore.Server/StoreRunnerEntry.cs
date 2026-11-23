using DbDataKit.DataModel.Runner;
using DbDataKit.Runner;
using DbDataKit.Runner.Kit;
using DbDataKit.Runner.Protocol;

namespace DbDataStore.Server;

/// <summary>
/// What <c>dbdatastore --runner</c> does (plan 33 SC24, the Store without its server): the program is the runtime <c>dbdatastore-runtime</c> that a model's definition package names; a runner starts it with the token and endpoint in the
/// environment, and it runs the one task it was started for (<c>DBDATAKIT_TASK_NAME</c>) and says how it ended. The tasks are <c>deploy</c> (<see cref="ModelDeployJob"/>), <c>contract</c> (<see cref="ModelContractJob"/>) and <c>preflight</c> (<see cref="ModelPreflightJob"/>).
/// </summary>
public static class StoreRunnerEntry
{
    public const string Flag = "--runner";

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length > 0)
        {
            await Console.Error.WriteLineAsync($"A runner starts dbdatastore with no arguments beyond {Flag}; got '{string.Join(' ', args)}'. The task's inputs come from its manifest.");
            return 2;
        }

        var task = Environment.GetEnvironmentVariable(RunnerProtocolNames.TaskVariable);
        if (task is not (ModelDeployPackage.DeployTask or ModelDeployPackage.ContractTask or DbDataKit.Runner.Preflight.TaskName))
        {
            await Console.Error.WriteLineAsync(string.IsNullOrWhiteSpace(task)
                ? $"{RunnerProtocolNames.TaskVariable} is not set: the runner tells dbdatastore which task it is run for."
                : $"dbdatastore runs the tasks '{ModelDeployPackage.DeployTask}', '{ModelDeployPackage.ContractTask}' and '{DbDataKit.Runner.Preflight.TaskName}' of a model's package, not '{task}'.");
            return 2;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        var options = new RemoteRunOptions { Client = "DbDataStore", SlotFactory = StoreGrantConnections.SlotFactory };
        var result = task switch
        {
            ModelDeployPackage.ContractTask => await RemoteJobHost.RunAsync<ModelContractJob>(cts.Token, options).ConfigureAwait(false),
            DbDataKit.Runner.Preflight.TaskName => await RemoteJobHost.RunAsync<ModelPreflightJob>(cts.Token, options).ConfigureAwait(false),
            _ => await RemoteJobHost.RunAsync<ModelDeployJob>(cts.Token, options).ConfigureAwait(false),
        };
        return result.End.State == TaskEndState.Failed ? 1 : 0;
    }
}
