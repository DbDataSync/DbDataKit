using System.IO.Compression;

namespace DbDataKit.Runner.Kit.Formats;

/// <summary>A compressed file that cannot be read: not gzip or zip at all, damaged, or larger once unpacked than the limit the job set. Permanent, and it names the file.</summary>
public sealed class CompressedFileException(string message, string? file = null, Exception? inner = null) : FileFormatException(message, file, inner);

/// <summary>An entry of a zip file.</summary>
public sealed record ZipEntryInfo(string Name, long Length, long CompressedLength, DateTimeOffset LastWriteTime);

/// <summary>
/// gzip and zip over streams (the framework's own code, with one guard: a limit on how much a compressed stream may unpack to, against a file that is small and unpacks to gigabytes). Nothing is written to disk: a job reads an entry as a stream and passes it to a reader of
/// <see cref="DelimitedReader"/> and its kin.
/// </summary>
public static class Compression
{
    static Compression()
    {
        // .NET reads a truncated gzip stream as a complete one unless this switch is on (it is read once, when the first GZipStream is made): a download cut short must not look like a small file
        AppContext.SetSwitch("System.IO.Compression.UseStrictValidation", true);
    }

    /// <summary>The default limit on the unpacked size of one stream: 4 GiB. A job that expects more says so.</summary>
    public const long DefaultMaxUnpackedBytes = 4L << 30;

    /// <summary>Whether the stream starts with the gzip signature (the stream must be seekable; it is left where it was).</summary>
    public static bool LooksLikeGzip(Stream stream)
    {
        var at = stream.Position;
        try { return stream.ReadByte() == 0x1f && stream.ReadByte() == 0x8b; }
        finally { stream.Position = at; }
    }

    /// <summary>Whether the stream starts with the zip signature (seekable; left where it was).</summary>
    public static bool LooksLikeZip(Stream stream)
    {
        var at = stream.Position;
        try { return stream.ReadByte() == 'P' && stream.ReadByte() == 'K'; }
        finally { stream.Position = at; }
    }

    /// <summary>The unpacked bytes of a gzip stream. A damaged or truncated stream, or one that unpacks past <paramref name="maxUnpackedBytes"/>, is a <see cref="CompressedFileException"/> naming <paramref name="file"/> when it is read.</summary>
    public static Stream Gunzip(Stream compressed, string? file = null, long maxUnpackedBytes = DefaultMaxUnpackedBytes, bool leaveOpen = true) =>
        new GuardedStream(new GZipStream(compressed, CompressionMode.Decompress, leaveOpen), file, maxUnpackedBytes, "gzip");

    /// <summary>A stream that gzips what is written to it; dispose it to finish the file.</summary>
    public static Stream Gzip(Stream destination, CompressionLevel level = CompressionLevel.Optimal, bool leaveOpen = true) => new GZipStream(destination, level, leaveOpen);

    /// <summary>The entries of a zip file (seekable stream). A file that is not a zip is a <see cref="CompressedFileException"/>.</summary>
    public static IReadOnlyList<ZipEntryInfo> ZipEntries(Stream zip, string? file = null)
    {
        using var archive = Open(zip, file);
        return [.. archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).Select(e => new ZipEntryInfo(e.FullName, e.Length, e.CompressedLength, e.LastWriteTime))];
    }

    /// <summary>One entry of a zip file as a stream, unpacked as it is read, with the same limit as <see cref="Gunzip"/>. The zip stream has to stay open while the entry is read; disposing the entry stream closes the archive (not <paramref name="zip"/>).</summary>
    public static Stream OpenZipEntry(Stream zip, string entryName, string? file = null, long maxUnpackedBytes = DefaultMaxUnpackedBytes)
    {
        var archive = Open(zip, file);
        var entry = archive.GetEntry(entryName);
        if (entry is null)
        {
            archive.Dispose();
            throw new CompressedFileException($"{Name(file)} has no entry named '{entryName}'.", file);
        }

        if (entry.Length > maxUnpackedBytes)
        {
            archive.Dispose();
            throw new CompressedFileException($"{Name(file)}: the entry '{entryName}' unpacks to {entry.Length} bytes, more than the limit of {maxUnpackedBytes}.", file);
        }

        try { return new GuardedStream(entry.Open(), file, maxUnpackedBytes, "zip", archive); }
        catch (InvalidDataException ex)
        {
            archive.Dispose();
            throw new CompressedFileException($"{Name(file)}: the entry '{entryName}' cannot be unpacked ({ex.Message}).", file, ex);
        }
    }

    /// <summary>A writer of a zip file: one stream per entry. Dispose to write the end of the file.</summary>
    public static ZipArchive CreateZip(Stream destination, bool leaveOpen = true) => new(destination, ZipArchiveMode.Create, leaveOpen);

    private static ZipArchive Open(Stream zip, string? file)
    {
        try { return new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true); }
        catch (InvalidDataException ex) { throw new CompressedFileException($"{Name(file)} is not a zip file, or is damaged.", file, ex); }
    }

    internal static string Name(string? file) => file is null ? "The compressed file" : $"'{file}'";

    /// <summary>Counts what comes out of a decompressor, refuses to go past the limit, and turns the decompressor's exceptions into one that names the file.</summary>
    private sealed class GuardedStream(Stream inner, string? file, long limit, string kind, IDisposable? owner = null) : Stream
    {
        private long _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            try { return Counted(inner.Read(buffer)); }
            catch (InvalidDataException ex) { throw Damaged(ex); }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try { return Counted(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)); }
            catch (InvalidDataException ex) { throw Damaged(ex); }
        }

        private int Counted(int n)
        {
            _read += n;
            if (_read > limit) throw new CompressedFileException($"{Name(file)} unpacks to more than the limit of {limit} bytes ({kind}); it is refused.", file);
            return n;
        }

        private CompressedFileException Damaged(Exception ex) => new($"{Name(file)} is damaged or cut short ({kind}).", file, ex);

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                owner?.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
