using System.Runtime.Loader;
using DbDataKit.Scripting;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Libraries;

namespace DbDataSync.Drivers.Descriptor;

/// <summary>
/// Compiles and runs a <c>driver.csx</c> (S06): its packages come from the driver's own store (<c>drivers/&lt;id&gt;/lib/</c>, checked against <c>driver.lock</c>, no network), the script is compiled by the shared
/// <see cref="CsxCompiler"/> against the shared framework, the host's driver contract and the script's packages, and it runs in a load context of its own that is never unloaded (a driver lives for the process).
/// A name the default context already has, which includes every installed library, is the host's: the driver's <c>IDriver</c> is the host's <c>IDriver</c> and a provider it shares with a library is the same assembly.
/// A driver opens files and sockets by nature, so the namespace guard that scripts are held to is off.
/// </summary>
public static class DriverScriptHost
{
    private static readonly string[] DefaultImports =
    [
        "System", "System.Linq", "System.Collections.Generic", "System.Threading", "System.Threading.Tasks", "System.Data", "System.Data.Common",
        "DbDataSync.Core.Config", "DbDataSync.Drivers.Abstractions",
    ];

    public static IDriver Load(string driverDir)
    {
        var manifest = CompiledDriverManifest.Read(Path.Combine(driverDir, CompiledDriverManifest.FileName));
        var libDir = LibraryPaths.LibDir(driverDir);
        if (!Directory.Exists(libDir))
            throw new InvalidOperationException($"Driver '{manifest.Id}' is not restored: run `dbdatasync driver sync {manifest.Id}`.");

        Closure closure;
        try { closure = LibraryResolution.LoadAsync(driverDir, manifest.Packages, lockFileName: CompiledDriverManifest.LockFileName).GetAwaiter().GetResult(); }
        catch (Exception ex) when (ex is PackageResolutionException or PackageIntegrityException)
        {
            throw new InvalidOperationException($"Driver '{manifest.Id}' is not restored ({ex.Message}): run `dbdatasync driver sync {manifest.Id}`.", ex);
        }
        DefaultContextBinder.Bind(closure, [libDir]);

        var (_, source) = ScriptDirectives.Extract(manifest.Source, exactOnly: true);
        var compiler = new CsxCompiler(new CsxCompilerOptions
        {
            CacheDirectory = Path.Combine(libDir, ".csx-cache"),
            LoadRoot = driverDir,
            HostReferences = [typeof(IDriver).Assembly.Location, typeof(ConnectionConfig).Assembly.Location],
            Imports = DefaultImports,
            BannedNamespaces = [],
        });
        var compiled = compiler.Compile(source, Path.Combine(driverDir, CompiledDriverManifest.FileName), closure);
        if (!compiled.Success)
            throw new InvalidOperationException($"Driver '{manifest.Id}' does not compile:{Environment.NewLine}{string.Join(Environment.NewLine, compiled.Diagnostics.Where(d => d.IsError))}");

        var context = new ClosureLoadContext($"driver:{manifest.Id}", closure, name => AssemblyLoadContext.Default.Assemblies.Any(a => a.GetName().Name == name));
        var result = CsxCompiler.RunAsync(compiled.Image!, context).GetAwaiter().GetResult();
        return result as IDriver
            ?? throw new InvalidOperationException($"Driver '{manifest.Id}': the script ends with {(result is null ? "no value" : $"a {result.GetType().Name}")}, not an IDriver. End it with an expression such as `new MyDriver()`, with no semicolon after it (a final `;` makes it a statement with no value).");
    }
}
