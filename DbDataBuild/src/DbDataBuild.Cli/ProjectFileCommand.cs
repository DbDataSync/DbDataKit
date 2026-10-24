using System.Text;
using DbDataBuild.Core;
using DbDataBuild.State;

namespace DbDataBuild.Cli;

/// <summary>
/// `dbdatabuild project file list|read|write|move|delete` (plan 13, W01). The one way a console or an agent reads and changes the files of a project, so that every interface is a client of the command surface. Confined
/// to the project tree: a path that climbs out, an absolute path, a path through a link that leaves the tree and the repository's own `.git` are refused (DDB-470). A change names the version it is based on by the file's
/// content hash and a different current content is refused (DDB-471), so concurrent edits never overwrite each other. Files are text (UTF-8, LF, no BOM, at most <see cref="MaxBytes"/>); the commands do not parse them,
/// so what they write is checked by the commands that load the project (`project compile`) and by the seeded mutation tests of those loaders.
/// </summary>
internal static class ProjectFileCommand
{
    public const int MaxBytes = 1024 * 1024;

    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>Folders `list` does not descend into: the repository's own, and what the tool or the build leaves behind.</summary>
    private static readonly string[] Hidden = [".git", ".dbdatabuild", "node_modules", "bin", "obj"];

    private static void Banner(CommandSpec spec, TextWriter output) =>
        output.WriteLine($"{ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}  |  {spec.Marks}  |  connection: none");

    public static int List(CommandSpec spec, string root, string? dir, TextWriter output, TextWriter error)
    {
        Banner(spec, output);
        if (!TryResolve(root, dir ?? "", allowRoot: true, error, out var folder, out _)) return CliApp.ExitFindings;
        if (!Directory.Exists(folder)) { error.Diag(Unusable(dir ?? ".", "it is not a folder of the project")); return CliApp.ExitFindings; }
        var rootFull = Path.GetFullPath(root);
        var files = Walk(folder).Order(StringComparer.Ordinal).Select(path => (Path: Relative(rootFull, path), Size: new FileInfo(path).Length, Hash: HashOf(path))).ToList();
        output.Payload("files", files.Select(f => new { path = f.Path, size = f.Size, hash = f.Hash }).ToList());
        foreach (var f in files) output.WriteLine($"{f.Hash[..12]}  {f.Size,9}  {f.Path}");
        output.WriteLine($"\n{files.Count} file(s).");
        return CliApp.ExitOk;
    }

    public static int Read(CommandSpec spec, string root, string path, TextWriter output, TextWriter error)
    {
        Banner(spec, output);
        if (!TryResolve(root, path, allowRoot: false, error, out var full, out var rel)) return CliApp.ExitFindings;
        if (!TryReadText(full, rel, error, out var text)) return CliApp.ExitFindings;
        var hash = Hashing.ScriptHash(text);
        output.Payload("path", rel);
        output.Payload("hash", hash);
        output.Payload("content", text);
        output.WriteLine($"hash: {hash}\n");
        output.Write(text);
        return CliApp.ExitOk;
    }

    public static int Write(CommandSpec spec, string root, string path, string content, string? expectHash, bool create, TextWriter output, TextWriter error)
    {
        Banner(spec, output);
        if (expectHash is null == !create) { error.WriteLine("Name the version the change is based on: --expect-hash <hash> for an existing file, or --create for a new one (not both)."); return CliApp.ExitUsage; }
        if (!TryResolve(root, path, allowRoot: false, error, out var full, out var rel)) return CliApp.ExitFindings;
        content = content.Replace("\r\n", "\n");
        if (Utf8.GetByteCount(content) > MaxBytes) { error.Diag(Unusable(rel, $"the new content is larger than {MaxBytes / 1024} KiB")); return CliApp.ExitFindings; }

        if (create)
        {
            if (File.Exists(full) || Directory.Exists(full)) { error.Diag(Stale(rel, "--create, but the file exists already")); return CliApp.ExitFindings; }
            if (!EnsureParent(root, full, rel, error)) return CliApp.ExitFindings;
        }
        else
        {
            if (!TryReadText(full, rel, error, out var current)) return CliApp.ExitFindings;
            if (!string.Equals(Hashing.ScriptHash(current), expectHash, StringComparison.OrdinalIgnoreCase)) { error.Diag(Stale(rel, $"the file's hash is {Hashing.ScriptHash(current)}, not {expectHash}")); return CliApp.ExitFindings; }
        }
        WriteAtomically(full, content);
        var hash = Hashing.ScriptHash(content);
        output.Payload("path", rel);
        output.Payload("hash", hash);
        output.WriteLine($"Wrote {rel} (hash {hash}).");
        return CliApp.ExitOk;
    }

    public static int Move(CommandSpec spec, string root, string from, string to, string? expectHash, TextWriter output, TextWriter error)
    {
        Banner(spec, output);
        if (expectHash is null) { error.WriteLine("Name the version being moved: --expect-hash <hash>."); return CliApp.ExitUsage; }
        if (!TryResolve(root, from, allowRoot: false, error, out var source, out var fromRel) || !TryResolve(root, to, allowRoot: false, error, out var target, out var toRel)) return CliApp.ExitFindings;
        if (!TryReadText(source, fromRel, error, out var current)) return CliApp.ExitFindings;
        if (!string.Equals(Hashing.ScriptHash(current), expectHash, StringComparison.OrdinalIgnoreCase)) { error.Diag(Stale(fromRel, $"the file's hash is {Hashing.ScriptHash(current)}, not {expectHash}")); return CliApp.ExitFindings; }
        if (File.Exists(target) || Directory.Exists(target)) { error.Diag(Unusable(toRel, "the destination exists already")); return CliApp.ExitFindings; }
        if (!EnsureParent(root, target, toRel, error)) return CliApp.ExitFindings;
        File.Move(source, target);
        output.Payload("path", toRel);
        output.Payload("from", fromRel);
        output.Payload("hash", Hashing.ScriptHash(current));
        output.WriteLine($"Moved {fromRel} to {toRel}.");
        return CliApp.ExitOk;
    }

    public static int Delete(CommandSpec spec, string root, string path, string? expectHash, TextWriter output, TextWriter error)
    {
        Banner(spec, output);
        if (expectHash is null) { error.WriteLine("Name the version being deleted: --expect-hash <hash>."); return CliApp.ExitUsage; }
        if (!TryResolve(root, path, allowRoot: false, error, out var full, out var rel)) return CliApp.ExitFindings;
        if (!TryReadText(full, rel, error, out var current)) return CliApp.ExitFindings;
        if (!string.Equals(Hashing.ScriptHash(current), expectHash, StringComparison.OrdinalIgnoreCase)) { error.Diag(Stale(rel, $"the file's hash is {Hashing.ScriptHash(current)}, not {expectHash}")); return CliApp.ExitFindings; }
        File.Delete(full);
        output.Payload("path", rel);
        output.WriteLine($"Deleted {rel}.");
        return CliApp.ExitOk;
    }

    /// <summary>
    /// The full path of a project-relative path, or a DDB-470 on <paramref name="error"/>. The check is on the resolved path, so `a/../../x`, a drive or a rooted path and a link that points out all fail the same test: the
    /// path, with every link in it resolved, must be inside the project root and not inside `.git`.
    /// </summary>
    internal static bool TryResolve(string root, string path, bool allowRoot, TextWriter error, out string full, out string relative)
    {
        full = ""; relative = path;
        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        bool Refuse(string why) { error.Diag(new Diagnostic(DiagnosticCatalog.ProjectFilePathRefused, new(path, 0, 0), why)); return false; }
        if (path.Contains('\0')) return Refuse("the path has a NUL character");
        var text = path.Replace('\\', '/');
        if (Path.IsPathRooted(text) || Path.IsPathRooted(path) || (text.Length >= 2 && text[1] == ':')) return Refuse("the path is absolute");
        var parts = text.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(p => p != ".").ToArray();
        if (parts.Any(p => p == "..")) return Refuse("the path climbs out of the project with `..`");
        if (parts.Any(p => string.Equals(p, ".git", StringComparison.OrdinalIgnoreCase))) return Refuse("the path is inside the repository's own .git folder");
        if (parts.Length == 0 && !allowRoot) return Refuse("the path names the project root, not a file");
        var combined = Path.GetFullPath(Path.Combine(rootFull, string.Join('/', parts)));
        if (combined != rootFull && !combined.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return Refuse("the path is outside the project");
        // a link anywhere on the way may point out of the tree: follow each existing part and test where it lands
        var realRoot = Path.TrimEndingDirectorySeparator(new DirectoryInfo(rootFull).ResolveLinkTarget(true)?.FullName ?? rootFull);
        var current = realRoot;
        foreach (var part in parts)
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (!info.Exists && info.LinkTarget is null) break;
            if (info.LinkTarget is not null) current = Path.TrimEndingDirectorySeparator(info.ResolveLinkTarget(true)?.FullName ?? current);
            if (current != realRoot && !current.StartsWith(realRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return Refuse("the path goes through a link that leaves the project");
        }
        full = combined;
        relative = parts.Length == 0 ? "." : string.Join('/', parts);
        return true;
    }

    private static bool EnsureParent(string root, string full, string rel, TextWriter error)
    {
        var parent = Path.GetDirectoryName(full)!;
        if (!TryResolve(root, Path.GetRelativePath(root, parent), allowRoot: true, error, out _, out _)) return false;
        Directory.CreateDirectory(parent);
        return true;
    }

    private static bool TryReadText(string full, string rel, TextWriter error, out string text)
    {
        text = "";
        if (!File.Exists(full)) { error.Diag(Unusable(rel, "there is no such file")); return false; }
        var length = new FileInfo(full).Length;
        if (length > MaxBytes) { error.Diag(Unusable(rel, $"the file is {length} bytes, larger than {MaxBytes / 1024} KiB")); return false; }
        try { text = Utf8.GetString(File.ReadAllBytes(full)).Replace("\r\n", "\n"); }
        catch (DecoderFallbackException) { error.Diag(Unusable(rel, "the file is not UTF-8 text")); return false; }
        if (text.Contains('\0')) { error.Diag(Unusable(rel, "the file is not text (it has NUL characters)")); return false; }
        return true;
    }

    private static string HashOf(string path)
    {
        try { return Hashing.ScriptHash(Utf8.GetString(File.ReadAllBytes(path))); }
        catch (DecoderFallbackException) { return Hashing.Sha256Hex(File.ReadAllText(path, Encoding.Latin1)); }
    }

    private static IEnumerable<string> Walk(string folder)
    {
        foreach (var file in Directory.EnumerateFiles(folder)) if (new FileInfo(file).LinkTarget is null) yield return file;
        foreach (var sub in Directory.EnumerateDirectories(folder))
        {
            if (Hidden.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase) || new DirectoryInfo(sub).LinkTarget is not null) continue;
            foreach (var f in Walk(sub)) yield return f;
        }
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static void WriteAtomically(string full, string content)
    {
        var temp = full + ".ddb-" + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllBytes(temp, new UTF8Encoding(false).GetBytes(content));
        File.Move(temp, full, overwrite: true);
    }

    private static Diagnostic Stale(string rel, string found) => new(DiagnosticCatalog.ProjectFileHashStale, new(rel, 0, 0), found);

    private static Diagnostic Unusable(string rel, string found) => new(DiagnosticCatalog.ProjectFileUnusable, new(rel, 0, 0), found);
}
