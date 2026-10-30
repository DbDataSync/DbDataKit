using System.IO.Compression;
using System.Text;

namespace DbDataSync.Tests.Shared;

/// <summary>
/// A throwaway folder feed in NuGet's global-packages layout (<c>id/version/id.version.nupkg</c> with its nuspec and hash beside it), for tests of the package pins (S12). The packages hold only a nuspec and
/// the files a test names, so nothing is downloaded and nothing in them is an assembly to load.
/// </summary>
public sealed class LocalPackageFeed : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "dbdatasync-feed-" + Guid.NewGuid().ToString("N")[..10]);

    public string Directory => Path.Combine(Root, "feed");

    /// <summary>Adds <c>id version</c> (replacing the package when it exists, as a feed that was tampered with would); <paramref name="marker"/> is a file's content, so two calls with different markers make different bytes.</summary>
    public void Add(string id, string version, string marker = "")
    {
        var dir = Path.Combine(Directory, id.ToLowerInvariant(), version.ToLowerInvariant());
        System.IO.Directory.CreateDirectory(dir);
        var nupkg = Path.Combine(dir, $"{id.ToLowerInvariant()}.{version.ToLowerInvariant()}.nupkg");
        File.Delete(nupkg);
        var nuspec = $"<?xml version=\"1.0\"?><package xmlns=\"http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd\"><metadata><id>{id}</id><version>{version}</version><authors>t</authors><description>t</description></metadata></package>";
        using (var zip = ZipFile.Open(nupkg, ZipArchiveMode.Create))
        {
            Write(zip, $"{id}.nuspec", nuspec);
            if (marker.Length > 0) Write(zip, "content/marker.txt", marker);
        }
        File.WriteAllText(Path.Combine(dir, id.ToLowerInvariant() + ".nuspec"), nuspec);
        File.WriteAllText(nupkg + ".sha512", DbDataKit.Scripting.PackageStore.HashOf(nupkg));
    }

    private static void Write(ZipArchive zip, string name, string text)
    {
        using var stream = zip.CreateEntry(name).Open();
        stream.Write(Encoding.UTF8.GetBytes(text));
    }

    public void Dispose()
    {
        try { System.IO.Directory.Delete(Root, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
