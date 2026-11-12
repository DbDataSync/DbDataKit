using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DbDataKit.Authoring;

namespace DbDataKit.Authoring.Server;

/// <summary>What one of Sync's own offline verbs came to for a draft: whether it ran, the findings that stopped it (the program's words, or that it did not answer) and the document it printed, passed through as the tool wrote it.</summary>
public sealed record DraftToolResult(bool Ok, IReadOnlyList<ProjectDiagnostic> Diagnostics, JsonNode? Data);

/// <summary>
/// What a Sync draft can do beyond being checked, offline (plan 33 SC22k, SC22j): list its scripts, compile and test one against generated input (<c>script test</c>), and plan the provisioning of a replication from the catalog cache
/// (<c>provisioning plan --from-cache</c>). Each runs the product's own program on a copy of the draft in a child process with a cleared environment and a time limit and leaves the draft untouched.
/// </summary>
public interface ISyncDraftTools
{
    Task<DraftToolResult> ScriptsAsync(string root, CancellationToken cancellationToken);

    Task<DraftToolResult> ScriptTestAsync(string root, string name, int rows, CancellationToken cancellationToken);

    /// <summary>The plan of a replication's source and target tables from the draft's <c>catalog-cache.yaml</c> (plan 33 SC22j), offline: <c>provisioning plan --from-cache</c>.</summary>
    Task<DraftToolResult> ProvisioningPlanAsync(string root, string replication, CancellationToken cancellationToken);
}

public sealed partial class SyncProjectKind : ISyncDraftTools
{
    /// <summary>The folders a copy of the draft leaves out: what a Sync server keeps beside the files and a draft never has (a secret store, state, the compiled-script cache).</summary>
    private static readonly string[] CopySkipped = ["secrets", "state", "script-cache", "lib"];

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.NonBacktracking)]
    private static partial Regex NamePattern();

    /// <summary>Whether <paramref name="name"/> can be the name of a script or a replication in the form the program takes: it names a file under <c>config/</c>, so it can never start with <c>-</c>, hold a separator or leave the project.</summary>
    public static bool IsName(string? name) => name is not null && NamePattern().IsMatch(name) && !name.Contains("..", StringComparison.Ordinal);

    private async Task<DraftToolResult> RunJsonAsync(string root, IReadOnlyList<string> verb, CancellationToken cancellationToken)
    {
        if (tool.Find() is null) throw new ProjectKindUnavailableException(SyncTool.Missing);
        var copy = Path.Combine(Path.GetTempPath(), "ddw-sync-" + Guid.NewGuid().ToString("N"));
        try
        {
            BuildProjectKind.CopyDraft(root, copy, CopySkipped);
            Directory.CreateDirectory(Path.Combine(copy, "config"));
            List<string> arguments = [.. verb, "--json", "--repo", copy];
            var result = await tool.RunAsync(arguments, copy, cancellationToken);
            JsonNode? data = null;
            try { data = JsonNode.Parse(result.Output.Replace(copy, ".", StringComparison.Ordinal)); }   // the copy's own path is no business of a screen
            catch (JsonException) { /* the program did not print a document: its words are the finding */ }
            if (data is JsonObject)
                return new(result.Exit is 0 or 1, result.Exit is 0 or 1 ? [] : [Failed(result.Exit, result.Error.Replace(copy, ".", StringComparison.Ordinal))], data);
            var error = result.Error.Replace(copy, ".", StringComparison.Ordinal);
            var said = FindingsOf(copy, error, "DBD-WS-SYNC");
            return new(false, said.Count > 0 ? said : [Failed(result.Exit, error)], null);
        }
        finally
        {
            try { if (Directory.Exists(copy)) Directory.Delete(copy, true); } catch (IOException) { }
        }
    }

    private static ProjectDiagnostic Failed(int exit, string error) => new("error", "DBD-WS-SYNC-FAILED", $"dbdatasync stopped with exit code {exit}: {error.Trim()}");

    public Task<DraftToolResult> ScriptsAsync(string root, CancellationToken cancellationToken) => RunJsonAsync(root, ["script", "list"], cancellationToken);

    public Task<DraftToolResult> ScriptTestAsync(string root, string name, int rows, CancellationToken cancellationToken) =>
        RunJsonAsync(root, ["script", "test", name, "--rows", rows.ToString(CultureInfo.InvariantCulture)], cancellationToken);

    public Task<DraftToolResult> ProvisioningPlanAsync(string root, string replication, CancellationToken cancellationToken) =>
        RunJsonAsync(root, ["provisioning", "plan", replication, "--from-cache"], cancellationToken);
}
