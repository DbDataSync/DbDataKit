using System.Formats.Tar;
using System.IO.Compression;

namespace DbDataKit.Update;

public sealed record ExtractionLimits(int MaxEntries = 100_000, long MaxTotalBytes = 4L * 1024 * 1024 * 1024);

/// <summary>Extraction that refuses zip-slip, absolute paths, drive letters, link escapes and archive bombs.</summary>
public static class ArchiveExtractor
{
    public static string ResolveSafe(string destRoot, string entryName)
    {
        if (string.IsNullOrEmpty(entryName) || entryName.Contains('\0')) throw Unsafe(entryName, "empty or NUL name");
        var name = entryName.Replace('\\', '/');
        if (name.StartsWith('/') || (name.Length >= 2 && name[1] == ':')) throw Unsafe(entryName, "rooted path");
        foreach (var seg in name.Split('/'))
            if (seg == "..") throw Unsafe(entryName, "parent segment");
        var root = Path.GetFullPath(destRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, name));
        if (!full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) && full + Path.DirectorySeparatorChar != root)
            throw Unsafe(entryName, "escapes destination");
        return full;
    }

    private static UpdateVerificationException Unsafe(string name, string why)
        => new(VerificationFailure.UnsafeArchive, $"Unsafe archive entry '{name}': {why}.");

    public static void Extract(string archivePath, string destDir, ExtractionLimits? limits = null)
    {
        var l = limits ?? new ExtractionLimits();
        var lower = archivePath.ToLowerInvariant();
        // The staged file is named by hash (.bin), so sniff by magic bytes first.
        using var probe = File.OpenRead(archivePath);
        Span<byte> magic = stackalloc byte[4];
        var read = probe.Read(magic);
        probe.Dispose();
        if (read >= 2 && magic[0] == 0x50 && magic[1] == 0x4B) ExtractZip(archivePath, destDir, l);
        else if (read >= 2 && magic[0] == 0x1F && magic[1] == 0x8B) ExtractTar(archivePath, destDir, true, l);
        else if (lower.EndsWith(".tar")) ExtractTar(archivePath, destDir, false, l);
        else throw new UpdateVerificationException(VerificationFailure.UnsafeArchive, "Unrecognised archive format.");
    }

    public static void ExtractZip(string zipPath, string destDir, ExtractionLimits? limits = null)
    {
        var l = limits ?? new ExtractionLimits();
        Directory.CreateDirectory(destDir);
        using var zip = ZipFile.OpenRead(zipPath);
        if (zip.Entries.Count > l.MaxEntries) throw Unsafe(zipPath, "too many entries");
        long total = 0;
        // Validate everything before writing anything.
        foreach (var e in zip.Entries) { ResolveSafe(destDir, e.FullName); total += e.Length; }
        if (total > l.MaxTotalBytes) throw Unsafe(zipPath, "declared size too large");
        long written = 0;
        foreach (var e in zip.Entries)
        {
            var target = ResolveSafe(destDir, e.FullName);
            if (e.FullName.EndsWith('/') || e.FullName.EndsWith('\\')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var src = e.Open();
            using var dst = File.Create(target);
            written += CopyBounded(src, dst, l.MaxTotalBytes - written);
        }
    }

    public static void ExtractTar(string path, string destDir, bool gzip, ExtractionLimits? limits = null)
    {
        var l = limits ?? new ExtractionLimits();
        Directory.CreateDirectory(destDir);
        using var raw = File.OpenRead(path);
        using Stream s = gzip ? new GZipStream(raw, CompressionMode.Decompress) : raw;
        using var reader = new TarReader(s);
        int count = 0;
        long written = 0;
        TarEntry? e;
        while ((e = reader.GetNextEntry()) is not null)
        {
            if (++count > l.MaxEntries) throw Unsafe(path, "too many entries");
            var target = ResolveSafe(destDir, e.Name);
            switch (e.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(target);
                    break;
                case TarEntryType.RegularFile or TarEntryType.V7RegularFile:
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using (var dst = File.Create(target))
                        if (e.DataStream is not null) written += CopyBounded(e.DataStream, dst, l.MaxTotalBytes - written);
                    if (!OperatingSystem.IsWindows())
                        File.SetUnixFileMode(target, e.Mode & (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute));
                    break;
                case TarEntryType.SymbolicLink or TarEntryType.HardLink:
                    // Links are never materialised: they are the classic route to writing outside the root.
                    throw Unsafe(e.Name, "link entries are not allowed");
                case TarEntryType.ExtendedAttributes or TarEntryType.GlobalExtendedAttributes:
                    break;
                default:
                    throw Unsafe(e.Name, $"entry type {e.EntryType} is not allowed");
            }
        }
    }

    private static long CopyBounded(Stream src, Stream dst, long remaining)
    {
        var buf = new byte[81920];
        long total = 0;
        int n;
        while ((n = src.Read(buf, 0, buf.Length)) > 0)
        {
            total += n;
            if (total > remaining) throw new UpdateVerificationException(VerificationFailure.UnsafeArchive, "Archive expands beyond the allowed size.");
            dst.Write(buf, 0, n);
        }
        return total;
    }
}
