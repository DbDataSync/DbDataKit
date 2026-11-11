using System.Security.Cryptography;

namespace DbDataKit.State;

/// <summary>
/// Stores the files of a version through <see cref="PayloadStore"/> (plan 30 section 2, decision 2) and hands back the <see cref="ArtifactRef"/>s a publish names. A file whose SHA-256 is already stored,
/// by an earlier version or earlier in this batch, is not written again: look up by hash first, store only when it is new. One instance per publish.
/// </summary>
public sealed class PublishArtifacts(PayloadStore payloads, DefinitionPublisher publisher, string createdBy)
{
    private readonly Dictionary<string, Guid> _stored = new(StringComparer.Ordinal);

    /// <summary>Files this instance wrote (not found by hash), for a caller that reports what was uploaded.</summary>
    public int Written { get; private set; }

    /// <summary>Stores a file given as bytes.</summary>
    public ArtifactRef Add(string path, ReadOnlySpan<byte> content, string? mediaType = null)
    {
        var sha = Convert.ToHexStringLower(SHA256.HashData(content));
        if (Find(sha) is { } existing) return new ArtifactRef(path, sha, content.Length, mediaType, existing);
        using var writer = payloads.OpenWrite(new PayloadOptions(mediaType, CreatedBy: createdBy));
        writer.Write(content);
        writer.Complete();
        Written++;
        _stored[sha] = writer.Id;
        return new ArtifactRef(path, sha, content.Length, mediaType, writer.Id);
    }

    /// <summary>Stores a file on disk, streamed: the hash is taken first, and the bytes are read again only when the hash is new.</summary>
    public ArtifactRef AddFile(string path, string filePath, string? mediaType = null)
    {
        string sha;
        long size;
        using (var hashing = File.OpenRead(filePath))
        {
            size = hashing.Length;
            sha = Convert.ToHexStringLower(SHA256.HashData(hashing));
        }

        if (Find(sha) is { } existing) return new ArtifactRef(path, sha, size, mediaType, existing);
        using var source = File.OpenRead(filePath);
        using var writer = payloads.OpenWrite(new PayloadOptions(mediaType, CreatedBy: createdBy));
        source.CopyTo(writer);
        writer.Complete();
        Written++;
        _stored[sha] = writer.Id;
        return new ArtifactRef(path, sha, size, mediaType, writer.Id);
    }

    /// <summary>Stores every file under a directory, paths relative to it with forward slashes, in path order.</summary>
    public IReadOnlyList<ArtifactRef> AddDirectory(string directory, Func<string, string?>? mediaTypeOf = null) =>
        [.. Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(file => (File: file, Path: Path.GetRelativePath(directory, file).Replace('\\', '/')))
            .OrderBy(f => f.Path, StringComparer.Ordinal)
            .Select(f => AddFile(f.Path, f.File, mediaTypeOf?.Invoke(f.Path)))];

    /// <summary>The payload for a hash, whether a version stored it or this batch did; null when it is new.</summary>
    public Guid? Find(string sha256) => _stored.TryGetValue(sha256, out var id) ? id : publisher.FindArtifact(sha256);

    /// <summary>Opens a stored file of a version for reading (the hash is checked when it is read to its end).</summary>
    public static PayloadReader Open(PayloadStore payloads, DefinitionArtifact artifact) => payloads.OpenRead(artifact.PayloadId);
}
