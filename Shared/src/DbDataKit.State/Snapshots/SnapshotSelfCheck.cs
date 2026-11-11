using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DbDataKit.State.Snapshots;

/// <summary>What <see cref="SnapshotSelfCheck"/> found.</summary>
/// <param name="Problems">Every difference between the files and the manifest, each a sentence naming the file; empty when the snapshot is intact.</param>
public sealed record SnapshotSelfCheckResult(SnapshotManifest Manifest, IReadOnlyList<string> Problems, int Tables, long Rows, int PayloadFiles, long PayloadBytes)
{
    public bool Intact => Problems.Count == 0;
}

/// <summary>
/// Checks a snapshot against its own manifest without any database (<c>state verify --snapshot</c>): every table file is hashed (the manifest's <c>sha256</c>) and read line by line (the row count and the
/// <c>rows_digest</c>), every payload file is measured and hashed, and the chunk rows' markers are added up per payload (the offsets, lengths and chunk count the manifest records). Problems are collected, not
/// thrown at the first one, so one run names every damaged file. A manifest that is missing, of another format or damaged is a <see cref="SnapshotImportException"/>, as for an import.
/// </summary>
public static class SnapshotSelfCheck
{
    public static async Task<SnapshotSelfCheckResult> CheckAsync(string directory, IProgress<SnapshotProgress>? progress, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var root = Path.GetFullPath(directory);
        var manifest = StateSnapshotReader.ReadManifest(root);
        var problems = new List<string>();
        long rows = 0;
        var tables = 0;

        var chunkSums = new Dictionary<Guid, (long Bytes, int Chunks)>();
        foreach (var module in manifest.Modules)
        {
            foreach (var table in module.Tables)
            {
                ct.ThrowIfCancellationRequested();
                tables++;
                var name = $"{module.Name}.{table.Name}";
                string path;
                try { path = StateSnapshotReader.ResolveFile(root, table.File, "the table file"); }
                catch (SnapshotImportException ex) { problems.Add(ex.Message); continue; }

                progress?.Report(new SnapshotProgress("table-start", module.Name, table.Name, rows, 0));
                try { await StateSnapshotReader.VerifyFileAsync(path, table.File, "the table file", table.Sha256, null, ct).ConfigureAwait(false); }
                catch (SnapshotImportException ex) { problems.Add(ex.Message); }

                var counted = await CountRowsAsync(root, module.Name, table, chunkSums, problems, ct).ConfigureAwait(false);
                if (counted is { } c)
                {
                    rows += c.Rows;
                    if (c.Rows != table.Rows) problems.Add($"The table file '{table.File}' ({name}) has {c.Rows} rows and the manifest says {table.Rows}.");
                    else if (!string.Equals(c.Digest, table.RowsDigest, StringComparison.OrdinalIgnoreCase))
                        problems.Add($"The rows of the table file '{table.File}' ({name}) have the rows_digest {c.Digest} and the manifest says {table.RowsDigest}.");
                }

                progress?.Report(new SnapshotProgress("table-done", module.Name, table.Name, rows, 0));
            }
        }

        long bytes = 0;
        var seen = new HashSet<Guid>();
        foreach (var payload in manifest.Payloads)
        {
            ct.ThrowIfCancellationRequested();
            seen.Add(payload.Id);
            string path;
            try { path = StateSnapshotReader.ResolveFile(root, payload.File, "the payload file"); }
            catch (SnapshotImportException ex) { problems.Add(ex.Message); continue; }

            try { await StateSnapshotReader.VerifyFileAsync(path, payload.File, "the payload file", payload.Sha256, payload.Size, ct).ConfigureAwait(false); bytes += payload.Size; }
            catch (SnapshotImportException ex) { problems.Add(ex.Message); }

            if (chunkSums.TryGetValue(payload.Id, out var sum))
            {
                if (sum.Bytes != payload.Size || sum.Chunks != payload.Chunks)
                    problems.Add($"The chunk rows of the payload {payload.Id:D} add up to {sum.Bytes} bytes in {sum.Chunks} chunks and the manifest says {payload.Size} bytes in {payload.Chunks} chunks.");
            }
            else if (payload.Chunks != 0 || payload.Size != 0)
            {
                problems.Add($"The manifest lists the payload file '{payload.File}' and no chunk row of the snapshot refers to it.");
            }
        }

        foreach (var id in chunkSums.Keys.Where(id => !seen.Contains(id)))
            problems.Add($"The chunk rows refer to the payload {id:D}, which the manifest does not list.");

        progress?.Report(new SnapshotProgress("done", null, null, rows, bytes));
        return new SnapshotSelfCheckResult(manifest, problems, tables, rows, manifest.Payloads.Count, bytes);
    }

    private static async Task<(long Rows, string Digest)?> CountRowsAsync(string root, string module, SnapshotTable table, Dictionary<Guid, (long Bytes, int Chunks)> chunkSums, List<string> problems, CancellationToken ct)
    {
        var payloadColumn = table.Columns.ToList().FindIndex(c => c.Storage == SnapshotColumnStorage.PayloadFile);
        var digest = new RowsDigest();
        long rows = 0;
        var hash = new byte[32];
        try
        {
            using var lines = StateSnapshotReader.OpenTable(root, table);
            while (await lines.ReadAsync(ct).ConfigureAwait(false) is { } line)
            {
                SHA256.HashData(Encoding.UTF8.GetBytes(line), hash);
                digest.Add(hash);
                rows++;
                if (payloadColumn < 0) continue;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var marker = document.RootElement[payloadColumn];
                    var id = Guid.Parse(marker.GetProperty("payload").GetString()!);
                    var length = marker.GetProperty("length").GetInt64();
                    var offset = marker.GetProperty("offset").GetInt64();
                    chunkSums.TryGetValue(id, out var sum);
                    if (offset != sum.Bytes) problems.Add($"The table file '{table.File}' line {lines.LineNumber}: the chunk starts at offset {offset} of the payload {id:D} and the chunks before it end at {sum.Bytes}.");
                    chunkSums[id] = (sum.Bytes + length, sum.Chunks + 1);
                }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or IndexOutOfRangeException)
                {
                    problems.Add($"The table file '{table.File}' line {lines.LineNumber} is not a row of {module}.{table.Name}: {ex.Message}");
                }
            }
        }
        catch (SnapshotImportException ex)
        {
            problems.Add(ex.Message);
            return null;
        }

        return (rows, digest.Hex());
    }
}
