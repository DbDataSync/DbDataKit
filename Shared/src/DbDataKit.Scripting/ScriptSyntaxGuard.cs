using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DbDataKit.Scripting;

/// <summary>
/// Rejects a script that reaches for a namespace the host does not want scripts in (ported from Sync's guard).
/// <para>
/// **This is a lint, not a security boundary.** Reflection and an alias defeat it, and .NET has had no in-process code-trust mechanism since CAS was removed. The reference set cannot do the job alone: keeping an assembly out works for
/// <c>System.Net.Http</c>, but <c>System.IO.File</c> and <c>System.Environment</c> live in <c>System.Private.CoreLib</c> next to <c>string</c>. What it buys is that the likeliest accident, a script quietly reading a file or a variable,
/// fails at save time with a message saying why instead of in a run. A script that loads a package is code execution in the process, as the README says.
/// </para>
/// </summary>
public static class ScriptSyntaxGuard
{
    /// <summary>
    /// What Sync's scripts may not touch. <c>System.Threading</c> is deliberately not here: <c>Task</c>, <c>ValueTask</c> and <c>CancellationToken</c> live there, so banning it would make the async contracts unimplementable.
    /// </summary>
    public static IReadOnlyList<string> DefaultBannedNamespaces { get; } = ["System.IO", "System.Net", "System.Diagnostics", "System.Reflection", "System.Runtime.InteropServices"];

    private const string Why =
        "A script transforms data and generates SQL; it does not read files, open sockets, start processes or touch the host. If you need something from outside, put it in the parameters instead.";

    public static IReadOnlyList<ScriptDiagnostic> Check(SyntaxTree tree, IReadOnlyList<string> banned)
    {
        var diagnostics = new List<ScriptDiagnostic>();
        var root = tree.GetRoot();
        bool IsBanned(string name) => banned.Any(ns => name == ns || name.StartsWith(ns + ".", StringComparison.Ordinal));
        string Prefix(string name) => banned.FirstOrDefault(ns => name == ns || name.StartsWith(ns + ".", StringComparison.Ordinal)) ?? name;

        foreach (var directive in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
        {
            var name = directive.Name?.ToString();
            if (name is not null && IsBanned(name)) diagnostics.Add(At(directive, $"Scripts may not use '{name}'. {Why}"));
        }

        // a qualified name spelled out in code, which a `using` check alone would miss; a using directive holds one too and was reported above
        foreach (var qualified in root.DescendantNodes().OfType<QualifiedNameSyntax>())
        {
            if (qualified.FirstAncestorOrSelf<UsingDirectiveSyntax>() is not null) continue;
            var name = qualified.ToString();
            if (IsBanned(name))
            {
                diagnostics.Add(At(qualified, $"Scripts may not use '{name}'. {Why}"));
                break;
            }
        }

        foreach (var member in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            var name = member.ToString();
            if (IsBanned(name))
            {
                diagnostics.Add(At(member, $"Scripts may not use '{Prefix(name)}'. {Why}"));
                break;
            }
        }
        return diagnostics;
    }

    private static ScriptDiagnostic At(SyntaxNode node, string message)
    {
        var span = node.GetLocation().GetLineSpan();
        return new ScriptDiagnostic(span.Path, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1, message);
    }
}
