using System.Globalization;
using DbDataKit.Runner;
using DbDataKit.Runner.Kit;
using DbDataKit.Runner.Protocol;
using DbDataKit.Sync.Core.Config;
using DbDataKit.Sync.Core.Secrets;
using DbDataSync.Libraries;
using DbDataSync.Scripting;

namespace DbDataSync.TaskRunner.Runner;

/// <summary>
/// What the set's <c>preview</c> is started with: the replication, its mapping, how many rows (1 to <see cref="MappingPreview.MaxRows"/>, text as every input is), where the definition is, and the text of the mapping's file
/// as the person's draft has it (<c>mapping_text</c>; empty reads the mapping the version has).
/// </summary>
public sealed record PreviewInput(string Replication = "", string Mapping = "", string Rows = "", string RepoDir = "repo", string MappingText = "");

/// <summary>
/// What the set's <c>try-strategy</c> is started with: the replication, its mapping, the strategy (an unsaved one as JSON in <c>strategy</c>, or one the replication has in <c>name</c>), the column, where the definition
/// is, and the text of the mapping's file as the person's draft has it (<c>mapping_text</c>; empty reads the mapping the version has).
/// </summary>
public sealed record TryStrategyInput(string Replication = "", string Mapping = "", string Strategy = "", string Name = "", string Column = "", string RepoDir = "repo", string MappingText = "");

/// <summary>The names and inputs of the set's two tasks a person waits for on a screen.</summary>
public static class PreviewTasks
{
    /// <summary>The first rows of a mapping's source (<see cref="PreviewJob"/>).</summary>
    public const string Preview = "preview";

    /// <summary>A segmenting strategy's candidates (<see cref="TryStrategyJob"/>).</summary>
    public const string TryStrategy = "try-strategy";

    /// <summary>The <c>preview</c> task's inputs as the manifest declares them; each has a default, so a run names only what it needs.</summary>
    public static IReadOnlyList<TaskInput> PreviewInputs(string repoDir) =>
    [
        new("replication", DbDataKit.State.ProgressType.Text, Default: "", Description: "The replication of the mapping"),
        new("mapping", DbDataKit.State.ProgressType.Text, Default: "", Description: "The mapping whose source is read"),
        new("rows", DbDataKit.State.ProgressType.Text, Default: MappingPreview.DefaultRows.ToString(CultureInfo.InvariantCulture), Description: $"How many rows to read, 1 to {MappingPreview.MaxRows}"),
        new("repo_dir", DbDataKit.State.ProgressType.Text, Default: repoDir, Description: "Where the definition is in the package"),
        new(MappingTextInput, DbDataKit.State.ProgressType.Text, Default: "", Description: MappingTextDescription),
    ];

    /// <summary>The input that carries the mapping's file as the draft has it, unsaved edits included; the run record keeps it as it keeps every input.</summary>
    public const string MappingTextInput = "mapping_text";

    private const string MappingTextDescription = "The text of the mapping's file as the draft has it; empty reads the mapping of this version";

    /// <summary>The <c>try-strategy</c> task's inputs as the manifest declares them.</summary>
    public static IReadOnlyList<TaskInput> TryStrategyInputs(string repoDir) =>
    [
        new("replication", DbDataKit.State.ProgressType.Text, Default: "", Description: "The replication of the mapping"),
        new("mapping", DbDataKit.State.ProgressType.Text, Default: "", Description: "The mapping the strategy divides"),
        new("strategy", DbDataKit.State.ProgressType.Text, Default: "", Description: "An unsaved strategy as JSON: name, kind, sql, scriptName, parameters"),
        new("name", DbDataKit.State.ProgressType.Text, Default: "", Description: "A strategy the replication has, when no unsaved one is given"),
        new("column", DbDataKit.State.ProgressType.Text, Default: "", Description: "The column of the source the strategy's bounds apply to"),
        new("repo_dir", DbDataKit.State.ProgressType.Text, Default: repoDir, Description: "Where the definition is in the package"),
        new(MappingTextInput, DbDataKit.State.ProgressType.Text, Default: "", Description: MappingTextDescription),
    ];

    /// <summary>
    /// A preview or trial that failed: the whole reason is the run's answer (<c>{"error": ...}</c>), for the person who asked, and the run fails with what its record may keep. Always throws.
    /// </summary>
    internal static async Task FailAsync(PreviewException failure, Func<object, CancellationToken, Task> answer, CancellationToken ct)
    {
        await answer(new { error = failure.Message }, ct).ConfigureAwait(false);
        throw new TaskFailureException(TaskFailureKind.Permanent, failure.Kept);
    }

    /// <summary>Where the run's package and scratch folder are, as the runner tells a task.</summary>
    internal static (string RepoRoot, string Scratch) Folders(string repoDir)
    {
        var package = Environment.GetEnvironmentVariable(RunnerProtocolNames.PackageDirectoryVariable)
            ?? throw new InvalidOperationException($"{RunnerProtocolNames.PackageDirectoryVariable} is not set: the runner tells a task where its package's view is.");
        var scratch = Environment.GetEnvironmentVariable(RunnerProtocolNames.ScratchVariable)
            ?? throw new InvalidOperationException($"{RunnerProtocolNames.ScratchVariable} is not set: the runner tells a task where it may write.");
        return (Path.GetFullPath(Path.Combine(package, repoDir)), scratch);
    }
}

/// <summary>
/// The set's manual, read-only task <c>preview</c> (Design: docs/authoring-metadata.md#rows-and-trials-are-answered-never-kept): the first rows of a mapping's source, read with the run login of the source's
/// slot, handed to the runner as the run's answer. The mapping is the draft's when the run is given its text, read with this version's replication, connections and drivers. It records no step and logs no value: the rows go to the console that started the run, for the person who asked, and nowhere else.
/// </summary>
[RunnerTask(PreviewTasks.Preview)]
public sealed class PreviewJob : RunnerJob<PreviewInput, NoProgress, NoSlots>
{
    protected override async Task RunAsync(CancellationToken ct)
    {
        var rows = MappingPreview.DefaultRows;
        if (Input.Rows.Trim().Length > 0 && (!int.TryParse(Input.Rows.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out rows) || rows is < 1 or > MappingPreview.MaxRows))
            throw new TaskFailureException(TaskFailureKind.Permanent, $"The input `rows` is a whole number from 1 to {MappingPreview.MaxRows}, not '{Input.Rows}'.");
        var (repoRoot, scratch) = PreviewTasks.Folders(Input.RepoDir);
        var secrets = SecretStore.ForProviders();
        var config = ConfigRepository.ReadOnly(Path.Combine(repoRoot, "config"), secrets);
        var engine = EngineComposition.Compose(config, repoRoot, new ScriptCacheDirectory(Path.Combine(scratch, "script-cache")));
        var connections = new RunLoginConnections(config, engine.Drivers, engine.Libraries, repoRoot, secrets);
        Status($"{PreviewTasks.Preview} {Input.Replication}/{Input.Mapping}");
        MappingPreviewAnswer answer;
        try { answer = await new MappingPreview(config, connections).ReadAsync(Input.Replication, Input.Mapping, rows, ct, Input.MappingText).ConfigureAwait(false); }
        catch (PreviewException ex) { await PreviewTasks.FailAsync(ex, AnswerAsync, ct).ConfigureAwait(false); throw; }
        await AnswerAsync(answer, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// The set's manual, read-only task <c>try-strategy</c> (Design: docs/authoring-metadata.md#rows-and-trials-are-answered-never-kept): a segmenting strategy of a replication, saved or being edited, run against a
/// mapping as a reload would run it, its SQL with the run logins the channel binds; every candidate is the run's answer. The mapping is the draft's when the run is given its text. A strategy's SQL is read only by intent, not by force: it is the author's SQL, run with
/// the login a reload runs it with.
/// </summary>
[RunnerTask(PreviewTasks.TryStrategy)]
public sealed class TryStrategyJob : RunnerJob<TryStrategyInput, NoProgress, NoSlots>
{
    protected override async Task RunAsync(CancellationToken ct)
    {
        var (repoRoot, scratch) = PreviewTasks.Folders(Input.RepoDir);
        var secrets = SecretStore.ForProviders();
        var config = ConfigRepository.ReadOnly(Path.Combine(repoRoot, "config"), secrets);
        var engine = EngineComposition.Compose(config, repoRoot, new ScriptCacheDirectory(Path.Combine(scratch, "script-cache")));
        var connections = new RunLoginConnections(config, engine.Drivers, engine.Libraries, repoRoot, secrets);
        Status($"{PreviewTasks.TryStrategy} {Input.Replication}/{Input.Mapping}");
        StrategyTrialAnswer answer;
        try
        {
            answer = await new StrategyTrial(config, connections, new CustomSegmentExpansion(new SegmentingStrategyRunner(engine.Scripts)))
                .TryAsync(Input.Replication, Input.Mapping, Input.Strategy, Input.Name, Input.Column, ct, Input.MappingText).ConfigureAwait(false);
        }
        catch (PreviewException ex) { await PreviewTasks.FailAsync(ex, AnswerAsync, ct).ConfigureAwait(false); throw; }
        await AnswerAsync(answer, ct).ConfigureAwait(false);
    }
}
