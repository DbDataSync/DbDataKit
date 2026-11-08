using DbDataBuild.Core;
using DbDataBuild.Planning;

namespace DbDataBuild.Cli;

/// <summary>
/// The files a compiled refresh names (`loads.yml` names each load script, resolver and hook script by path and hash), checked against those hashes. A refresh runs nothing when one disagrees (DDB-448), and `project publish` refuses to
/// send a project whose committed files disagree with each other, by the same check.
/// </summary>
internal static class RefreshFiles
{
    /// <summary>
    /// Where a file the compiled files name is: hook scripts (`hooks/...`) and files with no folder are beside the project file, everything else is under `rendered/`.
    /// </summary>
    public static string PathOf(string root, string file) =>
        Path.GetFullPath(Path.Combine(file.StartsWith("hooks/", StringComparison.Ordinal) || !file.Contains('/') ? root : Path.Combine(root, RenderCommand.RenderedDir), file));

    /// <summary>The text of the file when it exists and has the hash `loads.yml` recorded; otherwise null, with a DDB-448 diagnostic added to <paramref name="changed"/>.</summary>
    public static string? Read(string root, string file, string hash, string what, List<Diagnostic> changed)
    {
        var path = PathOf(root, file);
        if (!File.Exists(path)) { changed.Add(new Diagnostic(DiagnosticCatalog.RefreshFileChanged, new(file, 0, 0), $"The {what} `{file}`, which the compiled refresh files name, does not exist.")); return null; }
        var text = File.ReadAllText(path);
        if (Hashing.ScriptHash(text) != hash) { changed.Add(new Diagnostic(DiagnosticCatalog.RefreshFileChanged, new(file, 0, 0), $"The {what} `{file}` is not the one `{LoadsDocument.FileName}` was compiled from (its content has another hash).")); return null; }
        return text;
    }

    /// <summary>Every file one load of `loads.yml` names (script, resolver, hook scripts), as (path in the compiled files, hash, what it is).</summary>
    public static IEnumerable<(string File, string Hash, string What)> Named(LoadPlanEntry entry)
    {
        yield return (entry.File, entry.FileHash, "load script");
        if (entry.ResolverFile != null) yield return (entry.ResolverFile, entry.ResolverHash ?? "", "resolver");
        foreach (var h in entry.Before.Concat(entry.After)) yield return (h.File, h.FileHash, "hook script");
    }
}
