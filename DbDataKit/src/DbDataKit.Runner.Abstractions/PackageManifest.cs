using DbDataKit.Monitoring;
using DbDataKit.Scheduling;
using DbDataKit.State;

namespace DbDataKit.Runner;

/// <summary>How the runner starts a package's tasks (plan 28a decision DR1): through the task host for a .NET assembly, or as an executable that speaks the runner protocol.</summary>
public enum LaunchKind
{
    Assembly,
    Executable,
}

/// <summary>
/// How a package is started. <see cref="Assembly"/> (a path inside the package) for <see cref="LaunchKind.Assembly"/>; <see cref="Command"/> (a path inside the package, or a program on the runner) and
/// <see cref="Args"/> for <see cref="LaunchKind.Executable"/>. A definition package names the runtime package that runs it (<see cref="Runtime"/>, <c>launch.runtime</c>, plan 28c E04) instead of an assembly or command of its own.
/// </summary>
public sealed record PackageLaunch(LaunchKind Kind, string? Assembly = null, string? Command = null, IReadOnlyList<string>? Args = null, RuntimeReference? Runtime = null)
{
    public IReadOnlyList<string> ArgList => Args ?? [];
}

/// <summary>A reference from a definition package to a runtime package: the runtime's name and the versions of it the definition accepts (<see cref="VersionRange"/>: <c>2.x</c>, <c>&gt;=2.1 &lt;3</c>, <c>2.1.4</c>).</summary>
public sealed record RuntimeReference(string Name, string Range);

/// <summary>The contracts a package was built against, so staging can refuse one a runner cannot serve (plan 28a DR4). Versions are as the contract writes them: <c>10.0</c>, <c>1</c>, <c>dbdatakit.runner.v1</c>.</summary>
/// <param name="Dotnet">The .NET runtime (<c>10.0</c>); required for an assembly package.</param>
/// <param name="Kit">The kit's version (<c>1</c>, <c>1.2</c>), when the package uses it.</param>
/// <param name="Protocol">The runner protocol's package name and version, <c>dbdatakit.runner.v1</c>.</param>
public sealed record BuiltAgainst(string? Dotnet, string? Kit, string? Protocol);

/// <summary>What starts a task inside the package: the type and method of an assembly package, extra arguments of an executable one.</summary>
public sealed record TaskEntry(string? Type = null, string Method = TaskEntry.DefaultMethod, IReadOnlyList<string>? Args = null)
{
    public const string DefaultMethod = "Run";

    public IReadOnlyList<string> ArgList => Args ?? [];
}

public enum TriggerKind
{
    /// <summary>When another task's run ends (with one of the <see cref="TaskTrigger.On"/> outcomes).</summary>
    AfterRun,
    /// <summary>A webhook delivered through the serving layer's inbox.</summary>
    Webhook,
    /// <summary>A file arriving in a connection's folder.</summary>
    File,
    /// <summary>A change in a DbDataStore model.</summary>
    StoreChange,
    /// <summary>By hand.</summary>
    Manual,
}

/// <summary>
/// One way a task is started besides its schedule. Which fields count depends on <see cref="Kind"/>: after-run uses <see cref="Task"/>, <see cref="Package"/> and <see cref="On"/>; webhook <see cref="Source"/> and, to check a signature, <see cref="Secret"/> (the name of a secret in the runner's secret store);
/// file <see cref="Connection"/> (a slot of the task), <see cref="Path"/> and <see cref="Poll"/>; store change <see cref="Model"/> and <see cref="Entity"/>.
/// </summary>
public sealed record TaskTrigger(
    TriggerKind Kind,
    string? Task = null,
    string? Package = null,
    IReadOnlyList<RunOutcome>? On = null,
    string? Source = null,
    string? Connection = null,
    string? Path = null,
    TimeSpan? Poll = null,
    string? Model = null,
    string? Entity = null,
    string? Secret = null)
{
    /// <summary>The outcomes an after-run trigger fires on; <see cref="RunOutcome.Succeeded"/> when none is written.</summary>
    public IReadOnlyList<RunOutcome> Outcomes => On is { Count: > 0 } ? On : [RunOutcome.Succeeded];

    public static TaskTrigger AfterRun(string task, params RunOutcome[] on) => new(TriggerKind.AfterRun, Task: task, On: on.Length == 0 ? null : on);

    public static TaskTrigger OnWebhook(string source, string? secret = null) => new(TriggerKind.Webhook, Source: source, Secret: secret);

    public static TaskTrigger OnFile(string connection, string path, TimeSpan? poll = null) => new(TriggerKind.File, Connection: connection, Path: path, Poll: poll);

    public static TaskTrigger OnStoreChange(string model, string? entity = null) => new(TriggerKind.StoreChange, Model: model, Entity: entity);

    public static TaskTrigger ByHand() => new(TriggerKind.Manual);
}

/// <summary>A value the task is started with, set per channel or per run.</summary>
public sealed record TaskInput(string Name, ProgressType Type = ProgressType.Text, bool Required = false, string? Default = null, string? Description = null);

/// <summary>
/// A place in the task where a connection is plugged in. The package says what kind of connection it needs; a channel's binding says which one (plan 30). <see cref="Env"/> names how the runner puts the bound login into the
/// task's environment (one of <see cref="SlotEnvironmentFormats"/>; plan 28c section 6); null: the task asks for it through the protocol's <c>ResolveConnection</c> and the runner writes no variable.
/// </summary>
public sealed record ConnectionSlot(string Name, string Kind, string? Env = null);

/// <summary>
/// The forms in which the runner may inject a bound login into a task process (plan 28c section 6, ED11). A registry, not free text: a package picks a name, and the runner writes the variables of that form, so a package cannot make the
/// runner write an arbitrary variable.
/// </summary>
public static class SlotEnvironmentFormats
{
    /// <summary>Sync: one <c>DBDATAKIT_SECRET_&lt;NAME&gt;</c> per connection, the variable the engine's secret store reads for <c>dbdata:sync:connection:&lt;name&gt;</c>.</summary>
    public const string SyncSecret = "sync-secret";

    /// <summary>Build: <c>DBDATAKIT_BUILD_&lt;CONNECTION&gt;_READ</c> and <c>_WRITE</c>, one per login role the connection has (<c>LoginEnvironment.Name</c>).</summary>
    public const string BuildLogin = "build-login";

    public static IReadOnlyList<string> All { get; } = [SyncSecret, BuildLogin];

    public static bool IsKnown(string? format) => format is not null && All.Contains(format, StringComparer.Ordinal);
}

/// <summary>How a declared step keeps its result (plan 28b section 4): a small value as JSON, or a payload written in chunks.</summary>
public enum StepKind
{
    Value,
    Stream,
}

/// <summary>
/// A step the task declares, so the console and the staging check can list it before a run (plan 28b K12). <see cref="Input"/> and <see cref="Result"/> are type names as text, for people; nothing reads them back.
/// </summary>
public sealed record TaskStep(string Name, StepKind Kind = StepKind.Value, string? Input = null, string? Result = null);

/// <summary>A unit the task declares: the name its units are recorded under, and the type of the items as text.</summary>
public sealed record TaskUnit(string Name, string? Item = null);

/// <summary>
/// What a task may use. Every value is optional: a missing one is the runner's default. Each one written has to be above zero.
/// <see cref="Concurrency"/> is how many runs of the package may be going at once (plan 28a DR7). <see cref="Attempts"/> counts the first run too (plan 28a DR8); the wait before a retry starts at
/// <see cref="Backoff"/>, doubles, and stops growing at <see cref="MaxBackoff"/>.
/// </summary>
public sealed record TaskLimits(TimeSpan? Timeout = null, int? MemoryMb = null, decimal? Cpu = null, int? Concurrency = null, int? Attempts = null, TimeSpan? Backoff = null, TimeSpan? MaxBackoff = null)
{
    public static TaskLimits None { get; } = new();
}

/// <summary>
/// One task of a package. <c>Schedule</c> is when it is due by itself (<see cref="Schedule.HasSchedule"/> false: only its triggers and a person start it). <c>Progress</c> is the checkpoints and state values it
/// keeps between runs, in the model <see cref="ProgressMigration.Plan"/> takes (plan 29 section 4.3). <c>Requires</c> are labels a runner must have to take the task (plan 28 question 7); <c>Capabilities</c> what the
/// task needs the runner to be able to do. <c>AllowOverlap</c> true lets a run start while the last one is still going; by default a task never overlaps itself. <c>Steps</c>, <c>Units</c> and <c>Counters</c> are what the task declares for the console
/// (plan 28b K12); a .NET job's are generated from its declarations, another language writes them by hand, and none is required.
/// </summary>
public sealed record PackageTask(
    string Name,
    TaskEntry Entry,
    Schedule Schedule,
    IReadOnlyList<TaskTrigger> Triggers,
    IReadOnlyList<TaskInput> Inputs,
    IReadOnlyList<ConnectionSlot> Connections,
    ProgressDeclaration Progress,
    TaskLimits Limits,
    IReadOnlyList<string> Requires,
    IReadOnlyList<string> Capabilities,
    bool AllowOverlap = false,
    IReadOnlyList<TaskStep>? Steps = null,
    IReadOnlyList<TaskUnit>? Units = null,
    IReadOnlyList<string>? Counters = null)
{
    public IReadOnlyList<TaskStep> StepList => Steps ?? [];

    public IReadOnlyList<TaskUnit> UnitList => Units ?? [];

    public IReadOnlyList<string> CounterList => Counters ?? [];

    public PackageTask(string name, TaskEntry entry)
        : this(name, entry, new Schedule(), [], [], [], ProgressDeclaration.Empty, TaskLimits.None, [], [])
    {
    }
}

/// <summary>A package manifest, <c>package/1</c>: what the package is, how it starts, what it was built against and the tasks it holds.</summary>
public sealed record PackageManifest(string Name, string? Description, PackageLaunch Launch, BuiltAgainst BuiltAgainst, IReadOnlyList<PackageTask> Tasks, bool IsRuntime = false, string? Version = null, string? Platform = null)
{
    /// <summary>The key (<c>runtime: true</c>) of a runtime package: no tasks of its own, a <see cref="Version"/>, and a launch that is an executable (plan 28c E04).</summary>
    public const string RuntimeKey = "runtime";

    /// <summary>The key (<c>platform: linux-x64</c>) that says which platform a runtime package's files are built for (plan 28c E04/E05 follow-up: runtimes are published per platform).</summary>
    public const string PlatformKey = "platform";

    /// <summary>
    /// The name the package has in the runner's registry. An ordinary package: <see cref="Name"/>. A runtime package: <c>&lt;name&gt;-&lt;platform&gt;</c> (<c>dbdatabuild-runtime-linux-x64</c>), so the same runtime can be published once
    /// per platform, each with its own versions and its own active version per channel; a definition still names the runtime by <see cref="Name"/>, and the runner adds its own platform (<see cref="RuntimePlatforms.PackageName"/>).
    /// </summary>
    public string PackageName => IsRuntime && Platform is { } platform ? RuntimePlatforms.PackageName(Name, platform) : Name;

    /// <summary>The key of a definition package's reference to its runtime, <c>launch.runtime</c>.</summary>
    public const string LaunchRuntimeKey = "launch.runtime";

    /// <summary>The keys of <c>package/1</c> added after the first runners that this manifest uses; a runner that does not know one refuses the manifest naming it.</summary>
    public IReadOnlyList<string> NewerKeys => [.. (Launch.Runtime is null ? [] : new[] { LaunchRuntimeKey }), .. (IsRuntime ? new[] { RuntimeKey } : []), .. (Platform is null ? [] : new[] { PlatformKey })];

    /// <summary>The format id every manifest starts with, as <c>schema: package/1</c>.</summary>
    public const string FormatId = "package/1";

    public const int FormatVersion = 1;
}

/// <summary>
/// The platforms runtime packages are published for (plan 28c QE8: <c>win-x64</c> and <c>linux-x64</c>; macOS is a development platform only) and the platform of this process. A runner takes the runtime package of its own platform and
/// never fetches another's; tests give the runner another platform than the one they run on.
/// </summary>
public static class RuntimePlatforms
{
    public const string WindowsX64 = "win-x64";

    public const string LinuxX64 = "linux-x64";

    public static IReadOnlyList<string> Supported { get; } = [WindowsX64, LinuxX64];

    public static bool IsSupported(string? platform) => platform is not null && Supported.Contains(platform, StringComparer.Ordinal);

    /// <summary>This process's platform as a runtime identifier (<c>win-x64</c>, <c>linux-arm64</c>, <c>osx-arm64</c>): the operating system and the processor architecture, without the distribution part of <c>RuntimeInformation.RuntimeIdentifier</c>.</summary>
    public static string Current { get; } = Detect();

    /// <summary>The registry name of the runtime <paramref name="name"/> built for <paramref name="platform"/>.</summary>
    public static string PackageName(string name, string platform) => $"{name}-{platform}";

    private static string Detect()
    {
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : OperatingSystem.IsLinux() ? "linux" : "unknown";
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        return $"{os}-{arch}";
    }
}
