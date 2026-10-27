using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace DbDataKit.Scripting;

/// <summary>
/// One collectible load context for one library, driver or script: managed assemblies come from the closure the resolver produced, native libraries from the files of the best runtime identifier. A name the host shares (the contract assemblies its
/// drivers are written against, the shared framework) is answered by the default context, so a type is the same type on both sides. Nothing outside the closure is searched: a missing assembly is a load error, not a search of the machine.
/// </summary>
public sealed class ClosureLoadContext : AssemblyLoadContext
{
    private readonly Closure _closure;
    private readonly Func<string, bool> _shared;

    public ClosureLoadContext(string name, Closure closure, Func<string, bool>? shared = null) : base(name, isCollectible: true)
    {
        _closure = closure;
        _shared = shared ?? (_ => false);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var name = assemblyName.Name;
        if (name is null || _shared(name)) return null;
        return _closure.Managed.TryGetValue(name, out var path) ? LoadFromAssemblyPath(path) : null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        foreach (var candidate in NativeNames(unmanagedDllName))
            if (_closure.Native.TryGetValue(candidate, out var path)) return LoadUnmanagedDllFromPath(path);
        return IntPtr.Zero;
    }

    // a DllImport names a library without its prefix or extension, or with them; try the forms each platform uses
    private static IEnumerable<string> NativeNames(string name)
    {
        yield return name;
        var (prefix, extension) = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ("", ".dll") : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? ("lib", ".dylib") : ("lib", ".so");
        if (!name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) yield return name + extension;
        if (prefix.Length > 0 && !name.StartsWith(prefix, StringComparison.Ordinal))
        {
            yield return prefix + name;
            if (!name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) yield return prefix + name + extension;
        }
    }
}

/// <summary>
/// The loader (S03): what a host holds for each library, driver or script it has loaded. The policy is the host's: it loads under a key, loading again under the same key replaces (and unloads) the earlier one, and unloading is best effort. The managed
/// side is collected once nothing references it (<see cref="UnloadedAsync"/> says whether that happened); a native library stays loaded for the life of the process, which is why a changed native package needs a restart.
/// </summary>
public sealed class ClosureLoader : IDisposable
{
    private readonly Dictionary<string, ClosureLoadContext> _loaded = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly Func<string, bool>? _shared;

    public ClosureLoader(Func<string, bool>? shared = null) => _shared = shared;

    public bool IsLoaded(string key)
    {
        lock (_gate) return _loaded.ContainsKey(key);
    }

    /// <summary>Loads the closure under <paramref name="key"/>; the context it replaced, if any, is unloaded.</summary>
    public ClosureLoadContext Load(string key, Closure closure)
    {
        var context = new ClosureLoadContext(key, closure, _shared);
        ClosureLoadContext? replaced;
        lock (_gate)
        {
            _loaded.TryGetValue(key, out replaced);
            _loaded[key] = context;
        }
        replaced?.Unload();
        return context;
    }

    /// <summary>Starts unloading what was loaded under the key; false when nothing was.</summary>
    public bool Unload(string key)
    {
        ClosureLoadContext? context;
        lock (_gate) _loaded.Remove(key, out context);
        context?.Unload();
        return context is not null;
    }

    public void Dispose()
    {
        List<ClosureLoadContext> all;
        lock (_gate)
        {
            all = [.. _loaded.Values];
            _loaded.Clear();
        }
        foreach (var context in all) context.Unload();
    }

    /// <summary>A weak reference to a context, taken where the caller holds nothing else, so a test or a host can ask later whether it was collected.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public static WeakReference Track(ClosureLoadContext context) => new(context, trackResurrection: true);

    /// <summary>Collects until the tracked context is gone or the attempts run out; unloading needs every reference to its types and instances to be dropped first.</summary>
    public static async Task<bool> UnloadedAsync(WeakReference tracked, int attempts = 20)
    {
        for (var i = 0; i < attempts && tracked.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Task.Delay(25);
        }
        return !tracked.IsAlive;
    }
}
