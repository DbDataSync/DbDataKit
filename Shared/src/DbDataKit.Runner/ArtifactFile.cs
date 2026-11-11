namespace DbDataKit.Runner;

/// <summary>
/// One file of a published version (plan 30 sections 3 and 6): where it goes in the package, what its bytes must hash to, and how a source finds them.
/// </summary>
/// <param name="Path">Relative, with <c>/</c> separators; validated when the version is materialized.</param>
/// <param name="Sha256">Hex of the content (either case); the cache's key and what every download is verified against.</param>
/// <param name="Size">Bytes.</param>
/// <param name="MediaType">Informational.</param>
/// <param name="PayloadId">The payload that holds the bytes, for a source over <c>PayloadStore</c>; other sources may ignore it.</param>
public sealed record ArtifactFile(string Path, string Sha256, long Size, string? MediaType = null, Guid? PayloadId = null);

/// <summary>Where a version's files come from: the list of them, and the content of one as a stream.</summary>
public interface IArtifactSource
{
    /// <summary>The files of a version, in any order.</summary>
    Task<IReadOnlyList<ArtifactFile>> ListAsync(string versionKey, CancellationToken cancellationToken = default);

    /// <summary>Opens the content of one file; the caller disposes the stream. The bytes are not trusted: the cache hashes them as it reads.</summary>
    Task<Stream> OpenAsync(ArtifactFile file, CancellationToken cancellationToken = default);
}

/// <summary>An artifact was refused: a bad path, a bad descriptor, or content that is not what its descriptor says.</summary>
public class ArtifactException(string message) : Exception(message);

/// <summary>The content of a file did not hash to what its descriptor says.</summary>
public sealed class ArtifactHashMismatchException(string path, string expected, string actual)
    : ArtifactException($"The artifact '{path}' is not what was published: its content hashes to {actual}, but the version says {expected}. Nothing of it was kept.")
{
    public string Path { get; } = path;
    public string ExpectedSha256 { get; } = expected;
    public string ActualSha256 { get; } = actual;
}
