using System.Text.RegularExpressions;

namespace DbDataKit.Apps.Bundling;

/// <summary>
/// Finds Bun macros in an app's source: an import with the attribute <c>with { type: "macro" }</c> (or the older <c>assert</c>). Bun runs a macro's code while it bundles, so a
/// build would run the app's own code on the machine that builds it; an app build refuses a source that has one before Bun starts, and the Workspace's check of a draft shows the
/// same finding. A macro inside a package of <c>node_modules</c> is refused by Bun itself ("macros cannot be run from node_modules"), so only the app's own files are read (every
/// script file, hidden folders included, <c>node_modules</c> and <c>.git</c> left out). This check is what stops a macro: the Bun that <c>bun.pin</c> names (1.3.14) accepts
/// <c>bun build --no-macros</c> and still runs it.
/// </summary>
public static partial class BunMacros
{
    /// <summary>The diagnostic code of a macro import, in a draft's check.</summary>
    public const string Code = "DBD-APP-028";

    private static readonly string[] ScriptExtensions = [".ts", ".tsx", ".mts", ".cts", ".js", ".jsx", ".mjs", ".cjs"];

    /// <summary>Each macro import of the source folder, with its file (relative, <c>/</c>-separated) and line. Empty when there is none.</summary>
    public static IReadOnlyList<AppPackageViolation> Find(string sourceDirectory)
    {
        var root = Path.GetFullPath(sourceDirectory);
        var found = new List<AppPackageViolation>();
        foreach (var path in Files(root))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            found.AddRange(Find(relative, File.ReadAllText(path)));
        }
        return found;
    }

    /// <summary>Each macro import of one file's text.</summary>
    public static IReadOnlyList<AppPackageViolation> Find(string file, string text)
    {
        var found = new List<AppPackageViolation>();
        var named = new List<(int Start, int End)>();
        foreach (Match m in MacroImport().Matches(text))
        {
            var at = m.Groups["attr"].Index;
            named.Add((at, at + m.Groups["attr"].Length));
            if (!InComment(text, at)) found.Add(Violation(file, LineOf(text, at), m.Groups["s"].Value));
        }
        // an attribute the pattern above did not tie to a specifier (a form it does not know) is still refused, without the specifier
        foreach (Match m in MacroAttribute().Matches(text))
            if (!named.Any(n => m.Index >= n.Start && m.Index < n.End) && !InComment(text, m.Index)) found.Add(Violation(file, LineOf(text, m.Index), null));
        return found.OrderBy(v => v.Line).ToList();
    }

    private static AppPackageViolation Violation(string file, int line, string? specifier) =>
        new(file, line, specifier ?? "", $"{file}:{line}: {(specifier is null ? "has an import" : $"imports '{specifier}'")} with {{ type: \"macro\" }}, a Bun macro. Bun would run its code while it builds the app, and an app build does not run the app's code. Import it without the \"macro\" attribute, so it runs in the browser.");

    private static int LineOf(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index; i++) if (text[i] == '\n') line++;
        return line;
    }

    /// <summary>Whether the line holding <paramref name="index"/> is a comment line: it starts with <c>//</c>, <c>/*</c> or a <c>*</c> continuation.</summary>
    private static bool InComment(string text, int index)
    {
        var start = text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        var trimmed = text[start..index].TrimStart();
        return trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("/*", StringComparison.Ordinal) || trimmed.StartsWith('*');
    }

    private static IEnumerable<string> Files(string root)
    {
        var pending = new Stack<string>([root]);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileName(entry);
                if (name is "node_modules" or ".git") continue;
                if (Directory.Exists(entry)) { if (!new FileInfo(entry).Attributes.HasFlag(FileAttributes.ReparsePoint)) pending.Push(entry); }
                else if (ScriptExtensions.Contains(Path.GetExtension(entry), StringComparer.OrdinalIgnoreCase)) yield return entry;
            }
        }
    }

    // `import { x } from './m.ts' with { type: "macro" }`, `import './m.ts' assert { type: 'macro' }`, the clause on the next line, other keys beside `type`
    [GeneratedRegex(@"(?:\bfrom|\bimport)\s*(?<q>['""])(?<s>[^'""\r\n]+)\k<q>\s*(?<attr>\b(?:with|assert)\s*\{[^{}]*?['""]?\btype['""]?\s*:\s*(?<v>['""])macro\k<v>)")]
    private static partial Regex MacroImport();

    [GeneratedRegex(@"\b(?:with|assert)\s*:?\s*\{[^{}]*?['""]?\btype['""]?\s*:\s*(?<v>['""])macro\k<v>")]
    private static partial Regex MacroAttribute();
}
