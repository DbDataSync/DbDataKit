using System.Collections.Immutable;
using System.Reflection;
using DbDataKit.Runner.Kit.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DbDataKit.Runner.Kit.Tests;

/// <summary>Compiles C# snippets against the kit with the Roslyn compiler, with or without the kit's analyzer: the way the compile-time promises of plan 28b are tested.</summary>
internal static class RoslynHelper
{
    private static readonly Lazy<ImmutableArray<MetadataReference>> References = new(() =>
    {
        var trusted = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        return [.. trusted.Split(Path.PathSeparator).Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))];
    });

    public const string Header = """
        using System;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using DbDataKit.Runner.Kit;

        public sealed class P : Progress { [Checkpoint] public DateTimeOffset At { get; set; } [State] public string? Cursor { get; set; } }
        public static class S
        {
            public static readonly ValueStep<DateOnly, string> Pack = Step.Value<DateOnly, string>("pack", d => d.ToString());
            public static readonly StreamStep<int> Dump = Step.Stream<int>("dump", i => i.ToString());
            public static readonly Unit<int> Item = Unit.Of<int>("item", i => i.ToString());
            public static readonly Counter Rows = new("rows");
        }
        """;

    /// <summary>A job class whose <c>RunAsync</c> has <paramref name="body"/>, after <see cref="Header"/>.</summary>
    public static string Job(string body) => Header + $$"""

        [RunnerTask("x")]
        public sealed class J : RunnerJob<NoInput, P, NoSlots>
        {
            protected override async Task RunAsync(CancellationToken ct)
            {
                {{body}}
            }
        }
        """;

    private static CSharpCompilation Compile(string source) =>
        CSharpCompilation.Create("Snippet", [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest), path: "Snippet.cs")], References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

    /// <summary>The compiler's errors, as <c>CS0029: message</c> lines.</summary>
    public static IReadOnlyList<Diagnostic> Errors(string source) => [.. Compile(source).GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)];

    /// <summary>The kit analyzer's diagnostics for the source.</summary>
    public static async Task<IReadOnlyList<Diagnostic>> Analyze(string source)
    {
        var compilation = Compile(source);
        var withAnalyzers = compilation.WithAnalyzers([new RunnerKitAnalyzer()]);
        var found = await withAnalyzers.GetAnalyzerDiagnosticsAsync();
        return [.. found.OrderBy(d => d.Location.SourceSpan.Start)];
    }

    public static string Describe(IEnumerable<Diagnostic> diagnostics) => string.Join("\n", diagnostics.Select(d => $"{d.Id}: {d.GetMessage()} @ {d.Location.GetLineSpan().StartLinePosition}"));

    public static Assembly KitAssembly => typeof(RunnerJobBase).Assembly;
}
