using DbDataSync.Core.Config;

namespace DbDataSync.Scripting;

/// <summary>One case of a sample test, as the Scripts tab shows it: what went in, what came out, and why when that is not obvious.</summary>
public sealed record ScriptSampleCase(string Input, string? Output, string? Note);

/// <summary>
/// What a sample test of a script found: whether it compiled and ran, where its input came from, the cases, the log lines a transform wrote and the error (the compiler's words, what the script threw, or that the
/// script does not exist).
/// </summary>
public sealed record ScriptSampleOutcome(string Name, string Kind, bool Ok, string Source, IReadOnlyList<ScriptSampleCase> Cases, IReadOnlyList<string> Log, string? Error);

/// <summary>
/// The test of one script of a configuration repository against generated sample input: the script is compiled and run, and what it did is the outcome. It never opens a connection. <c>dbdatasync script test</c> and
/// the set's task <c>script-test</c> on a Runner both run it.
/// </summary>
public static class ScriptSampleTest
{
    /// <summary>The most rows of sample input a test asks for.</summary>
    public const int MaxRows = 50;

    /// <summary>The rows of sample input when none are asked for.</summary>
    public const int DefaultRows = 5;

    /// <summary>Compiles the script <paramref name="name"/> (into <paramref name="cache"/>) and runs it against <paramref name="rows"/> rows of generated input.</summary>
    public static async Task<ScriptSampleOutcome> RunAsync(ConfigRepository repository, string name, int rows, ScriptCacheDirectory cache, CancellationToken cancellationToken)
    {
        ScriptDefinition script;
        try { script = repository.LoadScript(name); }
        catch (FileNotFoundException) { return new(name, "", false, "", [], [], $"Script '{name}' does not exist in {repository.ScriptsDirectory}."); }

        var service = new ScriptTestService(repository, new ScriptCompiler(cache, repository.ScriptsDirectory));
        ScriptTestResult result;
        // a manifest whose slot this build does not know, or a mapping the test names that is not there, is the caller's mistake: it is the outcome's error, not a failure of the test
        try { result = await service.RunAsync(new ScriptTestRequest(script, SampleRows: rows), cancellationToken); }
        catch (Exception ex) when (ex is ConfigValidationException or FileNotFoundException) { return new(name, script.Manifest.Kind, false, "", [], [], ex.Message); }
        return new(name, script.Manifest.Kind, result.Error is null, result.Source, [.. result.Cases.Select(c => new ScriptSampleCase(c.Input, c.Output, c.Note))], result.Log, result.Error);
    }
}
