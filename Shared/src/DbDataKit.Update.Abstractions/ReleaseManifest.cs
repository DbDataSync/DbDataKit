using System.Text.Json;
using System.Text.Json.Serialization;

namespace DbDataKit.Update;

public sealed record ReleaseArtifact
{
    public string? Rid { get; init; }
    /// <summary>service-zip, zip, tarball, nupkg, native, mcpb, file ...</summary>
    public string Kind { get; init; } = "file";
    public string? Name { get; init; }
    public string Url { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public long Size { get; init; }
    /// <summary>Optional per-artifact signature (informational; the manifest signature is the trust root).</summary>
    public string? Signature { get; init; }
}

/// <summary>The <c>release/1</c> manifest. Trust comes from the detached signature over its exact bytes, never from parsing.</summary>
public sealed record ReleaseManifest
{
    public const string SchemaId = "release/1";

    public string Schema { get; init; } = SchemaId;
    public string Product { get; init; } = "";
    public string Version { get; init; } = "";
    public string Channel { get; init; } = "stable";
    public DateTimeOffset? Published { get; init; }
    public string? MinUpdater { get; init; }
    public bool RequiresConfigMigration { get; init; }
    public Dictionary<string, int> SchemaVersions { get; init; } = new();
    public List<ReleaseArtifact> Artifacts { get; init; } = new();
    public List<string> Supersedes { get; init; } = new();
    public bool Revoked { get; init; }
    public string? MinimumSafeVersion { get; init; }
    public string? Notes { get; init; }

    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    public ReleaseVersion ParsedVersion => ReleaseVersion.Parse(Version);

    public DbDataKit.Update.Channel ParsedChannel => Channel.ToLowerInvariant() switch
    {
        "stable" => DbDataKit.Update.Channel.Stable,
        "beta" => DbDataKit.Update.Channel.Beta,
        "snapshot" => DbDataKit.Update.Channel.Snapshot,
        _ => throw new FormatException($"Unknown channel '{Channel}'."),
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Parse and structurally validate. Throws <see cref="FormatException"/> on any violation.</summary>
    public static ReleaseManifest Parse(ReadOnlySpan<byte> utf8)
    {
        ReleaseManifest? m;
        try { m = JsonSerializer.Deserialize<ReleaseManifest>(utf8, JsonOptions); }
        catch (JsonException ex) { throw new FormatException("Manifest is not valid JSON: " + ex.Message, ex); }
        if (m is null) throw new FormatException("Manifest is empty.");
        m.Validate();
        return m;
    }

    public void Validate()
    {
        if (Schema != SchemaId) throw new FormatException($"Unsupported manifest schema '{Schema}'.");
        if (string.IsNullOrWhiteSpace(Product)) throw new FormatException("Manifest product is missing.");
        var v = ReleaseVersion.TryParse(Version, out var pv) ? pv : throw new FormatException($"Bad version '{Version}'.");
        var ch = ParsedChannel;
        if (v.Channel != ch) throw new FormatException($"Version '{Version}' does not belong on channel '{Channel}'.");
        if (MinimumSafeVersion is not null && !ReleaseVersion.TryParse(MinimumSafeVersion, out _))
            throw new FormatException($"Bad minimumSafeVersion '{MinimumSafeVersion}'.");
        foreach (var s in Supersedes)
            if (!ReleaseVersion.TryParse(s, out _)) throw new FormatException($"Bad supersedes entry '{s}'.");
        foreach (var a in Artifacts)
        {
            if (string.IsNullOrWhiteSpace(a.Url)) throw new FormatException("Artifact url is missing.");
            if (a.Sha256.Length != 64 || !a.Sha256.All(Uri.IsHexDigit)) throw new FormatException($"Artifact '{a.Url}' has a bad sha256.");
            if (a.Size < 0) throw new FormatException($"Artifact '{a.Url}' has a negative size.");
        }
    }

    public ReleaseArtifact? FindArtifact(string rid, params string[] kinds)
        => Artifacts.FirstOrDefault(a => string.Equals(a.Rid, rid, StringComparison.OrdinalIgnoreCase)
                                         && (kinds.Length == 0 || kinds.Contains(a.Kind, StringComparer.OrdinalIgnoreCase)));
}
