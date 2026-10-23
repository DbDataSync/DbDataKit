using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace RoslynNuGetScripting;

/// <summary>
/// One collectible context per script (or per package set). Package assemblies come from the resolved closure; anything it does not
/// know (the shared framework, assemblies the host chose to share) falls through to the default context, so those types stay shared.
/// </summary>
public sealed class ScriptLoadContext(string name, Closure closure) : AssemblyLoadContext(name, isCollectible: true)
{
    readonly Dictionary<IntPtr, string> loaded = [];

    protected override Assembly? Load(AssemblyName assemblyName) =>
        assemblyName.Name is { } n && closure.Managed.TryGetValue(n, out var path) ? LoadFromAssemblyPath(path) : null;

    // Reached for a [DllImport] in an assembly of this context when the assembly has no resolver of its own that succeeded.
    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        foreach (var candidate in Platform.NativeNameCandidates(unmanagedDllName))
            if (closure.Native.TryGetValue(candidate, out var path) && NativeLibrary.TryLoad(path, out var handle))
            {
                loaded[handle] = path;
                return handle;
            }
        return IntPtr.Zero;
    }

    public IReadOnlyCollection<string> LoadedNativeLibraries => loaded.Values;
}
