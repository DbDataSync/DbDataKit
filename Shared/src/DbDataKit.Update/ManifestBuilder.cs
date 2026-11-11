using System.Security.Cryptography;
using System.Text;

namespace DbDataKit.Update;

public sealed record ManifestBuildOptions
{
    public string Product { get; init; } = "";
    public string Version { get; init; } = "";
    public string Channel { get; init; } = "";
    /// <summary>Artifact URLs are BaseUrl + "/" + file name.</summary>
    public string BaseUrl { get; init; } = "";
    public string? MinUpdater { get; init; }
    public bool RequiresConfigMigration { get; init; }
    public Dictionary<string, int> SchemaVersions { get; init; } = new();
    public List<string> Supersedes { get; init; } = new();
    public string? MinimumSafeVersion { get; init; }
    public bool Revoked { get; init; }
    public string? Notes { get; init; }
    public DateTimeOffset? Published { get; init; }
}

/// <summary>Builds a release manifest from a directory of artifacts (rid and kind inferred from file names).</summary>
public static class ManifestBuilder
{
    private static readonly string[] Rids = ["win-x64", "win-x86", "win-arm64", "linux-x64", "linux-arm64", "linux-musl-x64", "osx-x64", "osx-arm64"];
    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase) { "release.json", "release.json.sig", "SHA256SUMS", "SHA256SUMS.txt" };

    public static string InferKind(string fileName)
    {
        var n = fileName.ToLowerInvariant();
        if (n.EndsWith(".tar.gz") || n.EndsWith(".tgz")) return "tarball";
        if (n.EndsWith(".zip")) return "zip";
        if (n.EndsWith(".nupkg")) return "nupkg";
        if (n.EndsWith(".mcpb")) return "mcpb";
        return "file";
    }

    public static string? InferRid(string fileName)
        => Rids.OrderByDescending(r => r.Length).FirstOrDefault(r => fileName.Contains(r, StringComparison.OrdinalIgnoreCase));

    public static ReleaseManifest Build(ManifestBuildOptions o, string artifactsDir)
    {
        var channel = string.IsNullOrEmpty(o.Channel)
            ? ReleaseVersion.Parse(o.Version).Channel.ToString().ToLowerInvariant()
            : o.Channel.ToLowerInvariant();
        var artifacts = Directory.EnumerateFiles(artifactsDir)
            .Where(f => !Skip.Contains(Path.GetFileName(f)))
            .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
            .Select(f =>
            {
                var name = Path.GetFileName(f);
                using var fs = File.OpenRead(f);
                return new ReleaseArtifact
                {
                    Rid = InferRid(name), Kind = InferKind(name), Name = name,
                    Url = o.BaseUrl.TrimEnd('/') + "/" + Uri.EscapeDataString(name),
                    Sha256 = Convert.ToHexStringLower(SHA256.HashData(fs)), Size = new FileInfo(f).Length,
                };
            }).ToList();
        var m = new ReleaseManifest
        {
            Product = o.Product, Version = ReleaseVersion.Parse(o.Version).ToString(), Channel = channel,
            Published = o.Published ?? DateTimeOffset.UtcNow, MinUpdater = o.MinUpdater,
            RequiresConfigMigration = o.RequiresConfigMigration, SchemaVersions = o.SchemaVersions, Artifacts = artifacts,
            Supersedes = o.Supersedes, Revoked = o.Revoked, MinimumSafeVersion = o.MinimumSafeVersion, Notes = o.Notes,
        };
        m.Validate();
        return m;
    }

    public static byte[] ToBytes(ReleaseManifest m) => new UTF8Encoding(false).GetBytes(m.ToJson() + "\n");
}
