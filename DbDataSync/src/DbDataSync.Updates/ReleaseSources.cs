namespace DbDataSync.Updates;

/// <summary>
/// Every place an update can come from, as constants. Deliberately not configurable and not overridable
/// by a command-line option: a tool that will fetch a package and print the commands to install it must not
/// be pointable at an arbitrary URL, and a later phase lets the running service do the installing itself.
/// </summary>
public static class ReleaseSources
{
    public const string PackageId = "DbDataSync";

    /// <summary>The GitHub owner and repository releases are read from. DbDataSync lives in the DbDataKit monorepo,
    /// which publishes every product's releases, so a release belongs to this product only if its tag starts with
    /// <see cref="TagPrefix"/>.</summary>
    public const string Repository = "DbDataSync/DbDataKit";

    public const string GitHubReleasesUrl = "https://api.github.com/repos/" + Repository + "/releases";

    /// <summary>Every tag this product publishes starts with this. It is what tells DbDataSync's releases from the
    /// other products' in the one repository; the asset names (<see cref="PackageFileName"/>) are a second,
    /// independent check.</summary>
    public const string TagPrefix = "dbdatasync/";

    /// <summary>Every release's tag is this plus its version, whatever the channel: the version's own suffix says which
    /// (<c>dbdatasync/v2026.10.17.1234</c> stable, <c>dbdatasync/v2026.10.17.1234-beta</c> beta,
    /// <c>dbdatasync/v2026.10.17.1234-snapshot.gabc1234</c> snapshot).</summary>
    public const string ReleaseTagPrefix = TagPrefix + "v";

    /// <summary>Every release asset is downloaded from under this — anything else in a release listing is
    /// ignored rather than followed.</summary>
    public const string AssetDownloadPrefix = "https://github.com/" + Repository + "/releases/download/";

    public static string PackageFileName(ReleaseVersion version) => $"{PackageId}.{version.Text}.nupkg";

    public static string ChecksumFileName(ReleaseVersion version) => PackageFileName(version) + ".sha512";
}
