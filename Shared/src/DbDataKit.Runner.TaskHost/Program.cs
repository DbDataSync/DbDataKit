using System.Reflection;
using System.Runtime.InteropServices;
using DbDataKit.Hosting.Children;
using DbDataKit.Runner.Kit;
using DbDataKit.Runner.Protocol;
using DbDataKit.Runner.TaskHost;
using Grpc.Core;

// dbdatakit-taskhost: started by a runner for one run of one task of a .NET package (plan 28a R06, DR1).
// It reads where to connect, the run token, the package assembly and the task's name from its environment (never argv), loads the package into its own load context,
// finds the job class with [RunnerTask("<task>")], and runs it through the kit against the runner's socket. The job's outcome goes to the runner as the end message; the exit code only says
// whether the host itself got that far (0 = the end message was sent, whatever the outcome was; anything else = the host failed before or while doing so).
return await TaskHostProgram.RunAsync();

namespace DbDataKit.Runner.TaskHost
{
    /// <summary>The exit codes of the task host. A runner reconciles them with the end message: an end message wins, and without one a zero is a protocol violation and a non-zero a process that exited.</summary>
    internal static class ExitCodes
    {
        /// <summary>The job ran and the end message was sent (its outcome may be a failure).</summary>
        public const int Ended = 0;

        /// <summary>The host failed for a reason of its own (an unexpected exception).</summary>
        public const int HostFailure = 1;

        /// <summary>The environment is incomplete or wrong: no endpoint, token, assembly or task.</summary>
        public const int BadEnvironment = 64;

        /// <summary>The package assembly could not be loaded, or holds no job for the task (or more than one).</summary>
        public const int PackageFailure = 65;

        /// <summary>The runner could not be reached, refused the token or speaks another protocol version.</summary>
        public const int RunnerFailure = 66;

        /// <summary>The parent (the runner) went away; the host stops at once.</summary>
        public const int ParentGone = 67;

        /// <summary>The host was asked to stop (SIGTERM, Ctrl-C) and did not send an end message.</summary>
        public const int Stopped = 68;
    }

    internal static class TaskHostProgram
    {
        public static async Task<int> RunAsync()
        {
            using var cancel = new CancellationTokenSource();
            using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                TryCancel(cancel);
            });
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                TryCancel(cancel);
            };

            // a child never outlives its runner (there is no job object on Linux)
            var parentPid = int.TryParse(Environment.GetEnvironmentVariable(ChildEnvironment.ParentPidVariable), out var pid) ? pid : 0;
            var parentStart = long.TryParse(Environment.GetEnvironmentVariable(ChildEnvironment.ParentStartVariable), out var ticks) ? ticks : 0;
            _ = ParentDeathMonitor.Start(parentPid, parentStart, () =>
            {
                Console.Error.WriteLine($"dbdatakit-taskhost: the runner (pid {parentPid}) is gone; stopping.");
                Environment.Exit(ExitCodes.ParentGone);
            }, cancel.Token);

            RunnerEndpoint endpoint;
            string token;
            try
            {
                (endpoint, token) = RunnerEndpoint.FromEnvironment();
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException)
            {
                return Fail(ExitCodes.BadEnvironment, ex.Message);
            }

            var assemblyPath = Environment.GetEnvironmentVariable(RunnerProtocolNames.AssemblyVariable);
            var taskName = Environment.GetEnvironmentVariable(RunnerProtocolNames.TaskVariable);
            if (string.IsNullOrWhiteSpace(assemblyPath) || string.IsNullOrWhiteSpace(taskName))
                return Fail(ExitCodes.BadEnvironment, $"The task host needs {RunnerProtocolNames.AssemblyVariable} (the package assembly) and {RunnerProtocolNames.TaskVariable} (the task) in its environment.");
            if (!File.Exists(assemblyPath)) return Fail(ExitCodes.BadEnvironment, $"The package assembly '{assemblyPath}' does not exist.");

            Type job;
            try
            {
                job = FindJob(new PackageLoadContext(assemblyPath), taskName);
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or FileNotFoundException or ReflectionTypeLoadException or InvalidOperationException)
            {
                return Fail(ExitCodes.PackageFailure, $"The package assembly '{assemblyPath}' could not be used for the task '{taskName}': {Describe(ex)}");
            }

            try
            {
                await RemoteJobHost.RunAsync(job, endpoint, token, cancel.Token).ConfigureAwait(false);
                return ExitCodes.Ended;
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                return Fail(ExitCodes.Stopped, "The task host was asked to stop before the run ended.");
            }
            catch (Exception ex) when (ex is RunnerProtocolException or IOException or RpcException or ArgumentException)
            {
                return Fail(ExitCodes.RunnerFailure, $"The run could not be carried out against the runner at {endpoint}: {ex.Message}");
            }
            catch (Exception ex)
            {
                return Fail(ExitCodes.HostFailure, $"The task host failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>The one job class of the package assembly that declares the task.</summary>
        internal static Type FindJob(PackageLoadContext context, string taskName)
        {
            var assembly = context.LoadPackage();
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = [.. ex.Types.Where(t => t is not null)!];
                if (types.Length == 0) throw;
            }

            var found = types.Where(t => t is { IsClass: true, IsAbstract: false } && typeof(RunnerJobBase).IsAssignableFrom(t)
                                         && t.GetCustomAttribute<RunnerTaskAttribute>(inherit: false)?.Name == taskName).ToList();
            return found.Count switch
            {
                1 => found[0],
                0 => throw new InvalidOperationException($"{assembly.GetName().Name} has no job class with [RunnerTask(\"{taskName}\")]."),
                _ => throw new InvalidOperationException($"{assembly.GetName().Name} has {found.Count} job classes with [RunnerTask(\"{taskName}\")] ({string.Join(", ", found.Select(t => t.FullName))}); a task has one."),
            };
        }

        private static string Describe(Exception ex) => ex is ReflectionTypeLoadException { LoaderExceptions: { Length: > 0 } inner }
            ? string.Join("; ", inner.Where(e => e is not null).Select(e => e!.Message).Distinct())
            : ex.Message;

        private static void TryCancel(CancellationTokenSource source)
        {
            try
            {
                source.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // the run is over
            }
        }

        private static int Fail(int code, string message)
        {
            Console.Error.WriteLine("dbdatakit-taskhost: " + message);
            return code;
        }
    }
}
