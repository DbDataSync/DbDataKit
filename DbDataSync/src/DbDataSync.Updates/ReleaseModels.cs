namespace DbDataSync.Updates;

/// <summary>How stable a promise a build is. All three are GitHub releases of the DbDataKit repository: <c>Stable</c>
/// is a release, <c>Beta</c> a prerelease cut on demand, <c>Snapshot</c> a prerelease per promoted <c>test</c> build.</summary>
public enum ReleaseChannel
{
    Stable,
    Beta,
    Snapshot,
}

/// <summary>
/// One installable version on one channel.
/// </summary>
/// <param name="PackageUrl">Where the release's nupkg is downloaded from.</param>
/// <param name="ChecksumUrl">The release's <c>.nupkg.sha512</c> sidecar; null with <see cref="PackageUrl"/>.</param>
/// <param name="ReleaseUrl">A page a human can read about it, when there is one.</param>
public sealed record ReleaseInfo(
    ReleaseVersion Version,
    ReleaseChannel Channel,
    Uri? PackageUrl = null,
    Uri? ChecksumUrl = null,
    Uri? ReleaseUrl = null)
{
    public DateTimeOffset? BuiltUtc => Version.BuiltUtc;
}

/// <summary>
/// A release source could not be read, or what it returned could not be trusted — carries a sentence an
/// operator can act on, not an HTTP status, so the CLI can print <see cref="Exception.Message"/> as it is.
/// </summary>
public sealed class ReleaseSourceException(string message, Exception? inner = null) : Exception(message, inner);
