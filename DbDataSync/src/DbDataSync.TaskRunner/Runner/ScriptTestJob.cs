using System.Globalization;
using DbDataKit.Runner;
using DbDataKit.Runner.Kit;
using DbDataKit.Sync.Core.Config;
using DbDataKit.Sync.Core.Secrets;
using DbDataSync.Scripting;

namespace DbDataSync.TaskRunner.Runner;

/// <summary>What the set's <c>script-test</c> is started with: the script of <c>config/scripts/</c>, how many rows of sample input (1 to <see cref="ScriptSampleTest.MaxRows"/>, text as every input is) and where the definition is.</summary>
public sealed record ScriptTestInput(string Name = "", string Rows = "", string RepoDir = "repo");

/// <summary>
/// What <c>script-test</c> answers: the script, its slot, whether it compiled and ran, where its input came from, the cases, the log lines a transform wrote, and in <paramref name="Message"/> the compiler's or the script's
/// words when it did not (an answer's <c>error</c> is read by the console as a failed run, and a script that does not compile is a test that worked).
/// </summary>
public sealed record ScriptTestAnswer(string Name, string Kind, bool Ok, string Source, IReadOnlyList<ScriptSampleCase> Cases, IReadOnlyList<string> Log, string? Message);

/// <summary>
/// The set's manual task <c>script-test</c> (Design: docs/authoring-metadata.md#a-scripts-test-runs-on-a-runner): one script of the version, compiled and run against generated sample input, never a connection; the
/// cases, the log lines a transform wrote and the compiler's or the script's error are the run's answer, for the person who asked on the Scripts tab, and kept nowhere. A script that does not compile or throws is an
/// answer, not a failed run: finding that out is what the test is for. Only an input that is not one fails the run.
/// </summary>
[RunnerTask(TaskName)]
public sealed class ScriptTestJob : RunnerJob<ScriptTestInput, NoProgress, NoSlots>
{
    /// <summary>The task's name in the set's manifest.</summary>
    public const string TaskName = "script-test";

    /// <summary>The task's inputs as the manifest declares them; each has a default, so a run names only what it needs.</summary>
    public static IReadOnlyList<TaskInput> Inputs(string repoDir) =>
    [
        new("name", DbDataKit.State.ProgressType.Text, Default: "", Description: "The script of config/scripts/ to test"),
        new("rows", DbDataKit.State.ProgressType.Text, Default: ScriptSampleTest.DefaultRows.ToString(CultureInfo.InvariantCulture), Description: $"How many rows of sample input, 1 to {ScriptSampleTest.MaxRows}"),
        new("repo_dir", DbDataKit.State.ProgressType.Text, Default: repoDir, Description: "Where the definition is in the package"),
    ];

    /// <summary>Whether <paramref name="name"/> can name a file under <c>config/scripts/</c>: it never holds a separator, never starts with <c>-</c> or <c>.</c> and never leaves the folder.</summary>
    public static bool IsScriptName(string name) =>
        name.Length is > 0 and <= 64 && char.IsAsciiLetterOrDigit(name[0]) && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-') && !name.Contains("..", StringComparison.Ordinal);

    protected override async Task RunAsync(CancellationToken ct)
    {
        var rows = ScriptSampleTest.DefaultRows;
        if (Input.Rows.Trim().Length > 0 && (!int.TryParse(Input.Rows.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out rows) || rows is < 1 or > ScriptSampleTest.MaxRows))
            throw new TaskFailureException(TaskFailureKind.Permanent, $"The input `rows` is a whole number from 1 to {ScriptSampleTest.MaxRows}, not '{Input.Rows}'.");
        var name = Input.Name.Trim();
        if (!IsScriptName(name)) throw new TaskFailureException(TaskFailureKind.Permanent, $"The input `name` names a script of config/scripts/ (letters, digits, `.`, `_` and `-`), not '{name}'.");
        var (repoRoot, scratch) = PreviewTasks.Folders(Input.RepoDir);
        var config = ConfigRepository.ReadOnly(Path.Combine(repoRoot, "config"), SecretStore.ForProviders());
        Status($"{TaskName} {name}");
        var outcome = await ScriptSampleTest.RunAsync(config, name, rows, new ScriptCacheDirectory(Path.Combine(scratch, "script-cache")), ct).ConfigureAwait(false);
        // the package's own folder is no business of the screen: the error names the scripts folder as the draft has it
        var message = outcome.Error?.Replace(repoRoot + Path.DirectorySeparatorChar, "", StringComparison.Ordinal).Replace(repoRoot, ".", StringComparison.Ordinal);
        await AnswerAsync(new ScriptTestAnswer(outcome.Name, outcome.Kind, outcome.Ok, outcome.Source, outcome.Cases, outcome.Log, message), ct).ConfigureAwait(false);
    }
}
