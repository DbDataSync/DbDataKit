using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace DbDataKit.Scripting;

/// <summary>
/// Makes a closure loadable by name in the process's default load context, for the libraries whose types the host compiles against (a built-in driver is written against <c>MySqlConnector</c> or <c>Microsoft.Data.SqlClient</c> with its runtime assets
/// excluded, so the assembly has to be found at first use and must be the same assembly on both sides). Nothing here unloads: a library in the default context lives as long as the process, which is the policy for libraries and drivers the spike
/// recommended; a script or a plugin that must unload uses <see cref="ClosureLoadContext"/> instead.
/// <para>
/// One handler per process answers for every bound closure. When two closures carry different versions of an assembly, the first one bound wins, as the default context allows only one: a conflict is the caller's to see (<see cref="Bind"/> returns the names it did not take).
/// </para>
/// </summary>
public static class DefaultContextBinder
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, string> Managed = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> Native = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> NativeDirectories = [];
    private static bool _armed;

    /// <summary>Adds the closure's assemblies and native files; <paramref name="nativeDirectories"/> are folders searched for a native library by its platform file name (an engine the package does not carry). Returns the assembly names already bound to another file.</summary>
    public static IReadOnlyList<string> Bind(Closure closure, IEnumerable<string>? nativeDirectories = null)
    {
        var conflicts = new List<string>();
        lock (Gate)
        {
            foreach (var (name, path) in closure.Managed)
            {
                if (Managed.TryGetValue(name, out var existing))
                {
                    if (!string.Equals(existing, path, StringComparison.OrdinalIgnoreCase)) conflicts.Add(name);
                    continue;
                }
                Managed[name] = path;
            }
            foreach (var (name, path) in closure.Native) Native.TryAdd(name, path);
            foreach (var directory in nativeDirectories ?? []) if (!NativeDirectories.Contains(directory)) NativeDirectories.Add(directory);
            Arm();
        }
        return conflicts;
    }

    private static void Arm()
    {
        if (_armed) return;
        _armed = true;
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            string? path;
            lock (Gate) path = name.Name is not null && Managed.TryGetValue(name.Name, out var found) ? found : null;
            return path is null ? null : context.LoadFromAssemblyPath(path);
        };
        AssemblyLoadContext.Default.ResolvingUnmanagedDll += (_, unmanagedName) =>
        {
            string[] candidates = [.. NativeNames(unmanagedName)];
            lock (Gate)
            {
                foreach (var candidate in candidates)
                    if (Native.TryGetValue(candidate, out var path)) return NativeLibrary.Load(path);
                foreach (var directory in NativeDirectories)
                    foreach (var candidate in candidates)
                    {
                        var file = Path.Combine(directory, candidate);
                        if (File.Exists(file)) return NativeLibrary.Load(file);
                    }
            }
            return IntPtr.Zero;
        };
    }

    // a DllImport names a library without its prefix or extension, or with them; the forms each platform uses
    internal static IEnumerable<string> NativeNames(string name)
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
