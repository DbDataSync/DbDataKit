using System.Runtime.InteropServices;
using DbDataKit.Runner.Kit;

namespace DbDataBuild.Cli.Runner;

/// <summary>
/// The entry a runner starts for a Build package (plan 28c E01): `dbdatabuild --runner-task`, with the endpoint and the run token in the environment (never argv). It runs <see cref="RefreshJob"/> through the kit against the
/// runner's socket. The exit code only says whether the run's end reached the runner: 0 when it did (whatever the outcome), 66 when the runner could not be reached, 68 when stopped first.
/// </summary>
public static class RunnerHost
{
    public const string Argument = "--runner-task";

    public static async Task<int> RunAsync()
    {
        using var cancel = new CancellationTokenSource();
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; cancel.Cancel(); });
        try
        {
            var options = new RemoteRunOptions { Client = "DbDataBuild", AnyTaskName = true };
            // a published project has one task per refresh plan (E07), each named `connection-plan`, and every one runs the refresh job; the operations package (plan 28c E21) names its tasks without a hyphen, so no plan is taken for one
            switch (Environment.GetEnvironmentVariable(DbDataKit.Runner.Protocol.RunnerProtocolNames.TaskVariable))
            {
                case BuildOperations.ApplyPlan: await RemoteJobHost.RunAsync<ApplyPlanJob>(cancel.Token, options).ConfigureAwait(false); break;
                case BuildOperations.Ack: await RemoteJobHost.RunAsync<AckJob>(cancel.Token, options).ConfigureAwait(false); break;
                case BuildOperations.Seed: await RemoteJobHost.RunAsync<SeedJob>(cancel.Token, options).ConfigureAwait(false); break;
                case CatchUpJob.TaskName: await RemoteJobHost.RunAsync<CatchUpJob>(cancel.Token, options).ConfigureAwait(false); break;
                default: await RemoteJobHost.RunAsync<RefreshJob>(cancel.Token, options).ConfigureAwait(false); break;
            }

            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException or Grpc.Core.RpcException or RunnerProtocolException)
        {
            Console.Error.WriteLine($"dbdatabuild {Argument}: the run could not be carried out against the runner: {ex.GetType().Name}: {ex.Message}");
            return 66;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine($"dbdatabuild {Argument}: stopped before the run ended.");
            return 68;
        }
    }
}
