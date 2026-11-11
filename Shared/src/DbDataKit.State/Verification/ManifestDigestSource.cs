using System.Text.Json;
using DbDataKit.State.Snapshots;

namespace DbDataKit.State.Verification;

/// <summary>
/// A snapshot's <c>manifest.json</c> as something a database can be verified against: each table's row count and <c>rows_digest</c>, each module's version and checksum. It reads the manifest only (no table
/// file), so it is quick, and it cannot list rows: it is an <see cref="ITableDigestSource"/> and not an <see cref="IRowDigestSource"/>, so a difference is reported as a count or digest mismatch naming the
/// table (what <see cref="StateVerifier"/> does when either side cannot name rows). The digest is the one <see cref="DatabaseDigestSource"/> computes (both use <see cref="SnapshotValues"/>), which is how a
/// database and the snapshot taken of it compare equal on any engine.
/// <para>
/// A table with a column whose values are in payload files (<c>payloads.chunk</c>) is not offered: its rows hold the offset of the bytes in the snapshot's payload files, which the database does not
/// have, so no digest of the database can equal it. Those bytes are checked through the payload files' own SHA-256 (the manifest's <c>payloads</c>) and the stored hash in <c>payloads.payload</c>.
/// </para>
/// </summary>
public sealed class ManifestDigestSource : ITableDigestSource
{
    private readonly string _directory;
    private readonly IReadOnlyList<ManifestModule> _modules;

    private sealed record ManifestModule(DigestModule Module, IReadOnlyDictionary<string, TableDigest> Digests, IReadOnlyList<string> NotOffered);

    /// <param name="snapshot">The snapshot's directory, or its <c>manifest.json</c>.</param>
    public ManifestDigestSource(string snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot);
        var file = Directory.Exists(snapshot) ? Path.Combine(snapshot, SnapshotFormat.ManifestFile) : snapshot;
        _directory = Path.GetDirectoryName(Path.GetFullPath(file))!;
        if (!File.Exists(file)) throw new FileNotFoundException($"The snapshot has no {SnapshotFormat.ManifestFile}: '{file}' does not exist.", file);

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(file));
            var format = document.RootElement.GetProperty("format").GetString();
            if (format != SnapshotFormat.Id) throw new InvalidDataException($"'{file}' is a '{format}', and this reads '{SnapshotFormat.Id}'.");
            _modules = [.. document.RootElement.GetProperty("modules").EnumerateArray().Select(ReadModule)];
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException($"'{file}' is not a snapshot manifest this can read: {ex.Message}", ex);
        }
    }

    /// <summary>The tables of the snapshot this source does not offer (<c>payloads.chunk</c>, see above), as <c>module.table</c>: a verification against a database reports each as a missing table, and a caller that knows why filters them out.</summary>
    public IReadOnlyList<string> NotOffered => [.. _modules.SelectMany(m => m.NotOffered.Select(t => $"{m.Module.Name}.{t}"))];

    public string Description => $"the snapshot in '{_directory}'";

    public IReadOnlyList<DigestModule> Modules() => [.. _modules.Select(m => m.Module)];

    public TableDigest Digest(string moduleKey, string table, VerifyOptions options, CancellationToken cancellationToken, Action<long>? onRows = null)
    {
        var module = _modules.FirstOrDefault(m => m.Module.Key == moduleKey) ?? throw new InvalidOperationException($"{Description} has no module '{moduleKey}'.");
        if (!module.Digests.TryGetValue(table, out var digest)) throw new InvalidOperationException($"{Description} has no table '{moduleKey}.{table}' to compare.");
        onRows?.Invoke(digest.Rows);
        return digest;
    }

    private static ManifestModule ReadModule(JsonElement module)
    {
        var name = module.GetProperty("name").GetString()!;
        var scope = module.TryGetProperty("scope_name", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        var key = scope is null ? name : $"{name}/{scope}";
        var digests = new Dictionary<string, TableDigest>(StringComparer.Ordinal);
        var notOffered = new List<string>();
        foreach (var table in module.GetProperty("tables").EnumerateArray())
        {
            var inPayloadFiles = table.GetProperty("columns").EnumerateArray().Any(c => c.TryGetProperty("storage", out var storage) && storage.ValueKind == JsonValueKind.String);
            if (inPayloadFiles)
            {
                notOffered.Add(table.GetProperty("name").GetString()!);
                continue;
            }

            digests[table.GetProperty("name").GetString()!] = new TableDigest(table.GetProperty("rows").GetInt64(), table.GetProperty("rows_digest").GetString()!);
        }

        var version = module.GetProperty("version").GetInt32();
        var checksum = module.GetProperty("checksum").GetString();
        return new ManifestModule(new DigestModule(key, name, version, checksum, [.. digests.Keys]), digests, notOffered);
    }
}
