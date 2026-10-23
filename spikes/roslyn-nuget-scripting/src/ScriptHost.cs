using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using NuGet.Packaging.Core;

namespace RoslynNuGetScripting;

public sealed record ScriptRun(string? Result, WeakReference Context, bool CompiledFromCache, IReadOnlyList<string> NativeLibraries);

/// <summary>.csx text to a result: resolve packages, compile with Roslyn, load into a collectible context, run.</summary>
public sealed class ScriptHost(PackageResolverOptions resolverOptions, string compiledCacheDir)
{
    static readonly Dictionary<string, string> Framework = Platform.FrameworkAssemblies();

    public static PackageResolverOptions Options(PackageStore store, IReadOnlyList<string> sources, Action<string> log,
        Func<PackageIdentity, bool>? allow = null, IReadOnlyDictionary<string, string>? pins = null) => new()
    {
        Store = store, Sources = sources, Log = log,
        HostProvides = Framework.ContainsKey,
        IsAllowed = allow ?? (_ => true),
        PinnedHashes = pins ?? new Dictionary<string, string>(),
    };

    public async Task<ScriptRun> RunAsync(string scriptText, string name, CancellationToken ct = default)
    {
        var (requests, source) = ScriptDirectives.Extract(scriptText);
        var closure = await new PackageResolver(resolverOptions).ResolveAsync(requests, ct);
        foreach (var p in closure.Packages) resolverOptions.Log($"  {p.Id} {p.Version}  sha512 {p.Sha512[..12]}...");
        return Execute(source, closure, name);
    }

    /// <remarks>
    /// Synchronous and not inlined on purpose. An async method keeps its locals in the state machine, which the returned task
    /// keeps alive, so a context created there could never be collected. Here every reference to the context dies with the frame.
    /// The script itself may be async; it is awaited with a blocking wait, fine for a console or a worker thread.
    /// </remarks>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    ScriptRun Execute(string source, Closure closure, string name)
    {
        var (image, fromCache) = Compile(source, closure);
        var context = new ScriptLoadContext(name, closure);
        var assembly = context.LoadFromStream(new MemoryStream(image));
        // Roslyn's own loader (InteractiveAssemblyLoader) would put the package assemblies into the default context, which cannot
        // be unloaded. Running the emitted submission by hand keeps every load inside our context.
        var factory = assembly.GetType("Submission#0")!.GetMethod("<Factory>")!;
        var result = ((Task<object?>)factory.Invoke(null, [new object?[2]])!).GetAwaiter().GetResult();
        // Only text leaves this method: a result object of a type from the context would keep the context alive.
        return new ScriptRun(result?.ToString(), new WeakReference(context), fromCache, context.LoadedNativeLibraries.ToList());
    }

    /// <summary>Unloads the context of a finished run and reports whether the managed side was really collected.</summary>
    public static bool Collect(WeakReference weak)
    {
        Unload(weak);
        for (var i = 0; i < 20 && weak.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        return !weak.IsAlive;
    }

    // Separate frame: a local holding the context here would keep it alive for the whole collection loop in the caller.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static void Unload(WeakReference weak) => (weak.Target as ScriptLoadContext)?.Unload();

    (byte[] Image, bool FromCache) Compile(string source, Closure closure)
    {
        // The key covers everything the compiled image depends on: the text, the exact files compiled against, and Roslyn itself.
        var key = new StringBuilder(source).Append('\0').Append(typeof(Script).Assembly.GetName().Version);
        foreach (var pair in closure.Compile.OrderBy(p => p.Key, StringComparer.Ordinal)) key.Append('\0').Append(pair.Value);
        var file = Path.Combine(compiledCacheDir, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.ToString())))[..32] + ".dll");
        if (File.Exists(file)) return (File.ReadAllBytes(file), true);

        // Host references: only the shared framework. A script cannot see the host application's own assemblies unless the host adds them.
        var references = Framework.Values.Concat(closure.Compile.Values).Select(p => MetadataReference.CreateFromFile(p));
        var options = ScriptOptions.Default
            .WithReferences(references)
            .WithImports("System", "System.IO", "System.Linq", "System.Collections.Generic", "System.Threading.Tasks")
            .WithEmitDebugInformation(false);
        var compilation = CSharpScript.Create<object?>(source, options).GetCompilation();
        using var image = new MemoryStream();
        var emit = compilation.Emit(image);
        if (!emit.Success)
            throw new InvalidOperationException("Script does not compile:\n" + string.Join("\n", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));

        Directory.CreateDirectory(compiledCacheDir);
        var temp = file + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(temp, image.ToArray());
        File.Move(temp, file, overwrite: true);
        return (image.ToArray(), false);
    }
}
