using System.Text;

namespace DbDataKit.Authoring;

/// <summary>A file of a workspace's folder: where it is (relative, <c>/</c> between the parts) and how big.</summary>
public sealed record WorkspaceFile(string Path, long Size);

/// <summary>Why a file call was refused, in a sentence a person can read; <see cref="Status"/> is the HTTP status the route answers with.</summary>
public sealed class WorkspaceFileException(string message, int status) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// The file commands of a workspace (plan 22a, GP35a): list, read, write, move and delete a file in a draft's folder, the same for every kind of project. A path is relative to the folder, written with <c>/</c>, and is
/// refused when it could leave the folder or touch git's own files: absolute, a drive, a backslash, a control character, an empty, <c>.</c> or <c>..</c> part, any part named <c>.git</c>, or a place reached through a
/// symbolic link. Files are text (UTF-8, no NUL) of at most <see cref="MaxFileBytes"/>, and a workspace holds at most <see cref="MaxFiles"/> of them. Nothing here commits: a commit takes what is in the folder.
/// </summary>
public static class WorkspaceFiles
{
    public const int MaxFileBytes = 1024 * 1024;
    public const int MaxFiles = 5000;
    public const int MaxPathChars = 240;
    public const int MaxDepth = 24;

    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>The path as it is kept (checked), or a <see cref="WorkspaceFileException"/> naming what is wrong with it. Symbolic links are checked against the folder by <see cref="Resolve"/>.</summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrEmpty(path)) throw new WorkspaceFileException("Give the file's path.", 400);
        if (path.Length > MaxPathChars) throw new WorkspaceFileException($"A path is at most {MaxPathChars} characters.", 400);
        if (path.Any(char.IsControl)) throw new WorkspaceFileException("A path has no control characters.", 400);
        if (path.Contains('\\') || path.Contains(':')) throw new WorkspaceFileException("A path is written with '/' and has no drive or ':' in it.", 400);
        if (path.StartsWith('/')) throw new WorkspaceFileException("A path is relative to the workspace, so it does not start with '/'.", 400);
        var parts = path.Split('/');
        if (parts.Length > MaxDepth) throw new WorkspaceFileException($"A path is at most {MaxDepth} folders deep.", 400);
        foreach (var part in parts)
        {
            if (part.Length == 0) throw new WorkspaceFileException("A path has no empty part ('//' or a trailing '/').", 400);
            if (part is "." or "..") throw new WorkspaceFileException("A path stays inside the workspace: no '.' or '..' parts.", 400);
            if (part.TrimEnd(' ', '.').Equals(".git", StringComparison.OrdinalIgnoreCase)) throw new WorkspaceFileException("Git's own files (.git) are not files of the workspace.", 403);
            if (part.EndsWith(' ') || part.EndsWith('.')) throw new WorkspaceFileException("A part of a path does not end with a space or a '.'.", 400);
        }
        return string.Join('/', parts);
    }

    /// <summary>The full path of a (checked) relative path in <paramref name="root"/>, refused when it, or a folder on the way to it, is a symbolic link (what is behind one could be outside the workspace).</summary>
    public static string Resolve(string root, string path)
    {
        var relative = Normalize(path);
        var rootFull = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(rootFull, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new WorkspaceFileException("A path stays inside the workspace.", 400);
        var at = rootFull;
        foreach (var part in relative.Split('/'))
        {
            at = Path.Combine(at, part);
            FileSystemInfo info = Directory.Exists(at) ? new DirectoryInfo(at) : new FileInfo(at);
            if (info.LinkTarget is not null) throw new WorkspaceFileException($"'{relative}' goes through a symbolic link, which a workspace does not follow.", 403);
        }
        return full;
    }

    /// <summary>Every file of the folder except git's, in path order. A symbolic link is not listed.</summary>
    public static IReadOnlyList<WorkspaceFile> List(string root)
    {
        var rootFull = Path.GetFullPath(root);
        var found = new List<WorkspaceFile>();
        void Walk(string folder)
        {
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
            {
                if (entry.LinkTarget is not null) continue;
                if (entry.Name.Equals(".git", StringComparison.OrdinalIgnoreCase)) continue;
                if (entry is DirectoryInfo directory) Walk(directory.FullName);
                else if (entry is FileInfo file)
                {
                    if (found.Count >= MaxFiles) return;
                    found.Add(new WorkspaceFile(Path.GetRelativePath(rootFull, file.FullName).Replace(Path.DirectorySeparatorChar, '/'), file.Length));
                }
            }
        }
        Walk(rootFull);
        return [.. found.OrderBy(f => f.Path, StringComparer.Ordinal)];
    }

    /// <summary>The text of a file, or a refusal: no such file (404), too big (413), or not text (415).</summary>
    public static string Read(string root, string path)
    {
        var full = Resolve(root, path);
        if (!File.Exists(full)) throw new WorkspaceFileException($"There is no file '{Normalize(path)}' in this workspace.", 404);
        var length = new FileInfo(full).Length;
        if (length > MaxFileBytes) throw new WorkspaceFileException($"'{Normalize(path)}' is {length} bytes; a file is shown and edited up to {MaxFileBytes}.", 413);
        var bytes = File.ReadAllBytes(full);
        if (Array.IndexOf(bytes, (byte)0) >= 0) throw new WorkspaceFileException($"'{Normalize(path)}' is not a text file.", 415);
        try { return Utf8.GetString(bytes); }
        catch (DecoderFallbackException) { throw new WorkspaceFileException($"'{Normalize(path)}' is not UTF-8 text.", 415); }
    }

    /// <summary>Writes (makes or replaces) a text file, making its folders. Returns whether the file is new.</summary>
    public static bool Write(string root, string path, string content)
    {
        var full = Resolve(root, path);
        if (content.Contains('\0')) throw new WorkspaceFileException("A file is text: no NUL characters.", 415);
        if (Utf8.GetByteCount(content) > MaxFileBytes) throw new WorkspaceFileException($"A file is at most {MaxFileBytes} bytes.", 413);
        if (Directory.Exists(full)) throw new WorkspaceFileException($"'{Normalize(path)}' is a folder.", 409);
        var isNew = !File.Exists(full);
        if (isNew && List(root).Count >= MaxFiles) throw new WorkspaceFileException($"A workspace holds at most {MaxFiles} files.", 413);
        // a file where a folder is needed: the part of the path that is a file is named
        var rootFull = Path.GetFullPath(root);
        for (var parent = Path.GetDirectoryName(full); parent is not null && parent.Length > rootFull.Length; parent = Path.GetDirectoryName(parent))
            if (File.Exists(parent)) throw new WorkspaceFileException($"'{Path.GetRelativePath(rootFull, parent).Replace(Path.DirectorySeparatorChar, '/')}' is a file, so nothing can be made inside it.", 409);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, Utf8.GetBytes(content));
        return isNew;
    }

    /// <summary>Moves a file to a path that is not taken.</summary>
    public static void Move(string root, string from, string to)
    {
        var source = Resolve(root, from);
        var target = Resolve(root, to);
        if (!File.Exists(source)) throw new WorkspaceFileException($"There is no file '{Normalize(from)}' in this workspace.", 404);
        if (File.Exists(target) || Directory.Exists(target)) throw new WorkspaceFileException($"'{Normalize(to)}' is taken; delete or move it first.", 409);
        var rootFull = Path.GetFullPath(root);
        for (var parent = Path.GetDirectoryName(target); parent is not null && parent.Length > rootFull.Length; parent = Path.GetDirectoryName(parent))
            if (File.Exists(parent)) throw new WorkspaceFileException($"'{Path.GetRelativePath(rootFull, parent).Replace(Path.DirectorySeparatorChar, '/')}' is a file, so nothing can be moved inside it.", 409);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(source, target);
        Prune(root, Path.GetDirectoryName(source)!);
    }

    /// <summary>Deletes a file (and the folders it leaves empty).</summary>
    public static void Delete(string root, string path)
    {
        var full = Resolve(root, path);
        if (!File.Exists(full)) throw new WorkspaceFileException($"There is no file '{Normalize(path)}' in this workspace.", 404);
        File.SetAttributes(full, FileAttributes.Normal);
        File.Delete(full);
        Prune(root, Path.GetDirectoryName(full)!);
    }

    private static void Prune(string root, string folder)
    {
        var rootFull = Path.GetFullPath(root);
        while (folder.Length > rootFull.Length && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
        {
            Directory.Delete(folder);
            folder = Path.GetDirectoryName(folder)!;
        }
    }
}
