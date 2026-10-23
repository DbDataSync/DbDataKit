using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NuGet.Versioning;

namespace RoslynNuGetScripting;

public sealed record PackageRequest(string Id, VersionRange Range);

/// <summary>Finds <c>#r "nuget: Id, Version"</c> lines (the idea of roslyn-extract-package-reference.cs) and blanks them out.</summary>
public static class ScriptDirectives
{
    const string Prefix = "nuget:";

    /// <returns>The packages asked for, and the script text with those directives replaced by empty lines (line numbers stay valid).</returns>
    public static (List<PackageRequest> Packages, string Source) Extract(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(kind: SourceCodeKind.Script));
        var found = new List<PackageRequest>();
        var spans = new List<Microsoft.CodeAnalysis.Text.TextSpan>();
        foreach (var trivia in tree.GetRoot().DescendantTrivia().Where(t => t.IsKind(SyntaxKind.ReferenceDirectiveTrivia)))
        {
            var directive = (ReferenceDirectiveTriviaSyntax)trivia.GetStructure()!;
            var text = directive.File.ValueText.Trim();
            if (!text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) continue;
            found.Add(Parse(text[Prefix.Length..], directive.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
            // Blank the directive but keep any line break it spans, so diagnostics still point at the right line.
            spans.Add(directive.Span);
        }
        foreach (var span in spans.OrderByDescending(s => s.Start))
            source = source[..span.Start] + new string(source[span.Start..span.End].Select(c => c is '\r' or '\n' ? c : ' ').ToArray()) + source[span.End..];
        return (found, source);
    }

    /// <summary>"Id", "Id, 1.2.3", "Id, [1.0,2.0)" and "Id, 1.2.*" are accepted; a bare id means the newest stable version.</summary>
    public static PackageRequest Parse(string text, int line = 0)
    {
        var at = line > 0 ? $" (script line {line})" : "";
        var comma = text.IndexOf(',');
        var id = (comma < 0 ? text : text[..comma]).Trim();
        var version = comma < 0 ? "*" : text[(comma + 1)..].Trim();
        // Ids become folder names in the cache, so the character set is checked here, before any path is built.
        if (id.Length == 0 || id.Length > 100 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
            throw new FormatException($"Invalid package id '{id}'{at}");
        if (!VersionRange.TryParse(version, out var range))
            throw new FormatException($"Invalid version '{version}' for {id}{at}");
        return new PackageRequest(id, range);
    }
}
