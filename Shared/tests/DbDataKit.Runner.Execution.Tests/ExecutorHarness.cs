using System.Text.RegularExpressions;
using DbDataKit.Runner.Execution;
using DbDataKit.Runner.Limits;
using DbDataKit.Runner.Protocol.Server;
using DbDataKit.Secrets;
using DbDataKit.State;
using Microsoft.Extensions.Logging;

namespace DbDataKit.Runner.Execution.Tests;

/// <summary>One test's state database on an engine, a runner protocol host on a real socket, a data directory and an executor over them.</summary>
public sealed partial class ExecutorHarness : IDisposable
{
    public const string Package = "sample";
    public const string Channel = "prod";

    private readonly ExecutionEngines _engines = new();

    public ExecutorHarness(string engine, Action<ExecutorSettings>? configure = null)
    {
        Database = _engines.Open(engine);
        DataDirectory = Directory.CreateTempSubdirectory("dbdatakit-exec-data-").FullName;
        Host = RunnerProtocolHost.StartAsync().GetAwaiter().GetResult();
        Redactor = new Redactor();
        var settings = new ExecutorSettings();
        configure?.Invoke(settings);
        Executor = new TaskExecutor(Database, Host, new TaskExecutorOptions
        {
            DataDirectory = DataDirectory,
            TaskHostPath = PackageFixture.TaskHostPath(),
            Redactor = Redactor,
            Limits = settings.Limits,
            MaxScratchBytes = settings.MaxScratchBytes,
            ScratchSampleInterval = settings.ScratchSampleInterval,
            Logger = settings.Logger,
            ExitWait = settings.ExitWait,
            Slots = settings.Slots?.Invoke(Database, Redactor),
        });
    }

    /// <summary>What a test changes about the executor.</summary>
    public sealed class ExecutorSettings
    {
        public LimitOptions? Limits { get; set; }

        public long MaxScratchBytes { get; set; } = 1024L * 1024 * 1024;

        /// <summary>How often the scratch size is read during a run; null: only after it (the tests that count on a run finishing before the first look).</summary>
        public TimeSpan? ScratchSampleInterval { get; set; }

        public ILogger? Logger { get; set; }

        /// <summary>Makes the slot resolver from the harness's state database and the redactor the run log uses.</summary>
        public Func<StateDatabase, IRedactor, ISlotResolver>? Slots { get; set; }

        public TimeSpan ExitWait { get; set; } = TimeSpan.FromSeconds(10);
    }

    public StateDatabase Database { get; }

    public string DataDirectory { get; }

    public RunnerProtocolHost Host { get; }

    public Redactor Redactor { get; }

    public TaskExecutor Executor { get; }

    public RunStore Runs => new(Database, "runner");

    public string ScratchRoot => Path.Combine(DataDirectory, "scratch");

    public StepStore StepsOf(string task) => new(Database, new ProgressKey(Package, Channel, task));

    public const string Chain = "0197f0e0-0000-7000-8000-00000000c0de";

    /// <summary>A request for a task of the sample package, run from its materialized view.</summary>
    public async Task<TaskRunRequest> SampleRequest(string task, Action<RequestSettings>? configure = null)
    {
        var version = await PackageFixture.SamplePackage();
        var settings = new RequestSettings();
        configure?.Invoke(settings);
        return new TaskRunRequest
        {
            Task = new TaskRef(Package, Channel, task),
            PackageDirectory = version.Directory,
            PackageVersion = "1.0.0",
            Launch = new PackageLaunch(LaunchKind.Assembly, Assembly: "DbDataKit.Runner.TestPackage.dll"),
            RunId = settings.RunId ?? Database.NewId(),
            Chain = settings.Chain ?? Chain,
            Attempt = settings.Attempt,
            Inputs = settings.Inputs,
            Limits = settings.Limits,
            CancellationGrace = settings.Grace,
        };
    }

    /// <summary>A request for an executable package: a script of the scripts package, run by <c>sh</c>.</summary>
    public async Task<TaskRunRequest> ScriptRequest(string script, params string[] extraArguments)
    {
        var version = await PackageFixture.ScriptPackage();
        return new TaskRunRequest
        {
            Task = new TaskRef("scripts", Channel, "run"),
            PackageDirectory = version.Directory,
            PackageVersion = "1.0.0",
            Launch = new PackageLaunch(LaunchKind.Executable, Command: "sh", Args: ["{package}/" + script, .. extraArguments]),
            RunId = Database.NewId(),
            Chain = Chain,
        };
    }

    /// <summary>A request for an executable package whose launch command is a file of the package itself (not a program of the runner).</summary>
    public async Task<TaskRunRequest> ProgramRequest(string program)
    {
        var version = await PackageFixture.ScriptPackage();
        return new TaskRunRequest
        {
            Task = new TaskRef("scripts", Channel, "run"),
            PackageDirectory = version.Directory,
            PackageVersion = "1.0.0",
            Launch = new PackageLaunch(LaunchKind.Executable, Command: program),
            RunId = Database.NewId(),
            Chain = Chain,
        };
    }

    public sealed class RequestSettings
    {
        public Guid? RunId { get; set; }

        public string? Chain { get; set; }

        public int Attempt { get; set; } = 1;

        public IReadOnlyDictionary<string, string> Inputs { get; set; } = new Dictionary<string, string>();

        public DbDataKit.Runner.TaskLimits Limits { get; set; } = DbDataKit.Runner.TaskLimits.None;

        public TimeSpan Grace { get; set; } = TimeSpan.FromSeconds(30);
    }

    /// <summary>Waits until a line containing <paramref name="text"/> is in the run's log, and returns the line.</summary>
    public async Task<RunLogLine> WaitForLog(Guid runId, string text, TimeSpan? timeout = null)
    {
        var until = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(60));
        while (DateTimeOffset.UtcNow < until)
        {
            if (Runs.Logs(runId, limit: 1000).FirstOrDefault(l => l.Message.Contains(text, StringComparison.Ordinal)) is { } line) return line;
            await Task.Delay(50);
        }

        throw new TimeoutException($"'{text}' did not appear in the log of {runId}. The log:\n" + string.Join("\n", Runs.Logs(runId, limit: 1000).Select(l => $"[{l.Level}] {l.Message}")));
    }

    public IReadOnlyList<RunLogLine> Log(Guid runId) => Runs.Logs(runId, limit: 1000);

    /// <summary>The process id the executor logged when it started the process.</summary>
    public async Task<int> PidOf(Guid runId)
    {
        var line = await WaitForLog(runId, "(pid ");
        return int.Parse(PidPattern().Match(line.Message).Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"\(pid (\d+)\)")]
    private static partial Regex PidPattern();

    public void Dispose()
    {
        Host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _engines.Dispose();
        try { Directory.Delete(DataDirectory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a data directory left in temp is untidy, not wrong */ }
    }
}
