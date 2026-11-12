using System.Text.Json;
using DbDataKit.State;

namespace DbDataKit.ReportCatalogs;

/// <summary>
/// The kind <c>report-catalog</c> in plan 30's registry (plan 32 RC02): one definition per catalog (its name is the provider's name, <c>ssrs</c> or what the connection's <c>report_catalog.name</c> says), one version per change of what the scheduled read finds, per channel. The content is the
/// <c>report-catalog/1</c> document (<see cref="ReportCatalogSnapshot"/>). It is data a host reads, not code: target type <c>serving</c>, served at once, no approval.
/// </summary>
public static class ReportCatalogDefinitions
{
    public const string Kind = "report-catalog";

    /// <summary>Registers the kind with the validator that reads the document back (a document of another number, or one that is not a snapshot, is refused; so is one whose provider is not the definition's name).</summary>
    public static DefinitionKinds Register(DefinitionKinds kinds) =>
        kinds.Register(new DefinitionKind(Kind, ["serving"], PublishMode.Serve) { Formats = DefinitionFormats.Of(ReportCatalogSnapshot.Current), FormatOf = FormatOf, Validator = Validate });

    /// <summary>The document's <c>schema_version</c>, or null when the text does not say.</summary>
    public static int? FormatOf(string manifest)
    {
        try
        {
            using var doc = JsonDocument.Parse(manifest);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("schema_version", out var v) && v.TryGetInt32(out var n) && n >= 1 ? n : null;
        }
        catch (JsonException) { return null; }
    }

    private static string? Validate(DefinitionCandidate candidate)
    {
        try
        {
            var snapshot = ReportCatalogSnapshot.Parse(candidate.Manifest);
            return string.Equals(snapshot.Provider, candidate.Name, StringComparison.Ordinal) ? null : $"The snapshot is of the catalog '{snapshot.Provider}', and the definition is named '{candidate.Name}'.";
        }
        catch (FormatException e) { return e.Message; }
    }
}

/// <summary>
/// <see cref="IReportCatalogSnapshotSink"/> over the registry: a snapshot is a version of the <c>report-catalog</c> definition named for the provider, in one channel. The latest is what the channel serves; a publish of the content the channel already serves is no new version
/// (the registry stores the same content once), which the job has checked before it publishes.
/// </summary>
public sealed class DefinitionStoreSnapshotSink(DefinitionPublisher publisher, DefinitionStore store, string by, string channel = DefinitionStore.Production) : IReportCatalogSnapshotSink
{
    public Task<StoredCatalogSnapshot?> LatestAsync(string provider, CancellationToken cancellationToken = default)
    {
        if (store.Current(ReportCatalogDefinitions.Kind, provider, channel) is not { } current) return Task.FromResult<StoredCatalogSnapshot?>(null);
        // the hash is the snapshot's own (what the job compares), not the registry's: read it from the content
        try { return Task.FromResult<StoredCatalogSnapshot?>(new StoredCatalogSnapshot(current.Version, ReportCatalogSnapshot.Parse(current.Content).Hash())); }
        catch (FormatException) { return Task.FromResult<StoredCatalogSnapshot?>(null); }
    }

    public Task<int> PublishAsync(string provider, string json, string hash, CancellationToken cancellationToken = default)
    {
        var result = publisher.Publish(new PublishRequest(ReportCatalogDefinitions.Kind, provider, json, by, channel, PublishMode.Serve, Note: "scheduled read " + hash[..Math.Min(hash.Length, 19)]));
        return Task.FromResult(result.Version.Version);
    }
}
