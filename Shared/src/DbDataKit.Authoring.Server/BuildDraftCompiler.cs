using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Authoring;

namespace DbDataKit.Authoring.Server;

/// <summary>One file <c>project compile</c> makes of a draft, by what it would do to the draft's committed <c>rendered/</c>: <c>added</c>, <c>changed</c>, <c>unchanged</c> or <c>removed</c>. The text comes with an added or changed file (up to <see cref="BuildProjectKind.MaxFileText"/> characters).</summary>
public sealed record DraftCompiledFile(string Path, string Status, string? Content, bool Truncated = false);

/// <summary>What compiling a draft came to: whether the project compiled, the compiler's findings, and the files by status. Nothing in the draft changed.</summary>
public sealed record DraftCompileResult(bool Ok, string? Message, IReadOnlyList<ProjectDiagnostic> Diagnostics, IReadOnlyList<DraftCompiledFile> Files);

/// <summary>What a model is lowered and rendered to: the text of each file the compiler made for it (one per connection), or the findings that stopped it.</summary>
public sealed record DraftLoweredResult(bool Ok, IReadOnlyList<ProjectDiagnostic> Diagnostics, IReadOnlyList<DraftLoweredFile> Files);

public sealed record DraftLoweredFile(string Path, string Content);

/// <summary>What a run on generated rows came to: the findings and the tool's own <c>tables</c> (name, columns, row count, rows, error), passed through.</summary>
public sealed record DraftSampleResult(bool Ok, IReadOnlyList<ProjectDiagnostic> Diagnostics, JsonNode? Data);

/// <summary>
/// What a kind can do with a draft beyond checking it, offline (plan 33 SC20l): compile it (the plan files it would write, shown against the committed ones), lower one model, run models on generated rows. All three run the product's own program in a
/// copy or read only and leave the draft untouched: the files a compile makes are written into the draft by the author, through the file routes, once seen.
/// </summary>
public interface IDraftCompiler
{
    Task<DraftCompileResult> CompileAsync(string root, CancellationToken cancellationToken);

    /// <summary><paramref name="modelPath"/> is a file of the project under <c>models/</c> (checked by the caller with <see cref="BuildProjectKind.IsModelPath"/>).</summary>
    Task<DraftLoweredResult> LowerAsync(string root, string modelPath, CancellationToken cancellationToken);

    Task<DraftSampleResult> SampleAsync(string root, IReadOnlyList<string> modelPaths, int rows, int seed, int limit, CancellationToken cancellationToken);
}

public sealed partial class BuildProjectKind
{
    /// <summary>A rendered file's text is sent up to this many characters; a larger one is listed without it (compile) or cut (lowered).</summary>
    public const int MaxFileText = 1024 * 1024;

    private static readonly string[] SkippedFolders = [".git", "node_modules", "bin", "obj", ".dbdatabuild"];

    /// <summary>Whether <paramref name="path"/> names a file of a project's <c>models/</c> folder in the form the tool takes: relative, <c>/</c> between the parts, no <c>..</c>, so it can never start with <c>-</c> or leave the project.</summary>
    public static bool IsModelPath(string? path) =>
        path is { Length: > 0 and <= 400 } && path.StartsWith("models/", StringComparison.Ordinal) && !path.Contains("..", StringComparison.Ordinal) && !path.Contains('\\') && !path.Contains('\0')
        && (path.EndsWith(".sql", StringComparison.Ordinal) || path.EndsWith(".yml", StringComparison.Ordinal));

    private static void CopyDraft(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
            if (new FileInfo(file).LinkTarget is null) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var folder in Directory.EnumerateDirectories(from))
        {
            var name = Path.GetFileName(folder);
            if (SkippedFolders.Contains(name, StringComparer.Ordinal) || new DirectoryInfo(folder).LinkTarget is not null) continue;
            CopyDraft(folder, Path.Combine(to, name));
        }
    }

    private async Task<(JsonDocument? Document, int Exit, string Error)> RunToolAsync(IReadOnlyList<string> arguments, string folder, CancellationToken cancellationToken)
    {
        var result = await tool.RunAsync(arguments, folder, cancellationToken);
        try { return (JsonDocument.Parse(result.Output), result.Exit, result.Error); }
        catch (JsonException) { return (null, result.Exit, result.Error); }
    }

    private static ProjectDiagnostic Failed(int exit, string error) => new("error", "DBD-WS-BUILD-FAILED", $"dbdatabuild stopped with exit code {exit}: {error.Trim()}");

    private static JsonElement DataOf(JsonDocument document) => document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object ? data : document.RootElement;

    private static List<ProjectDiagnostic> DiagnosticsOf(JsonDocument document, int exit, string error)
    {
        var found = ReadDiagnostics(document.RootElement);
        if (exit is not (0 or 1) && !found.Any(d => d.IsError)) found.Add(Failed(exit, error));
        return found;
    }

    public async Task<DraftCompileResult> CompileAsync(string root, CancellationToken cancellationToken)
    {
        if (tool.Find() is null) throw new ProjectKindUnavailableException(BuildTool.Missing);
        var copy = Path.Combine(Path.GetTempPath(), "ddw-compile-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyDraft(root, copy);
            var before = RenderedFiles(Path.Combine(root, "rendered"));
            var (document, exit, error) = await RunToolAsync(["project", "compile", "--project", copy], copy, cancellationToken);
            using (document)
            {
                if (document is null) return new(false, $"dbdatabuild did not answer with a document (exit code {exit}): {error.Trim()}", [Failed(exit, error)], []);
                var diagnostics = DiagnosticsOf(document, exit, error);
                if (diagnostics.Any(d => d.IsError)) return new(false, "Nothing was compiled: the project has errors.", diagnostics, []);
                var after = RenderedFiles(Path.Combine(copy, "rendered"));
                var files = new List<DraftCompiledFile>();
                foreach (var (path, text) in after.OrderBy(f => f.Key, StringComparer.Ordinal))
                {
                    var status = !before.TryGetValue(path, out var was) ? "added" : was == text ? "unchanged" : "changed";
                    var carries = status is "added" or "changed";
                    var big = text.Length > MaxFileText;
                    files.Add(new($"rendered/{path}", status, carries && !big ? text : null, carries && big));
                }
                foreach (var path in before.Keys.Where(k => !after.ContainsKey(k)).Order(StringComparer.Ordinal)) files.Add(new($"rendered/{path}", "removed", null));
                var written = files.Count(f => f.Status is "added" or "changed");
                var removed = files.Count(f => f.Status == "removed");
                return new(true, written == 0 && removed == 0 ? $"{after.Count} rendered file(s), all as committed." : $"{after.Count} rendered file(s): {written} would be written, {removed} removed.", diagnostics, files);
            }
        }
        finally
        {
            try { if (Directory.Exists(copy)) Directory.Delete(copy, true); } catch (IOException) { }
        }
    }

    /// <summary>The text of every file under a <c>rendered/</c> folder by its path below it; none when the folder is not there.</summary>
    private static Dictionary<string, string> RenderedFiles(string folder)
    {
        var all = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(folder)) return all;
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            if (new FileInfo(file).LinkTarget is null) all[Path.GetRelativePath(folder, file).Replace('\\', '/')] = File.ReadAllText(file);
        return all;
    }

    public async Task<DraftLoweredResult> LowerAsync(string root, string modelPath, CancellationToken cancellationToken)
    {
        if (tool.Find() is null) throw new ProjectKindUnavailableException(BuildTool.Missing);
        var (document, exit, error) = await RunToolAsync(["project", "compile", modelPath, "--project", root, "--content"], root, cancellationToken);
        using (document)
        {
            if (document is null) return new(false, [Failed(exit, error)], []);
            var diagnostics = DiagnosticsOf(document, exit, error);
            var files = new List<DraftLoweredFile>();
            if (DataOf(document).TryGetProperty("files", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var f in list.EnumerateArray())
                    if (Text(f, "path") is { } path && Text(f, "content") is { } content) files.Add(new(path, content.Length > MaxFileText ? content[..MaxFileText] : content));
            return new(!diagnostics.Any(d => d.IsError), diagnostics, files);
        }
    }

    public async Task<DraftSampleResult> SampleAsync(string root, IReadOnlyList<string> modelPaths, int rows, int seed, int limit, CancellationToken cancellationToken)
    {
        if (tool.Find() is null) throw new ProjectKindUnavailableException(BuildTool.Missing);
        var arguments = new List<string> { "project", "sample" };
        arguments.AddRange(modelPaths);
        arguments.AddRange(["--project", root, "--rows", rows.ToString(CultureInfo.InvariantCulture), "--seed", seed.ToString(CultureInfo.InvariantCulture), "--limit", limit.ToString(CultureInfo.InvariantCulture)]);
        var (document, exit, error) = await RunToolAsync(arguments, root, cancellationToken);
        using (document)
        {
            if (document is null) return new(false, [Failed(exit, error)], null);
            var diagnostics = DiagnosticsOf(document, exit, error);
            JsonNode? data = DataOf(document).TryGetProperty("tables", out var tables) ? new JsonObject { ["tables"] = JsonNode.Parse(tables.GetRawText()) } : null;
            return new(!diagnostics.Any(d => d.IsError) && data is not null, diagnostics, data);
        }
    }
}
