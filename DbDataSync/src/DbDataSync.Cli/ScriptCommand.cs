using System.Text.Encodings.Web;
using System.Text.Json;
using DbDataKit.Cli;
using DbDataKit.Config.Git;
using DbDataSync.Api.Services;
using DbDataSync.Core.Config;
using DbDataSync.Core.Secrets;
using DbDataSync.Scripting;

namespace DbDataSync.Cli;

/// <summary>
/// <c>dbdatasync script list|test</c> — the scripts of <c>config/scripts/</c>, and the same test the editor's button runs: the script is compiled and run against generated sample input, and what it did is printed.
/// A test that touches a system (a live connection, a mapping's own tables) stays in the editor; this one never opens a connection.
/// </summary>
public static class ScriptCommand
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The <c>data</c> schemas of the two verbs (<see cref="DataSchemas"/>): what <c>--format json</c> carries, and what <c>--json</c> prints as the whole document.</summary>
    public static readonly string ListSchema = DataSchemas.Object([("scripts", DataSchemas.ListOf(DataSchemas.Object([("name", DataSchemas.String), ("kind", DataSchemas.String), ("language", DataSchemas.String), ("error", Nullable(DataSchemas.String))], "name", "kind", "language", "error")))], "scripts");

    public static readonly string TestSchema = DataSchemas.Object(
    [
        ("name", DataSchemas.String), ("kind", DataSchemas.String), ("ok", DataSchemas.Boolean), ("source", DataSchemas.String),
        ("cases", DataSchemas.ListOf(DataSchemas.Object([("input", DataSchemas.String), ("output", Nullable(DataSchemas.String)), ("note", Nullable(DataSchemas.String))], "input", "output", "note"))),
        ("log", DataSchemas.StringList), ("error", Nullable(DataSchemas.String)),
    ], "name", "kind", "ok", "source", "cases", "log", "error");

    private static string Nullable(string schema) => $$"""{ "anyOf": [{{schema}}, { "type": "null" }] }""";

    /// <summary>One script of <c>config/scripts/</c>: its name, the slot it fills (or the reason its manifest cannot be read, in which case the kind is empty) and its language.</summary>
    public sealed record ScriptEntry(string Name, string Kind, string Language, string? Error);

    /// <summary>One case of a test, as the editor's test shows it: what went in, what came out, and why when that is not obvious.</summary>
    public sealed record TestCase(string Input, string? Output, string? Note);

    /// <summary>What <c>script test</c> found: whether the script compiled and ran, where its input came from, the cases, the log lines a transform wrote and the error (the compiler's words, or what the script threw). A script that does not exist is an error too.</summary>
    public sealed record TestOutcome(string Name, string Kind, bool Ok, string Source, IReadOnlyList<TestCase> Cases, IReadOnlyList<string> Log, string? Error);

    public static Task<int> RunAsync(string[] args) => RunAsync(args, null);

    /// <summary>As <see cref="RunAsync(string[])"/>, and what was found goes to <paramref name="data"/> too (the <c>--format json</c> document's <c>data</c>).</summary>
    public static async Task<int> RunAsync(string[] args, IDictionary<string, object?>? data)
    {
        if (args.Length == 0) return Usage();
        var repository = OpenRepository(args);
        var json = CliOptions.Has(args, "--json");
        switch (args[0].ToLowerInvariant())
        {
            case "list":
                var scripts = List(repository);
                if (data is not null) data["scripts"] = scripts;
                if (json) Console.WriteLine(JsonSerializer.Serialize(new { scripts }, Json));
                else foreach (var script in scripts) Console.WriteLine(script.Error is null ? $"{script.Name}\t{script.Kind}" : $"{script.Name}\t(unreadable: {script.Error})");
                return 0;
            case "test":
                var rest = args[1..];
                if (rest.Length == 0 || rest[0].StartsWith("--", StringComparison.Ordinal)) return Usage();
                var outcome = await TestAsync(repository, rest[0], int.TryParse(CliOptions.Read(rest, "--rows"), out var n) ? n : 5);
                if (data is not null) { data["name"] = outcome.Name; data["kind"] = outcome.Kind; data["ok"] = outcome.Ok; data["source"] = outcome.Source; data["cases"] = outcome.Cases; data["log"] = outcome.Log; data["error"] = outcome.Error; }
                if (json) Console.WriteLine(JsonSerializer.Serialize(outcome, Json));
                else WriteText(outcome);
                return outcome.Ok ? 0 : 1;
            default:
                return Usage();
        }
    }

    internal static ConfigRepository OpenRepository(string[] args)
    {
        var repoRoot = DbDataSyncRoot.Resolve(args);
        return new ConfigRepository(Path.Combine(repoRoot, "config"), new GitCommitService(repoRoot), SecretStore.OpenInRepository(repoRoot));
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: dbdatasync script list | script test <name> [--rows <n>] [--repo <path>] [--json]");
        return 1;
    }

    /// <summary>The scripts and the slot each fills; a manifest the strict reader refuses is listed with its reason, not left out.</summary>
    public static IReadOnlyList<ScriptEntry> List(ConfigRepository repository)
    {
        var entries = new List<ScriptEntry>();
        foreach (var name in repository.ListScripts())
        {
            try
            {
                var manifest = repository.LoadScriptManifest(name);
                entries.Add(new(name, manifest.Kind, manifest.Language.ToString(), null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                entries.Add(new(name, "", "", ex.Message));
            }
        }
        return entries;
    }

    /// <summary>Compiles the script and runs it against generated sample input. Never opens a connection.</summary>
    public static async Task<TestOutcome> TestAsync(ConfigRepository repository, string name, int rows)
    {
        ScriptDefinition script;
        try { script = repository.LoadScript(name); }
        catch (FileNotFoundException) { return new(name, "", false, "", [], [], $"Script '{name}' does not exist in {repository.ScriptsDirectory}."); }

        var cache = new ScriptCacheDirectory(Path.Combine(Path.GetTempPath(), "dbdatasync-script-test-cache"));
        var service = new ScriptTestService(repository, new ScriptCompiler(cache, repository.ScriptsDirectory));
        var result = await service.RunAsync(new ScriptTestRequest(script, SampleRows: rows), CancellationToken.None);
        return new(name, script.Manifest.Kind, result.Error is null, result.Source, [.. result.Cases.Select(c => new TestCase(c.Input, c.Output, c.Note))], result.Log, result.Error);
    }

    private static void WriteText(TestOutcome outcome)
    {
        if (outcome.Source.Length > 0) Console.WriteLine($"{outcome.Kind} '{outcome.Name}' - {outcome.Source}");
        foreach (var c in outcome.Cases)
            Console.WriteLine($"  {c.Input}  ->  {c.Output ?? "(none)"}{(c.Note is null ? "" : $"  [{c.Note}]")}");
        foreach (var line in outcome.Log) Console.WriteLine($"  log: {line}");
        if (outcome.Error is not null) Console.Error.WriteLine(outcome.Error);
    }
}
