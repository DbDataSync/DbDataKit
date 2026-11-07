using System.Text.RegularExpressions;

namespace DbDataKit.Runner;

/// <summary>What an artifact's path and hash may look like. A path becomes a place in a run's directory, so anything that could leave it, or that some OS would read differently, is refused.</summary>
public static partial class ArtifactPaths
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex HashPattern();

    /// <summary>The path split into segments; throws <see cref="ArtifactException"/> naming the path and the reason.</summary>
    public static string[] Segments(string path)
    {
        if (string.IsNullOrEmpty(path)) throw Refuse(path, "it is empty");
        if (path.Contains('\\')) throw Refuse(path, "it contains a backslash; paths use '/'");
        if (path[0] == '/') throw Refuse(path, "it is absolute");
        if (path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0])) throw Refuse(path, "it starts with a drive letter");
        var segments = path.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0) throw Refuse(path, "it has an empty segment (a leading, trailing or doubled '/')");
            if (segment is "." or "..") throw Refuse(path, $"it contains '{segment}' and would leave or repeat its directory");
            foreach (var c in segment)
            {
                if (c < ' ' || c == 127) throw Refuse(path, "it contains a control character");
                if (c is '<' or '>' or ':' or '"' or '|' or '?' or '*') throw Refuse(path, $"it contains '{c}', which some systems do not allow in a file name");
            }

            if (segment[^1] is '.' or ' ') throw Refuse(path, $"the segment '{segment}' ends with a dot or a space");
            var dot = segment.IndexOf('.');
            var stem = dot < 0 ? segment : segment[..dot];
            if (Reserved.Contains(stem.TrimEnd(' '))) throw Refuse(path, $"'{stem}' is a reserved device name on Windows");
        }

        return segments;
    }

    /// <summary>Lower-case hex of a SHA-256, or a refusal naming the file.</summary>
    public static string NormalizeHash(string path, string? sha256)
    {
        var lower = sha256?.ToLowerInvariant() ?? string.Empty;
        if (!HashPattern().IsMatch(lower)) throw new ArtifactException($"The artifact '{path}' has the hash '{sha256}', which is not 64 hex digits (a SHA-256).");
        return lower;
    }

    private static ArtifactException Refuse(string? path, string reason) => new($"The artifact path '{path}' is refused: {reason}.");
}
