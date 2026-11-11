using System.Collections.Concurrent;
using System.Security.Cryptography;
using DbDataKit.State;

namespace DbDataKit.Runner;

/// <summary>
/// An <see cref="IArtifactSource"/> over the payload store (plan 29 section 3.6, DR13): a file's descriptor carries its payload id. The version-to-files table is held here in
/// memory; plan 30's <c>version_artifact</c> table replaces it by implementing <see cref="IArtifactSource"/> itself and reading the same payloads.
/// </summary>
public sealed class PayloadArtifactSource(PayloadStore payloads) : IArtifactSource
{
    private readonly ConcurrentDictionary<string, List<ArtifactFile>> _versions = new(StringComparer.Ordinal);

    /// <summary>Stores a file's content as a payload and records it in the version; returns its descriptor.</summary>
    public async Task<ArtifactFile> AddAsync(string versionKey, string path, Stream content, string? mediaType = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(versionKey);
        ArgumentNullException.ThrowIfNull(content);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long size = 0;
        Guid id;
        await using (var writer = payloads.OpenWrite(new PayloadOptions(MediaType: mediaType, OwnerModule: "runner", OwnerRow: versionKey)))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                size += read;
                await writer.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            writer.Complete();
            id = writer.Id;
        }

        var file = new ArtifactFile(path, Convert.ToHexStringLower(hash.GetHashAndReset()), size, mediaType, id);
        var list = _versions.GetOrAdd(versionKey, _ => []);
        lock (list) list.Add(file);
        return file;
    }

    public Task<IReadOnlyList<ArtifactFile>> ListAsync(string versionKey, CancellationToken cancellationToken = default)
    {
        if (!_versions.TryGetValue(versionKey, out var list))
            throw new ArtifactException($"The version '{versionKey}' has no files recorded.");
        lock (list) return Task.FromResult<IReadOnlyList<ArtifactFile>>([.. list]);
    }

    public Task<Stream> OpenAsync(ArtifactFile file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.PayloadId is not { } id)
            throw new ArtifactException($"The artifact '{file.Path}' has no payload id, so the payload store cannot supply it.");
        return Task.FromResult<Stream>(payloads.OpenRead(id));
    }
}
