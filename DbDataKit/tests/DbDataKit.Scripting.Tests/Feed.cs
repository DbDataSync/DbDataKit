using System.IO.Compression;
using System.Text;

namespace DbDataKit.Scripting.Tests;

/// <summary>A throwaway folder feed in the global-packages layout (<c>id/version/id.version.nupkg</c> with its nuspec and hash beside it) with packages made here: a nuspec with dependencies and files of the contents a test needs. Nothing is downloaded in these tests.</summary>
public sealed class Feed : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "dbdata-scripting-" + Guid.NewGuid().ToString("N")[..10]);
    public string Directory { get; }

    public Feed() { Directory = Path.Combine(Root, "feed"); System.IO.Directory.CreateDirectory(Directory); }

    public string Cache => Path.Combine(Root, "cache");

    public sealed record Dep(string Id, string Range, string Group = "net10.0");

    /// <summary>Makes <c>id version</c> with the given files (path inside the package to its bytes) and dependencies per target framework group.</summary>
    public string Add(string id, string version, IEnumerable<string>? files = null, IEnumerable<Dep>? dependencies = null, IEnumerable<(string Name, byte[] Bytes)>? raw = null)
    {
        var dir = Path.Combine(Directory, id.ToLowerInvariant(), version.ToLowerInvariant());
        System.IO.Directory.CreateDirectory(dir);
        var nupkg = Path.Combine(dir, $"{id.ToLowerInvariant()}.{version.ToLowerInvariant()}.nupkg");
        File.Delete(nupkg);                                                  // adding again replaces the package, as a feed that was tampered with would
        var groups = (dependencies ?? []).GroupBy(d => d.Group).ToList();
        var nuspec = new StringBuilder($"<?xml version=\"1.0\"?><package xmlns=\"http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd\"><metadata><id>{id}</id><version>{version}</version><authors>t</authors><description>t</description><dependencies>");
        foreach (var g in groups)
        {
            nuspec.Append($"<group targetFramework=\"{g.Key}\">");
            foreach (var d in g) nuspec.Append($"<dependency id=\"{d.Id}\" version=\"{d.Range}\" />");
            nuspec.Append("</group>");
        }
        nuspec.Append("</dependencies></metadata></package>");
        using (var zip = ZipFile.Open(nupkg, ZipArchiveMode.Create))
        {
            Write(zip, $"{id}.nuspec", Encoding.UTF8.GetBytes(nuspec.ToString()));
            foreach (var f in files ?? []) Write(zip, f, Encoding.UTF8.GetBytes("not a real assembly: " + f));
            foreach (var (name, bytes) in raw ?? []) Write(zip, name, bytes);
        }
        // a folder is a feed when it is laid out as NuGet's global-packages folder is: the nuspec and the hash sit beside the .nupkg
        File.WriteAllText(Path.Combine(dir, id.ToLowerInvariant() + ".nuspec"), nuspec.ToString());
        File.WriteAllText(nupkg + ".sha512", PackageStore.HashOf(nupkg));
        return nupkg;
    }

    private static void Write(ZipArchive zip, string name, byte[] bytes)
    {
        var entry = zip.CreateEntry(name);
        using var s = entry.Open();
        s.Write(bytes);
    }

    public PackageResolverOptions Options(Action<List<ResolutionWarning>>? _ = null, IReadOnlyList<PackageSource>? sources = null, LockFile? lockFile = null, bool offline = false, bool requirePinned = false, IReadOnlyList<string>? rids = null,
        Func<NuGet.Packaging.Core.PackageIdentity, bool>? allow = null, Func<string, bool>? hostProvides = null) => new()
    {
        Store = new PackageStore(Cache),
        Sources = sources ?? [new PackageSource(Directory)],
        Lock = lockFile ?? new LockFile(),
        Offline = offline,
        RequirePinned = requirePinned,
        Rids = rids ?? ["linux-x64", "linux", "unix", "any"],
        IsAllowed = allow ?? (_ => true),
        HostProvides = hostProvides ?? (_ => false),
    };

    public void Dispose() { try { System.IO.Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
