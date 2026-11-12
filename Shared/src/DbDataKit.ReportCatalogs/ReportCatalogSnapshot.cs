using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DbDataKit.Serving;
using DbDataKit.Views;

namespace DbDataKit.ReportCatalogs;

/// <summary>What the scheduled read keeps of one item of an external catalog (plan 32 RC02): the entry itself, and for a report its parameters, the formats it exports in and the policies the server states.</summary>
public sealed record ReportCatalogSnapshotItem(string Id, string Path, string Name, string Type, string? Description, string? Owner, DateTimeOffset? Modified,
    IReadOnlyList<ReportCatalogParameter> Parameters, IReadOnlyList<ReportCatalogExport> Exports, ReportCatalogPermissions Permissions);

/// <summary>The <c>report-catalog/1</c> document: every folder and item of one external catalog as the service account sees it, in path order. It carries no read time, so a catalog that did not change reads as the same bytes and the same hash.</summary>
public sealed record ReportCatalogSnapshot(string Provider, IReadOnlyList<ReportCatalogSnapshotItem> Items)
{
    /// <summary>The number of the format (<c>schemaVersion</c>); the registry of formats lists it.</summary>
    public const int Current = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <summary>The document as JSON text: <c>schemaVersion</c> first, then the provider and the items.</summary>
    public string ToJson() => JsonSerializer.Serialize(new Document(Current, Provider, Items), Json);

    /// <summary>The hash of the text (<c>sha256:</c> and 64 hex digits): what a second read compares to know nothing changed.</summary>
    public string Hash() => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ToJson())));

    private sealed record Document(int SchemaVersion, string Provider, IReadOnlyList<ReportCatalogSnapshotItem> Items);

    /// <summary>Reads a snapshot back; a document of another number, or one that is not a snapshot, is refused in words.</summary>
    public static ReportCatalogSnapshot Parse(string json)
    {
        Document? doc;
        try { doc = JsonSerializer.Deserialize<Document>(json, Json); }
        catch (JsonException e) { throw new FormatException($"Not a report-catalog snapshot: {e.Message}", e); }
        if (doc is null) throw new FormatException("Not a report-catalog snapshot: the document is empty.");
        if (doc.SchemaVersion != Current) throw new FormatException($"A report-catalog snapshot is read at format {Current}; this one says {doc.SchemaVersion}.");
        return new ReportCatalogSnapshot(doc.Provider, doc.Items);
    }
}

/// <summary>Reads a whole external catalog into a snapshot (plan 32 RC02): the folders from the root down, and for each report its parameters, exports and policies.</summary>
public static class ReportCatalogReader
{
    /// <summary>The caller a scheduled read acts as: no person, no roles. The provider reads as its own service account whatever this says.</summary>
    public static ReportCatalogViewer ServiceViewer { get; } = new(new ServingCaller("report-catalog-reader", "Report catalog reader", new HashSet<string>(), new Dictionary<string, string>()));

    /// <summary>Any failure of the provider, a refused service account included, ends the read: a half snapshot would hide items. The exception is the provider's own (<see cref="ReportCatalogException"/>).</summary>
    public static async Task<ReportCatalogSnapshot> ReadAsync(IReportCatalogProvider provider, CancellationToken cancellationToken = default)
    {
        var items = new List<ReportCatalogSnapshotItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var folders = new Queue<string?>();
        folders.Enqueue(null);
        while (folders.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = folders.Dequeue();
            foreach (var item in await provider.BrowseAsync(ServiceViewer, new ReportCatalogQuery(folder), cancellationToken).ConfigureAwait(false))
            {
                if (!seen.Add(item.Id)) continue;
                var folderItem = item.IsFolder;
                var parameters = folderItem ? [] : await provider.ParametersAsync(ServiceViewer, item.Id, cancellationToken).ConfigureAwait(false);
                var exports = folderItem ? [] : await provider.ExportsAsync(ServiceViewer, item.Id, cancellationToken).ConfigureAwait(false);
                var permissions = await provider.PermissionsAsync(ServiceViewer, item.Id, cancellationToken).ConfigureAwait(false);
                items.Add(new ReportCatalogSnapshotItem(item.Id, item.Path, item.Name, item.Type, item.Description, item.Owner, item.Modified, parameters, exports, permissions));
                if (folderItem) folders.Enqueue(item.Path);
            }
        }
        return new ReportCatalogSnapshot(provider.Provider, [.. items.OrderBy(i => i.Path, StringComparer.Ordinal)]);
    }
}

/// <summary>The latest snapshot a channel holds (what the registry says), by version and hash.</summary>
public sealed record StoredCatalogSnapshot(int Version, string Hash);

/// <summary>Where a snapshot goes: plan 30's registry in the host (a kind of its own, RC02's host half); a test's list in the tests.</summary>
public interface IReportCatalogSnapshotSink
{
    Task<StoredCatalogSnapshot?> LatestAsync(string provider, CancellationToken cancellationToken = default);

    /// <summary>Stores the snapshot as the next version and returns its number.</summary>
    Task<int> PublishAsync(string provider, string json, string hash, CancellationToken cancellationToken = default);
}

/// <summary>What a scheduled read did: <see cref="Published"/> (a new version), <see cref="Unchanged"/> (the catalog reads as the last version did: nothing published) or <see cref="Failed"/> (the last version stays, and <see cref="Reason"/> says why).</summary>
public sealed record ReportCatalogReadResult(string Outcome, int? Version = null, string? Reason = null)
{
    public const string Published = "published";
    public const string Unchanged = "unchanged";
    public const string Failed = "failed";
}

/// <summary>One scheduled read of an external catalog (plan 32 RC02): read, compare with the last version, publish only a change; a failing read leaves the last version and says why.</summary>
public sealed class ReportCatalogSnapshotJob(IReportCatalogProvider provider, IReportCatalogSnapshotSink sink)
{
    public async Task<ReportCatalogReadResult> RunAsync(CancellationToken cancellationToken = default)
    {
        ReportCatalogSnapshot snapshot;
        try
        {
            snapshot = await ReportCatalogReader.ReadAsync(provider, cancellationToken).ConfigureAwait(false);
        }
        catch (ReportCatalogException e)
        {
            return new ReportCatalogReadResult(ReportCatalogReadResult.Failed, Reason: $"{e.Code}: {e.Message}");
        }
        var hash = snapshot.Hash();
        var latest = await sink.LatestAsync(provider.Provider, cancellationToken).ConfigureAwait(false);
        if (latest?.Hash == hash) return new ReportCatalogReadResult(ReportCatalogReadResult.Unchanged, latest.Version);
        var version = await sink.PublishAsync(provider.Provider, snapshot.ToJson(), hash, cancellationToken).ConfigureAwait(false);
        return new ReportCatalogReadResult(ReportCatalogReadResult.Published, version);
    }
}
