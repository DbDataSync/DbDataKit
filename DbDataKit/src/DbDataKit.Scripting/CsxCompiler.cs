using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace DbDataKit.Scripting;

public sealed class CsxCompilerOptions
{
    /// <summary>Where compiled images are kept, under the product's data directory (never <c>%TEMP%</c> or a user profile).</summary>
    public required string CacheDirectory { get; init; }

    /// <summary>The folder <c>#load</c> may reach (the configuration repository); null refuses every <c>#load</c>.</summary>
    public string? LoadRoot { get; init; }

    /// <summary>Assemblies of the host that scripts may see besides the shared framework and their own packages: the contracts a driver or a hook implements. A script cannot reach the host's other assemblies by accident.</summary>
    public IReadOnlyList<string> HostReferences { get; init; } = [];

    public IReadOnlyList<string> Imports { get; init; } = ["System", "System.Linq", "System.Collections.Generic", "System.Threading.Tasks"];

    public IReadOnlyList<string> BannedNamespaces { get; init; } = ScriptSyntaxGuard.DefaultBannedNamespaces;

    /// <summary>Which shared-framework assemblies a script may reference, by simple name; the default is all of them (the guard covers what the reference set cannot).</summary>
    public Func<string, bool> AllowedFrameworkAssembly { get; init; } = _ => true;

    /// <summary>The type of the <c>globals</c> a host hands the script (its members are in scope by name), or null.</summary>
    public Type? GlobalsType { get; init; }
}

public sealed record CsxCompilation(bool Success, byte[]? Image, bool FromCache, IReadOnlyList<ScriptDiagnostic> Diagnostics);

/// <summary>
/// The compiler (S04): a <c>.csx</c> source to a cached assembly with Roslyn scripting, against the shared framework, the script's own closure and the host's contract assemblies. <c>#load</c> goes through <see cref="RestrictedSourceResolver"/>,
/// diagnostics carry the script's own line numbers, and <see cref="ScriptSyntaxGuard"/> runs before anything is emitted. The image is not run here: <see cref="RunAsync"/> loads it into the caller's load context, so every package assembly it needs
/// stays inside that context (Roslyn's own interactive loader would put them in the default context, which cannot be unloaded).
/// </summary>
public sealed class CsxCompiler(CsxCompilerOptions options)
{
    /// <summary>
    /// Roslyn's scripting output is a class <c>Submission#0</c> with a static <c>&lt;Factory&gt;</c> method taking the submission array. That is a naming convention, not a public API; a test pins it, and if Roslyn changes it this is the one place to change
    /// (the alternative is compiling a normal class library with a conventional entry point).
    /// </summary>
    internal const string SubmissionType = "Submission#0";
    internal const string FactoryMethod = "<Factory>";

    /// <param name="source">The script text with its <c>#r "nuget:"</c> directives blanked (<see cref="ScriptDirectives.Extract"/>).</param>
    /// <param name="name">The script's name, the file name in diagnostics.</param>
    public CsxCompilation Compile(string source, string name, Closure closure)
    {
        var resolver = options.LoadRoot is null ? null : new RestrictedSourceResolver(options.LoadRoot);
        var scriptOptions = ScriptOptions.Default
            .WithReferences(References(closure))
            .WithImports(options.Imports)
            .WithFilePath(name)
            .WithEmitDebugInformation(false)
            .WithMetadataResolver(NoFileReferences.Instance)
            .WithSourceResolver(resolver ?? new RestrictedSourceResolver(Path.Combine(Path.GetTempPath(), "dbdatakit-no-load-" + Guid.NewGuid().ToString("N"))));

        var key = Key(source, name, closure, resolver);
        var cached = Path.Combine(options.CacheDirectory, "compiled", key + ".dll");
        if (File.Exists(cached)) return new CsxCompilation(true, File.ReadAllBytes(cached), true, []);

        var compilation = CSharpScript.Create<object?>(source, scriptOptions, options.GlobalsType).GetCompilation();

        // before Roslyn emits anything: the reference set cannot say this on its own
        var banned = compilation.SyntaxTrees.SelectMany(t => ScriptSyntaxGuard.Check(t, options.BannedNamespaces)).ToList();
        if (banned.Count > 0) return new CsxCompilation(false, null, false, banned);

        using var image = new MemoryStream();
        var emit = compilation.Emit(image);
        var diagnostics = emit.Diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(ToDiagnostic).ToList();
        if (!emit.Success) return new CsxCompilation(false, null, false, diagnostics);

        var bytes = image.ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
        var temp = cached + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, cached, overwrite: true);                       // atomic: two processes writing one entry write the same bytes
        return new CsxCompilation(true, bytes, false, diagnostics);
    }

    /// <summary>
    /// Loads the image into <paramref name="context"/> and runs the script. Call it from a frame that keeps nothing of the result: a result object of a type from the context would keep the context alive, so a host that wants to unload
    /// reads what it needs as plain data before it lets go. The script may be async; it is awaited here.
    /// </summary>
    public static Task<object?> RunAsync(byte[] image, AssemblyLoadContext context, object? globals = null) => Load(image, context).RunAsync(globals);

    /// <summary>
    /// Loads the image into the context once, for a host that runs the script more than once (each run is a new instance of the script and of whatever its last expression builds). An assembly can be loaded into a context only once,
    /// so a host that wants a fresh script object per use loads once and runs many times.
    /// </summary>
    public static LoadedCsx Load(byte[] image, AssemblyLoadContext context)
    {
        var assembly = context.LoadFromStream(new MemoryStream(image));
        var factory = assembly.GetType(SubmissionType)?.GetMethod(FactoryMethod)
            ?? throw new InvalidOperationException($"The compiled script has no {SubmissionType}.{FactoryMethod}: Roslyn's scripting output changed shape.");
        return new LoadedCsx(factory);
    }

    private IEnumerable<MetadataReference> References(Closure closure)
    {
        var paths = new List<string>();
        paths.AddRange(Platform.FrameworkAssemblies().Where(f => options.AllowedFrameworkAssembly(f.Key)).Select(f => f.Value));
        paths.AddRange(options.HostReferences);
        paths.AddRange(closure.Compile.Values);
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).Select(p => MetadataReference.CreateFromFile(p));
    }

    // everything the image depends on: the text and the files it loads, the files compiled against, the options that change the output, and Roslyn itself
    private string Key(string source, string name, Closure closure, RestrictedSourceResolver? resolver)
    {
        var key = new StringBuilder(source).Append('\0').Append(name).Append('\0').Append(typeof(Script).Assembly.GetName().Version);
        key.Append('\0').Append(string.Join(',', options.Imports)).Append('\0').Append(string.Join(',', options.BannedNamespaces)).Append('\0').Append(options.GlobalsType?.AssemblyQualifiedName);
        foreach (var path in Platform.FrameworkAssemblies().Where(f => options.AllowedFrameworkAssembly(f.Key)).Select(f => f.Value).Concat(options.HostReferences).Concat(closure.Compile.Values).Order(StringComparer.Ordinal))
            key.Append('\0').Append(path).Append('|').Append(File.GetLastWriteTimeUtc(path).Ticks);
        foreach (var loaded in LoadedFiles(source, name, resolver, []))
            key.Append('\0').Append(loaded);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.ToString())))[..32];
    }

    private static IEnumerable<string> LoadedFiles(string source, string? basePath, RestrictedSourceResolver? resolver, HashSet<string> seen)
    {
        if (resolver is null) yield break;
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(kind: SourceCodeKind.Script));
        foreach (var load in tree.GetRoot().DescendantTrivia().Where(t => t.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.LoadDirectiveTrivia)).Select(t => (Microsoft.CodeAnalysis.CSharp.Syntax.LoadDirectiveTriviaSyntax)t.GetStructure()!))
        {
            var path = resolver.ResolveReference(load.File.ValueText, basePath);
            if (path is null || !seen.Add(path)) continue;
            var text = File.ReadAllText(path);
            yield return path + "|" + text;
            foreach (var nested in LoadedFiles(text, path, resolver, seen)) yield return nested;
        }
    }

    private static ScriptDiagnostic ToDiagnostic(Diagnostic diagnostic)
    {
        var span = diagnostic.Location.GetLineSpan();
        return new ScriptDiagnostic(span.Path, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1, diagnostic.GetMessage(), diagnostic.Severity == DiagnosticSeverity.Error);
    }
}

/// <summary>A compiled script loaded into a context; <see cref="RunAsync"/> runs it and returns the value of its last expression.</summary>
public sealed class LoadedCsx
{
    private readonly MethodInfo _factory;

    internal LoadedCsx(MethodInfo factory) => _factory = factory;

    public async Task<object?> RunAsync(object? globals = null)
    {
        var submissions = new object?[2];
        submissions[0] = globals;
        return await ((Task<object?>)_factory.Invoke(null, [submissions])!).ConfigureAwait(false);
    }
}
