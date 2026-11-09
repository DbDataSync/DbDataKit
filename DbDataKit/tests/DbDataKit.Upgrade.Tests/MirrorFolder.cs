using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace DbDataKit.Upgrade.Tests;

/// <summary>A mirror folder in a temporary directory, with releases in it: what a site without internet reads the feed from.</summary>
public class MirrorFolder : IDisposable
{
    public const string Product = "dbdatatest";
    public const string New = "2026.11.9.1430";

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "dbdup-" + Guid.NewGuid().ToString("N"));

    public string Mirror => Path.Combine(Root, "mirror");

    public static string Rid => PlatformRid.Current;

    /// <summary>Puts a release in the mirror folder: its zip (a program folder with <c>version.txt</c>, and <c>broken</c> when asked), <c>SHA256SUMS</c>, and a <c>release.json</c> that states the generation when asked.</summary>
    public string Release(string version, bool broken = false, int? generation = null, string channelSuffix = "", bool listInSums = true)
    {
        var directory = Path.Combine(Mirror, Product, "v" + version + channelSuffix);
        Directory.CreateDirectory(directory);
        var zip = Path.Combine(directory, $"{Product}-v{version}{channelSuffix}-{Rid}.zip");
        using (var stream = File.Create(zip))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            Add(archive, "version.txt", version + channelSuffix);
            Add(archive, "dbdatatest", "#!/bin/sh\n");
            if (broken) Add(archive, "broken", "this build never becomes healthy");
        }

        var sums = new StringBuilder();
        if (listInSums) sums.Append(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(zip)))).Append(" *").Append(Path.GetFileName(zip)).Append('\n');
        File.WriteAllText(Path.Combine(directory, "SHA256SUMS"), sums.ToString());
        if (generation is { } g)
            File.WriteAllText(Path.Combine(directory, "release.json"), $"{{\"schema\":\"release/1\",\"product\":\"{Product}\",\"version\":\"{version}{channelSuffix}\",\"channel\":\"{(channelSuffix.Length == 0 ? "stable" : "beta")}\",\"schemaVersions\":{{\"generation\":{g}}}}}");
        return zip;
    }

    private static void Add(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    public virtual void Dispose()
    {
        try { Directory.Delete(Root, true); } catch (IOException) { }
    }
}
