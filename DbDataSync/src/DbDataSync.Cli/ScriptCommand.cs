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
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0) return Usage();
        var repoRoot = DbDataSyncRoot.Resolve(args);
        var repository = new ConfigRepository(Path.Combine(repoRoot, "config"), new GitCommitService(repoRoot), SecretStore.Open(repoRoot));
        return args[0].ToLowerInvariant() switch
        {
            "list" => List(repository),
            "test" => await TestAsync(repository, args[1..]),
            _ => Usage(),
        };
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: dbdatasync script list | script test <name> [--rows <n>] [--repo <path>]");
        return 1;
    }

    private static int List(ConfigRepository repository)
    {
        foreach (var name in repository.ListScripts())
            Console.WriteLine($"{name}\t{repository.LoadScriptManifest(name).Kind}");
        return 0;
    }

    private static async Task<int> TestAsync(ConfigRepository repository, string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal)) return Usage();
        var name = args[0];
        var rows = int.TryParse(CliOptions.Read(args, "--rows"), out var n) ? n : 5;

        ScriptDefinition script;
        try { script = repository.LoadScript(name); }
        catch (FileNotFoundException)
        {
            Console.Error.WriteLine($"Script '{name}' does not exist in {repository.ScriptsDirectory}.");
            return 1;
        }

        var cache = new ScriptCacheDirectory(Path.Combine(Path.GetTempPath(), "dbdatasync-script-test-cache"));
        var service = new ScriptTestService(repository, new ScriptCompiler(cache, repository.ScriptsDirectory));
        var result = await service.RunAsync(new ScriptTestRequest(script, SampleRows: rows), CancellationToken.None);

        Console.WriteLine($"{script.Manifest.Kind} '{name}' - {result.Source}");
        foreach (var c in result.Cases)
            Console.WriteLine($"  {c.Input}  ->  {c.Output ?? "(none)"}{(c.Note is null ? "" : $"  [{c.Note}]")}");
        foreach (var line in result.Log) Console.WriteLine($"  log: {line}");
        if (result.Error is not null)
        {
            Console.Error.WriteLine(result.Error);
            return 1;
        }
        return 0;
    }
}
