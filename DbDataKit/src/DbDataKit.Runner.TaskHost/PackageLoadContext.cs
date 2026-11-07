using System.Reflection;
using System.Runtime.Loader;

namespace DbDataKit.Runner.TaskHost;

/// <summary>
/// The load context of one package (plan 28a R06): its assembly and the dependencies beside it, resolved from the package's own directory through its <c>deps.json</c> when there is one (and by file name
/// when there is not). What the task host itself uses to speak to the runner (the kit, the protocol, their dependencies, the framework) always comes from the host's own context, even when the package
/// ships a copy: a job and the kit must agree on the types they exchange, so a copy of the kit in the package is never loaded.
/// </summary>
internal sealed class PackageLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _directory;
    private readonly HashSet<string> _shared;

    public PackageLoadContext(string assemblyPath)
        : base("package:" + Path.GetFileNameWithoutExtension(assemblyPath), isCollectible: false)
    {
        AssemblyPath = Path.GetFullPath(assemblyPath);
        _directory = Path.GetDirectoryName(AssemblyPath)!;
        _resolver = new AssemblyDependencyResolver(AssemblyPath);
        _shared = SharedAssemblyNames();
    }

    public string AssemblyPath { get; }

    /// <summary>The assembly of the package.</summary>
    public Assembly LoadPackage() => LoadFromAssemblyPath(AssemblyPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var name = assemblyName.Name;
        if (name is null || _shared.Contains(name)) return null; // null: the default context loads it

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        if (path is null)
        {
            var beside = Path.Combine(_directory, name + ".dll");
            if (File.Exists(beside)) path = beside;
        }

        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? 0 : LoadUnmanagedDllFromPath(path);
    }

    /// <summary>The names of the assemblies the task host's own code needs: everything the host, the kit and the protocol reference, transitively.</summary>
    private static HashSet<string> SharedAssemblyNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<Assembly>();
        queue.Enqueue(typeof(PackageLoadContext).Assembly);
        queue.Enqueue(typeof(Kit.RemoteJobHost).Assembly);
        queue.Enqueue(typeof(Protocol.RunnerEndpoint).Assembly);
        while (queue.Count > 0)
        {
            var assembly = queue.Dequeue();
            if (assembly.GetName().Name is not { } name || !names.Add(name)) continue;
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                if (reference.Name is null || names.Contains(reference.Name)) continue;
                try
                {
                    queue.Enqueue(Default.LoadFromAssemblyName(reference));
                }
                catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
                {
                    // the host cannot load it, so the package may bring its own
                }
            }
        }

        return names;
    }
}
