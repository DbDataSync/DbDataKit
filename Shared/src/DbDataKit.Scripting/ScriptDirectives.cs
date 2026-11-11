using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using NuGet.Versioning;

namespace DbDataKit.Scripting;

/// <summary>A package a script asks for with <c>#r "nuget: Id, Version"</c>, and the line of the script it was asked on (0 when it did not come from a script).</summary>
public sealed record PackageRequest(string Id, VersionRange Range, int Line = 0)
{
    public static PackageRequest Exact(string id, string version)
    {
        LibraryValidation.ValidatePackageId(id);
        LibraryValidation.ValidateVersion(version);
        return new(id, new VersionRange(NuGetVersion.Parse(version), includeMinVersion: true, NuGetVersion.Parse(version), includeMaxVersion: true));
    }
}

/// <summary>A directive that cannot be used. The message names the script line and never quotes more of the directive than the package id.</summary>
public sealed class ScriptDirectiveException(string message, int line) : FormatException(line > 0 ? $"{message} (script line {line})" : message)
{
    public int Line { get; } = line;
}

/// <summary>
/// Finds <c>#r "nuget: Id, Version"</c> directives by parsing the script with Roslyn, never with a pattern (a directive in a comment or a string is not one), and blanks them out so the script compiles and its line numbers stay valid.
/// Config and library scripts use exact versions only (the default of <c>exactOnly</c>); a range, a floating version or a bare id (the newest stable version) is for interactive use.
/// </summary>
public static class ScriptDirectives
{
    private const string Prefix = "nuget:";

    /// <returns>The packages asked for, and the script text with those directives replaced by blanks (line breaks kept).</returns>
    public static (IReadOnlyList<PackageRequest> Packages, string Source) Extract(string source, bool exactOnly = true)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(kind: SourceCodeKind.Script));
        var found = new List<PackageRequest>();
        var spans = new List<TextSpan>();
        foreach (var trivia in tree.GetRoot().DescendantTrivia().Where(t => t.IsKind(SyntaxKind.ReferenceDirectiveTrivia)))
        {
            var directive = (ReferenceDirectiveTriviaSyntax)trivia.GetStructure()!;
            var text = directive.File.ValueText.Trim();
            if (!text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) continue;       // #r "System.Xml" and the like are Roslyn's own
            found.Add(Parse(text[Prefix.Length..], directive.GetLocation().GetLineSpan().StartLinePosition.Line + 1, exactOnly));
            spans.Add(directive.Span);
        }
        foreach (var span in spans.OrderByDescending(s => s.Start))
            source = source[..span.Start] + new string(source[span.Start..span.End].Select(c => c is '\r' or '\n' ? c : ' ').ToArray()) + source[span.End..];
        return (found, source);
    }

    /// <summary>"Id, 1.2.3" always; "Id", "Id, [1.0,2.0)" and "Id, 1.2.*" only when <paramref name="exactOnly"/> is false.</summary>
    public static PackageRequest Parse(string text, int line = 0, bool exactOnly = true)
    {
        var comma = text.IndexOf(',');
        var id = (comma < 0 ? text : text[..comma]).Trim();
        var version = comma < 0 ? null : text[(comma + 1)..].Trim();
        try { LibraryValidation.ValidatePackageId(id); }
        catch (LibraryValidationException ex) { throw new ScriptDirectiveException($"The package id in the directive is not valid: {ex.Rule}", line); }

        if (version is null)
        {
            if (exactOnly) throw new ScriptDirectiveException($"'{id}' has no version, and a stored script names an exact one: #r \"nuget: {id}, 1.2.3\"", line);
            return new PackageRequest(id, VersionRange.AllStable, line);
        }
        if (exactOnly)
        {
            try { LibraryValidation.ValidateVersion(version); }
            catch (LibraryValidationException) { throw new ScriptDirectiveException($"The version of '{id}' must be one exact version (1.2.3), not a range, a wildcard or a property", line); }
            return PackageRequest.Exact(id, version) with { Line = line };       // [1.2.3], not NuGet's "1.2.3 or newer"
        }
        if (!VersionRange.TryParse(version, out var range)) throw new ScriptDirectiveException($"The version of '{id}' is not a version or a version range", line);
        return new PackageRequest(id, range, line);
    }
}
