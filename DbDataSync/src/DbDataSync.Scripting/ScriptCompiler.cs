using DbDataKit.Scripting;
using DbDataSync.Core.Config;
using DbDataSync.Scripting.Abstractions;

namespace DbDataSync.Scripting;

/// <summary>
/// Compiles a script (<c>config/scripts/&lt;name&gt;.csx</c>) with the shared compiler of <c>DbDataKit.Scripting</c> (plan 13, S07). A script may <c>#r "nuget: Id, 1.2.3"</c> packages (exact versions), <c>#load</c> other scripts of the
/// configuration repository's scripts folder, and ends with an expression that is the slot's implementation (<c>new MyTransform()</c>). It is compiled against the allow-listed framework assemblies, the contracts a script implements and its own
/// packages, and it runs in a collectible load context of its own, so replacing a script does not leak an assembly for the life of the process.
/// <para>
/// **The allow-list and the syntax guard are a guardrail, not a security boundary, and must not be described as one.** Reflection defeats them, and .NET has had no in-process code-trust mechanism since CAS was removed. The security decision (the
/// risk is accepted and gated by permissions) is recorded in architecture/planning/done/csharp-scripting-host.md. Keeping an assembly out works for <c>System.Net.Http</c> and <c>System.Diagnostics.Process</c>, which have their own; it does nothing
/// about <c>System.IO.File</c> or <c>System.Environment</c>, which live in <c>System.Private.CoreLib</c> next to <c>string</c>, and <see cref="ScriptSyntaxGuard"/> covers that gap as a lint.
/// </para>
/// </summary>
public sealed class ScriptCompiler(ScriptCacheDirectory cache, string? scriptsDirectory = null, string? packageSource = null)
{
    private static readonly string[] AllowedAssemblies =
    [
        "System.Runtime",
        "System.Private.CoreLib",
        "System.Linq",
        "System.Collections",
        "System.Text.RegularExpressions",
        "System.Globalization",
        "netstandard",
    ];

    private static readonly string[] Imports =
    [
        "System", "System.Linq", "System.Collections.Generic", "System.Threading", "System.Threading.Tasks",
        "DbDataSync.Core.Config", "DbDataSync.Scripting.Abstractions", "DbDataSync.Drivers.Abstractions",
    ];

    public ScriptCompilation Compile(ScriptDefinition script)
    {
        if (string.IsNullOrWhiteSpace(script.Code))
            return ScriptCompilation.Failed([new ScriptDiagnostic(0, 0, $"Script '{script.Manifest.Name}' has no code.")]);

        // A SQL hook has no code to compile — ScriptHost never routes one here (see HookExecution), so reaching this with Language=Sql is a caller error, not a config problem.
        if (script.Manifest.Language != ScriptLanguage.CSharp)
            throw new InvalidOperationException(
                $"Script '{script.Manifest.Name}' is not a C# script and cannot be compiled — ScriptCompiler.Compile is only for ScriptLanguage.CSharp scripts.");

        IReadOnlyList<PackageRequest> requests;
        string source;
        try { (requests, source) = ScriptDirectives.Extract(script.Code, exactOnly: true); }
        catch (ScriptDirectiveException ex) { return ScriptCompilation.Failed([new ScriptDiagnostic(ex.Line, 1, ex.Message)]); }

        Closure closure;
        try { closure = ResolvePackages(script.Manifest.Name, requests); }
        catch (Exception ex) when (ex is PackageResolutionException or PackageIntegrityException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return ScriptCompilation.Failed([new ScriptDiagnostic(0, 0, $"The packages of script '{script.Manifest.Name}' could not be resolved: {ex.Message}")]);
        }

        var compiler = new CsxCompiler(new CsxCompilerOptions
        {
            CacheDirectory = Path.Combine(cache.Root, "csx"),
            LoadRoot = scriptsDirectory,
            HostReferences = [typeof(ISqlColumnExpression).Assembly.Location, typeof(DbDataSync.Drivers.Abstractions.ColumnMetadata).Assembly.Location, typeof(ColumnMapping).Assembly.Location],
            Imports = Imports,
            AllowedFrameworkAssembly = name => AllowedAssemblies.Contains(name, StringComparer.OrdinalIgnoreCase),
        });
        // Before Roslyn emits anything, because the reference set cannot express the namespace lint on its own — the guard runs inside the shared compiler.
        var name = scriptsDirectory is null ? script.Manifest.Name + ".csx" : Path.Combine(scriptsDirectory, script.Manifest.Name + ".csx");
        var compiled = compiler.Compile(source, name, closure);
        if (!compiled.Success)
            return ScriptCompilation.Failed(compiled.Diagnostics.Where(d => d.IsError).Select(d => new ScriptDiagnostic(d.Line, d.Column, d.Message)).ToList());

        return Load(compiled.Image!, closure, script);
    }

    private ScriptCompilation Load(byte[] image, Closure closure, ScriptDefinition script)
    {
        var hash = ScriptConfig.ComputeHash(script.Code);
        // Collectible, so replacing a script does not leak an assembly for the life of the process. In the TaskRunner that hardly matters — the process is per run — but the API is long-lived and compiles on every save.
        var context = new ClosureLoadContext($"script:{script.Manifest.Name}:{hash[..8]}", closure,
            shared: n => System.Runtime.Loader.AssemblyLoadContext.Default.Assemblies.Any(a => a.GetName().Name == n));
        try
        {
            return ScriptCompilation.Succeeded(context, CsxCompiler.Load(image, context), hash);
        }
        catch (Exception ex)
        {
            context.Unload();
            return ScriptCompilation.Failed([new ScriptDiagnostic(0, 0, $"Could not load the compiled script: {ex.Message}")]);
        }
    }

    // exact versions that are in the package store need no network; a script's packages are fetched when it is first validated (saved or previewed), and a run finds them there.
    // The hashes are pinned in scripts/<name>.lock beside the script (S12): the first resolution writes it, a package whose bytes differ later is refused naming the file. A compiler with no scripts folder (a test, a preview of an
    // unsaved script with no repository) has nowhere to keep a lock file and trusts the first download, as before.
    private Closure ResolvePackages(string scriptName, IReadOnlyList<PackageRequest> requests)
    {
        var closure = new Closure();
        if (requests.Count == 0) return closure;
        var lockPath = scriptsDirectory is null ? null : Path.Combine(scriptsDirectory, scriptName + ScriptConfig.LockExtension);
        var pins = lockPath is null ? new LockFile() : LockFile.Read(lockPath, $"{Path.GetFileName(scriptsDirectory!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}/{scriptName}{ScriptConfig.LockExtension}");
        var options = new PackageResolverOptions
        {
            Store = new PackageStore(Path.Combine(cache.Root, "packages")),
            Sources = [new PackageSource(packageSource ?? "https://api.nuget.org/v3/index.json")],
            HostProvides = n => Platform.FrameworkAssemblies().ContainsKey(n),
            Lock = pins,
        };
        closure = new PackageResolver(options).ResolveAsync(requests).GetAwaiter().GetResult();
        if (lockPath is not null)
        {
            pins.Replace(closure.Packages);
            pins.Write(lockPath);
        }
        return closure;
    }
}

public sealed record ScriptDiagnostic(int Line, int Column, string Message)
{
    public override string ToString() => Line > 0 ? $"({Line},{Column}): {Message}" : Message;
}

public sealed class ScriptCompilation
{
    private ScriptCompilation(ClosureLoadContext? context, LoadedCsx? script, string? hash, IReadOnlyList<ScriptDiagnostic> diagnostics)
    {
        LoadContext = context;
        Script = script;
        Hash = hash;
        Diagnostics = diagnostics;
    }

    public ClosureLoadContext? LoadContext { get; }
    public LoadedCsx? Script { get; }
    public string? Hash { get; }
    public IReadOnlyList<ScriptDiagnostic> Diagnostics { get; }

    public bool Success => Script is not null;

    public static ScriptCompilation Succeeded(ClosureLoadContext context, LoadedCsx script, string hash) => new(context, script, hash, []);

    public static ScriptCompilation Failed(IReadOnlyList<ScriptDiagnostic> diagnostics) => new(null, null, null, diagnostics);

    /// <summary>Runs the script, which builds the implementation its slot requires: a new instance each time, cast to the contract.</summary>
    public T CreateInstance<T>(string scriptName) where T : class
    {
        if (Script is null)
            throw new ScriptExecutionException($"Script '{scriptName}' did not compile: {string.Join("; ", Diagnostics)}");

        object? result;
        try { result = Script.RunAsync().GetAwaiter().GetResult(); }
        catch (Exception ex) { throw new ScriptExecutionException($"Script '{scriptName}' failed while it was being built: {ex.GetBaseException().Message}"); }

        return result switch
        {
            T instance => instance,
            null => throw new ScriptExecutionException($"Script '{scriptName}' ends with no value: end it with an expression that is the {typeof(T).Name} and no semicolon after it, for example `new MyScript()` (a final `;` makes it a statement with no value)."),
            _ => throw new ScriptExecutionException($"Script '{scriptName}' ends with a {result.GetType().Name}, which does not implement {typeof(T).Name}."),
        };
    }
}
