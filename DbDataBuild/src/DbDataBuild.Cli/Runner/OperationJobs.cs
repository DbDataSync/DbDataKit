using System.Text.RegularExpressions;
using DbDataKit.Runner;
using DbDataKit.Runner.Kit;
using DbDataKit.State;
using DbDataKit.Tracking.Build;

namespace DbDataBuild.Cli.Runner;

/// <summary>What the apply task is started with: the connection, the stored plan (its id in the tracking tables, or its id with the plan's text in <see cref="PlanText"/> for a plan that lives in a file), and what the person allowed.</summary>
public sealed record ApplyPlanInput(string Connection, string Plan, string PlanText = "", bool AllowRisky = false, string AllowDestructive = "", string AllowConsumers = "", string InputPins = "");

/// <summary>What the acknowledge task is started with: the connection, the kind (`drift`, `definition` or `history`), what is acknowledged and why.</summary>
public sealed record AckInput(string Connection, string Kind, string Name, string Reason);

/// <summary>What the seed task is started with: the connection, the variables the seeds read and whether a seeded table that exists is dropped and recreated.</summary>
public sealed record SeedInput(string Connection, int Seed = 1, string Scale = "", bool Replace = false);

/// <summary>
/// The person-driven writes of Build as manual-only runner tasks (plan 28c E21, owner decision of 2026-11-12: no write login in a Build server, every person-driven write a manual runner task). What each task runs is the command
/// of the same name through <see cref="CliApp"/> in the task process, so the statements, the gate, the tracking rows and the refusals are exactly what the command line gives; what is new is who the run is for (the person the
/// Build server signed in, <see cref="Actor.Variable"/> from the run's record) and where the login comes from (the runner's binding of the slot, never a server's store). Task names have no hyphen, which every refresh task's name has
/// (`connection-plan`), so a plan can never be mistaken for one of these.
/// </summary>
internal static class BuildOperations
{
    public const string ApplyPlan = "apply_plan";
    public const string Ack = "ack";
    public const string Seed = "seed";

    public static IReadOnlyList<string> All { get; } = [ApplyPlan, Ack, Seed];

    /// <summary>The package of a project's operations on a runner: its refresh package's name with `-ops` (the project folder's name, as `project publish --operations` names it when `--package` is not given).</summary>
    public static string PackageOf(string projectId) => Cap(RunnerProject.NameOf(projectId), 64 - 4) + "-ops";

    private static string Cap(string text, int length) => text.Length > length ? text[..length] : text;

    /// <summary>The operation as the route and the console name it (`apply-plan`) to the task's name (`apply_plan`); null when it is none of them.</summary>
    public static string? TaskOf(string operation) => operation.Replace('-', '_') is var name && All.Contains(name) ? name : null;

    private static readonly Regex Word = new(@"^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ObjectName = new(@"^[A-Za-z0-9_][A-Za-z0-9_.$ -]{0,255}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The inputs of a task as the manifest declares them (the names a run is started with).</summary>
    public static IReadOnlyList<TaskInput> InputsOf(string task) => task switch
    {
        ApplyPlan =>
        [
            new("connection", ProgressType.Text, Required: true, Description: "The connection the plan is for"),
            new("plan", ProgressType.Text, Required: true, Description: "The plan's id (`2026-11-12-0a1b2c3d`): kept in the tracking tables (`plans.deploy.keep: database`), or the id of the text in plan_text"),
            new("plan_text", ProgressType.Text, Default: "", Description: "The text of a plan that lives in a file, when it is not kept in the tracking tables"),
            new("allow_risky", ProgressType.Bool, Default: "false", Description: "Allow the plan's risky steps"),
            new("allow_destructive", ProgressType.Text, Default: "", Description: "Objects (comma separated) whose destructive steps are allowed"),
            new("allow_consumers", ProgressType.Text, Default: "", Description: "Published consumers (comma separated) that may lose a column they read"),
            new("input_pins", ProgressType.Text, Default: "", Description: "`[model.load.]name=value`, one per line: the values of inputs that moved since the plan"),
        ],
        Ack =>
        [
            new("connection", ProgressType.Text, Required: true, Description: "The connection the decision is recorded for"),
            new("kind", ProgressType.Text, Required: true, Description: "drift, definition or history"),
            new("name", ProgressType.Text, Required: true, Description: "The object (drift), the model (definition) or `model.column` (history)"),
            new("reason", ProgressType.Text, Required: true, Description: "Why the change is accepted; recorded with the person"),
        ],
        Seed =>
        [
            new("connection", ProgressType.Text, Required: true, Description: "The connection to create and fill the seeded tables on"),
            new("seed", ProgressType.Int, Default: "1", Description: "The variable `seed` the seeds read"),
            new("scale", ProgressType.Text, Default: "", Description: "The variable `scale` the seeds read (blank: the project's own)"),
            new("replace", ProgressType.Bool, Default: "false", Description: "Drop and recreate a seeded table that exists"),
        ],
        _ => [],
    };

    /// <summary>The refusal of inputs that no command should be given (an option in the place of a name, a path in the place of an id), or null. Checked by the route before anything is queued and by the task before it runs.</summary>
    public static string? Refusal(string task, IReadOnlyDictionary<string, string> inputs)
    {
        string Get(string name) => inputs.TryGetValue(name, out var v) ? v : "";
        if (!Word.IsMatch(Get("connection"))) return "`connection` is a connection's name.";
        switch (task)
        {
            case ApplyPlan:
                if (!Word.IsMatch(Get("plan"))) return "`plan` is a plan's id (letters, digits, `-`, `_` and `.`).";
                if (Get("plan_text").Length > 900_000) return "`plan_text` is larger than 900 KB.";
                foreach (var objectName in Split(Get("allow_destructive"), ',')) if (!ObjectName.IsMatch(objectName)) return $"`allow_destructive` names `{objectName}`, which is not an object.";
                foreach (var consumer in Split(Get("allow_consumers"), ',')) if (consumer.StartsWith('-')) return $"`allow_consumers` names `{consumer}`, which is not a consumer.";
                foreach (var pin in Split(Get("input_pins"), '\n')) if (pin.StartsWith('-') || !pin.Contains('=')) return $"`input_pins` has `{pin}`; each is `[model.load.]name=value`.";
                return null;
            case Ack:
                if (Get("kind") is not ("drift" or "definition" or "history")) return "`kind` is drift, definition or history.";
                if (!ObjectName.IsMatch(Get("name"))) return "`name` is the object, the model or `model.column` that is acknowledged.";
                if (string.IsNullOrWhiteSpace(Get("reason"))) return "`reason` is required: an acknowledgement is recorded with who made it and why.";
                return null;
            case Seed:
                if (Get("seed").Length > 0 && !int.TryParse(Get("seed"), out _)) return "`seed` is a whole number.";
                if (Get("scale").Length > 0 && !int.TryParse(Get("scale"), out _)) return "`scale` is a whole number.";
                return null;
            default:
                return $"`{task}` is not an operation (apply_plan, ack, seed).";
        }
    }

    private static IEnumerable<string> Split(string text, char separator) => text.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The command line of the task: `connection deploy --apply-plan`, `connection deploy --ack`, `connection seed`. A dry run is the command's own (`--dry-run`, and a seed without `--apply`).</summary>
    public static string[] Arguments(string task, IReadOnlyDictionary<string, string> inputs, string project, string? planFile, bool dryRun)
    {
        string Get(string name) => inputs.TryGetValue(name, out var v) ? v : "";
        var args = new List<string> { "connection", task == Seed ? "seed" : "deploy", Get("connection") };
        switch (task)
        {
            case ApplyPlan:
                args.Add("--apply-plan");
                args.Add(planFile ?? Get("plan"));
                if (bool.TryParse(Get("allow_risky"), out var risky) && risky) args.Add("--allow-risky");
                foreach (var o in Split(Get("allow_destructive"), ',')) { args.Add("--allow-destructive"); args.Add(o); }
                foreach (var c in Split(Get("allow_consumers"), ',')) { args.Add("--allow-consumer"); args.Add(c); }
                foreach (var pin in Split(Get("input_pins"), '\n')) args.Add("--input=" + pin);
                if (dryRun) args.Add("--dry-run");
                break;
            case Ack:
                args.Add("--ack");
                args.Add($"{Get("kind")}:{Get("name")}");
                args.Add("--reason=" + Get("reason"));
                break;
            default:
                if (int.TryParse(Get("seed"), out var seed)) { args.Add("--seed"); args.Add(seed.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
                if (int.TryParse(Get("scale"), out var scale)) { args.Add("--scale"); args.Add(scale.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
                if (bool.TryParse(Get("replace"), out var replace) && replace) args.Add("--replace");
                if (!dryRun) args.Add("--apply");
                break;
        }

        args.AddRange(["--project", project, "--format", "json"]);
        return [.. args];
    }

    /// <summary>
    /// Who the run is for: the person the Build server signed in, as the runner recorded it (<see cref="DbDataKit.Runner.Protocol.RunnerProtocolNames.RequestedByVariable"/>), else the run's own name. This is the value of
    /// <see cref="Actor.Variable"/> the command reads to name the one who applied a plan or acknowledged a block.
    /// </summary>
    public static string ActorOf(Func<string, string?> env, string package, string channel) =>
        env(DbDataKit.Runner.Protocol.RunnerProtocolNames.RequestedByVariable) is { Length: > 0 } asked ? asked : $"runner {package}/{channel}";
}

/// <summary>The shared run of the three operations: the command in this process, the logins from the environment the runner's slots fill, the tracking journal of a state-first project, the follower copy afterwards.</summary>
public abstract class OperationJob<TInput> : RunnerJob<TInput, NoProgress, NoSlots>
{
    internal abstract string Operation { get; }

    protected override async Task RunAsync(CancellationToken ct)
    {
        var project = Environment.GetEnvironmentVariable(RefreshJob.PackageDirectoryVariable);
        if (string.IsNullOrWhiteSpace(project) || !Directory.Exists(project))
            throw new TaskFailureException(TaskFailureKind.Permanent, $"The package directory ({RefreshJob.PackageDirectoryVariable}) is not set or does not exist; an operation runs from the project the package holds.");

        var inputs = InputBinder.ToStrings(Input);
        if (BuildOperations.Refusal(Operation, inputs) is { } refusal) throw new TaskFailureException(TaskFailureKind.Permanent, $"{Operation}: {refusal}");

        var scratch = Environment.GetEnvironmentVariable(DbDataKit.Runner.Protocol.RunnerProtocolNames.ScratchVariable);
        var actor = BuildOperations.ActorOf(Environment.GetEnvironmentVariable, Run.Package, Run.Channel);
        string? Env(string name) =>
            name == Actor.Variable ? actor
            : name == InitCommand.StatementLogVariable && !string.IsNullOrEmpty(scratch) ? Path.Combine(scratch, "statement-log")
            : Environment.GetEnvironmentVariable(name);

        // a plan that lives in a file is written to the run's scratch directory (the package's view is read-only) and applied from there
        string? planFile = null;
        if (Operation == BuildOperations.ApplyPlan && inputs.GetValueOrDefault("plan_text") is { Length: > 0 } text)
        {
            if (string.IsNullOrEmpty(scratch)) throw new TaskFailureException(TaskFailureKind.Permanent, "The run has no scratch directory to put the plan's text in.");
            var directory = Path.Combine(scratch, "plans");
            Directory.CreateDirectory(directory);
            planFile = Path.Combine(directory, inputs["plan"] + ".plan.yml");
            await File.WriteAllTextAsync(planFile, text, new System.Text.UTF8Encoding(false), ct).ConfigureAwait(false);
        }

        // an acknowledgement has no dry form of its own: a dry run of the task says what it would record and records nothing
        if (Run.DryRun && Operation == BuildOperations.Ack)
        {
            Run.WouldDo($"record the acknowledgement {inputs["kind"]}:{inputs["name"]} on {inputs["connection"]} for {actor}: {inputs["reason"]}");
            return;
        }

        var args = BuildOperations.Arguments(Operation, inputs, project, planFile, Run.DryRun);
        Status($"{Operation} {inputs["connection"]} for {actor}{(Run.DryRun ? " (dry run)" : "")}");
        int exit;
        System.Text.Json.JsonDocument document;
        // a project whose tracking is state-first writes each record to the runner's state, through the protocol, before it writes the target (plan 28c E16)
        using (TrackingJournalSource.Use(new RemoteBuildTrackingSink(Tracking)))
            (exit, document) = await System.Threading.Tasks.Task.Run(() => RefreshJob.Invoke(args, Env), ct).ConfigureAwait(false);
        using var _ = document;
        try
        {
            if (exit == CliApp.ExitBusy) throw new TaskFailureException(TaskFailureKind.Temporary, $"Another operation holds the lock of `{inputs["connection"]}` ({RefreshJob.Describe(document)}).");
            if (exit != CliApp.ExitOk)
            {
                var said = RefreshJob.Describe(document);
                if (said.Length == 0) said = string.Join(" ", RefreshJob.Lines(document, "messages").Concat(RefreshJob.Lines(document, "errors")).TakeLast(5));
                throw new TaskFailureException(TaskFailureKind.Permanent, $"`{string.Join(' ', args.Take(3))}` ended with exit code {exit}. {said}".TrimEnd());
            }
        }
        finally
        {
            if (!Run.DryRun) await CopyTrackingAsync(project, Env, inputs["connection"], ct).ConfigureAwait(false);
        }
    }

    /// <summary>The follower step after a write (plan 28c E16): the tracking rows it wrote are copied into the runner's state. Never fails the run.</summary>
    private async Task CopyTrackingAsync(string project, Func<string, string?> env, string connection, CancellationToken ct)
    {
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromMinutes(1));
            foreach (var line in await RunnerTrackingCopy.CopyAsync(Tracking, project, connection, env, TimeSpan.FromSeconds(50), limit.Token).ConfigureAwait(false)) Console.Out.WriteLine("tracking " + line);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // a copy that could not be made is the catch-up task's to make
        }
    }
}

/// <summary>Applies a plan a person made (plan 28c E21): `connection deploy --apply-plan` on the runner, the tracking tables naming the person the run is for.</summary>
[RunnerTask(BuildOperations.ApplyPlan)]
public sealed class ApplyPlanJob : OperationJob<ApplyPlanInput>
{
    internal override string Operation => BuildOperations.ApplyPlan;
}

/// <summary>Records a person's decision about a block (`connection deploy --ack`): one `block_log` row naming the person and the reason (plan 28c E21).</summary>
[RunnerTask(BuildOperations.Ack)]
public sealed class AckJob : OperationJob<AckInput>
{
    internal override string Operation => BuildOperations.Ack;
}

/// <summary>Creates the seeded tables on a connection and fills them (`connection seed --apply`); a dry run prints what it would do and connects to nothing (plan 28c E21).</summary>
[RunnerTask(BuildOperations.Seed)]
public sealed class SeedJob : OperationJob<SeedInput>
{
    internal override string Operation => BuildOperations.Seed;
}
