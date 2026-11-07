using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DbDataKit.State.Snapshots;

/// <summary>
/// Reads a snapshot written by <see cref="StateSnapshotWriter"/> (format <see cref="SnapshotFormat"/>) and checks it against itself: the manifest is parsed strictly, a table file is hashed and compared
/// with the manifest's <c>sha256</c>, and rows are streamed one line at a time. Nothing is ever written to the snapshot. Every problem is a <see cref="SnapshotImportException"/> naming the file.
/// </summary>
public static class StateSnapshotReader
{
    private static readonly HashSet<string> ColumnTypes = new(StringComparer.Ordinal)
    {
        SnapshotColumnTypes.Uuid, SnapshotColumnTypes.Time, SnapshotColumnTypes.Bool, SnapshotColumnTypes.Int, SnapshotColumnTypes.Decimal, SnapshotColumnTypes.Json, SnapshotColumnTypes.Text, SnapshotColumnTypes.Blob,
    };

    /// <summary>Reads <c>manifest.json</c> of a snapshot directory and refuses a missing file, a different format and anything that is not a manifest, naming the file.</summary>
    public static SnapshotManifest ReadManifest(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var path = Path.Combine(Path.GetFullPath(directory), SnapshotFormat.ManifestFile);
        if (!File.Exists(path))
            throw new SnapshotImportException(SnapshotImportRefusal.ManifestMissing, $"The snapshot has no {SnapshotFormat.ManifestFile} ('{path}'): the directory is not a snapshot, or the export that wrote it did not finish.");

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SnapshotImportException(SnapshotImportRefusal.ManifestCorrupt, $"The snapshot's {SnapshotFormat.ManifestFile} ('{path}') cannot be read: {ex.Message}", inner: ex);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes);
        }
        catch (JsonException ex)
        {
            throw new SnapshotImportException(SnapshotImportRefusal.ManifestCorrupt, $"The snapshot's {SnapshotFormat.ManifestFile} ('{path}') is not valid JSON: {ex.Message}", inner: ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String)
                throw new SnapshotImportException(SnapshotImportRefusal.ManifestCorrupt, $"The snapshot's {SnapshotFormat.ManifestFile} ('{path}') has no 'format' member: it is not a snapshot manifest.");
            if (format.GetString() != SnapshotFormat.Id)
                throw new SnapshotImportException(SnapshotImportRefusal.FormatUnsupported, $"The snapshot's {SnapshotFormat.ManifestFile} ('{path}') has the format '{format.GetString()}'; this version reads '{SnapshotFormat.Id}' only.");

            try
            {
                return Parse(root);
            }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException or OverflowException)
            {
                throw new SnapshotImportException(SnapshotImportRefusal.ManifestCorrupt, $"The snapshot's {SnapshotFormat.ManifestFile} ('{path}') is damaged: {ex.Message}", inner: ex);
            }
        }
    }

    private static SnapshotManifest Parse(JsonElement root)
    {
        var tool = Member(root, "tool", JsonValueKind.Object);
        var scope = Member(root, "scope", JsonValueKind.Object);
        var modules = Member(root, "modules", JsonValueKind.Array).EnumerateArray().Select(ParseModule).ToList();
        var payloads = Member(root, "payloads", JsonValueKind.Array).EnumerateArray().Select(p => new SnapshotPayloadFile(
            Guid.Parse(Text(p, "id")), Text(p, "file"), Member(p, "size", JsonValueKind.Number).GetInt64(), Text(p, "sha256"), Member(p, "chunks", JsonValueKind.Number).GetInt32())).ToList();
        return new SnapshotManifest(
            SnapshotFormat.Id,
            DateTimeOffset.Parse(Text(root, "created_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
            Text(root, "engine"),
            new SnapshotTool(Text(tool, "name"), Text(tool, "version")),
            new SnapshotScope(NullableText(scope, "group"), NullableText(scope, "service")),
            modules,
            payloads);
    }

    private static SnapshotModule ParseModule(JsonElement module) => new(
        Text(module, "name"), Text(module, "scope_kind"), NullableText(module, "scope_name"), Member(module, "version", JsonValueKind.Number).GetInt32(), Text(module, "checksum"),
        [.. Member(module, "tables", JsonValueKind.Array).EnumerateArray().Select(t => ParseTable(Text(module, "name"), t))]);

    private static SnapshotTable ParseTable(string module, JsonElement table)
    {
        var columns = Member(table, "columns", JsonValueKind.Array).EnumerateArray().Select(c =>
        {
            var type = Text(c, "type");
            if (!ColumnTypes.Contains(type)) throw new FormatException($"the column '{module}.{Text(table, "name")}.{Text(c, "name")}' has the type '{type}', which this version does not know");
            return new SnapshotColumn(Text(c, "name"), type, Member(c, "nullable", JsonValueKind.True, JsonValueKind.False).GetBoolean(),
                c.TryGetProperty("identity", out var identity) && identity.ValueKind == JsonValueKind.True, NullableText(c, "storage"));
        }).ToList();
        return new SnapshotTable(
            Text(table, "name"), Text(table, "physical_name"), Text(table, "file"), columns, [.. Member(table, "primary_key", JsonValueKind.Array).EnumerateArray().Select(k => k.GetString()!)],
            Member(table, "rows", JsonValueKind.Number).GetInt64(), Text(table, "sha256"), Text(table, "rows_digest"));
    }

    private static JsonElement Member(JsonElement element, string name, params JsonValueKind[] kinds)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) throw new FormatException($"the member '{name}' is missing");
        if (!kinds.Contains(value.ValueKind)) throw new FormatException($"the member '{name}' is a {value.ValueKind}");
        return value;
    }

    private static string Text(JsonElement element, string name) => Member(element, name, JsonValueKind.String).GetString()!;

    private static string? NullableText(JsonElement element, string name) =>
        !element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null ? null : Member(element, name, JsonValueKind.String).GetString();

    /// <summary>The full path of a file the manifest names; a path that leaves the snapshot directory is refused.</summary>
    internal static string ResolveFile(string root, string relative, string what)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.Ordinal))
            throw new SnapshotImportException(SnapshotImportRefusal.FileMissing, $"The manifest names {what} '{relative}', which is outside the snapshot directory '{root}'.");
        if (!File.Exists(full))
            throw new SnapshotImportException(SnapshotImportRefusal.FileMissing, $"The snapshot is missing {what} '{relative}' (expected at '{full}').");
        return full;
    }

    /// <summary>Hashes a whole file and compares it with the manifest (and, where given, its size); a difference is a <see cref="SnapshotImportRefusal.FileCorrupt"/> naming the file.</summary>
    internal static async Task VerifyFileAsync(string path, string relative, string what, string expectedSha256, long? expectedSize, CancellationToken ct)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long size = 0;
        await using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await file.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                sha.AppendData(buffer.AsSpan(0, read));
                size += read;
            }
        }

        CheckHash(relative, what, expectedSha256, Convert.ToHexStringLower(sha.GetHashAndReset()));
        if (expectedSize is { } expected && expected != size)
            throw new SnapshotImportException(SnapshotImportRefusal.FileCorrupt, $"{Capitalise(what)} '{relative}' is {size} bytes and the manifest says {expected}.");
    }

    internal static void CheckHash(string relative, string what, string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            throw new SnapshotImportException(SnapshotImportRefusal.FileCorrupt, $"{Capitalise(what)} '{relative}' was changed or damaged: its SHA-256 is {actual} and the manifest says {expected}.");
    }

    /// <summary>The lines of a table file, uncompressed, one at a time.</summary>
    internal static TableLines OpenTable(string root, SnapshotTable table) => new(ResolveFile(root, table.File, "the table file"), table.File);

    internal sealed class TableLines : IDisposable
    {
        private readonly FileStream _file;
        private readonly StreamReader _reader;
        private readonly string _relative;
        private long _line;

        public TableLines(string path, string relative)
        {
            _relative = relative;
            _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            _reader = new StreamReader(new GZipStream(_file, CompressionMode.Decompress), new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, 81920);
        }

        public long LineNumber => _line;

        /// <summary>The next row's line without its line break, or null at the end.</summary>
        public async ValueTask<string?> ReadAsync(CancellationToken ct)
        {
            try
            {
                var line = await _reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is not null) _line++;
                return line;
            }
            catch (InvalidDataException ex)
            {
                throw new SnapshotImportException(SnapshotImportRefusal.FileCorrupt, $"The table file '{_relative}' is damaged (not valid gzip data after line {_line}): {ex.Message}", inner: ex);
            }
        }

        public void Dispose()
        {
            _reader.Dispose();
            _file.Dispose();
        }
    }

    private static string Capitalise(string text) => char.ToUpperInvariant(text[0]) + text[1..];
}
