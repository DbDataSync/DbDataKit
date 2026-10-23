using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

string scriptCode = @"
    #r ""nuget: Newtonsoft.Json, 13.0.3""
    #r ""System.Net.Http""
    using System;

    Console.WriteLine(""Hello"");
";

var syntaxTree = CSharpSyntaxTree.ParseText(scriptCode);
var root = syntaxTree.GetRoot();

// Extract all reference directive trivia (#r)
var referenceDirectives = root.DescendantTrivia()
    .Where(t => t.IsKind(SyntaxKind.ReferenceDirectiveTrivia))
    .Select(t => (ReferenceDirectiveTriviaSyntax)t.GetStructure());

foreach (var dir in referenceDirectives)
{
    // dir.File.ValueText contains the target string, e.g., "nuget: Newtonsoft.Json, 13.0.3"
    string referenceValue = dir.File.ValueText;
    System.Console.WriteLine($"Found #r reference: {referenceValue}");
}
