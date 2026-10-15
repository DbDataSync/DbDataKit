using System.Security.Cryptography;

namespace DbData.Update;

/// <summary>Allow-list of URL prefixes artifacts may be downloaded from (e.g. the product's own GitHub releases/download/ path).</summary>
public sealed class TrustedUrlPolicy
{
    private readonly List<Uri> _prefixes;
    public bool AllowInsecureHttp { get; init; }

    public TrustedUrlPolicy(IEnumerable<string> prefixes)
    {
        _prefixes = prefixes.Select(p => new Uri(p.EndsWith('/') ? p : p + "/", UriKind.Absolute)).ToList();
    }

    public bool IsTrusted(Uri candidate)
    {
        var raw = candidate.OriginalString;
        if (raw.Contains('\\') && !candidate.IsFile) return false;
        var lower = raw.ToLowerInvariant();
        if (lower.Contains("%2e") || lower.Contains("%2f") || lower.Contains("%5c") || lower.Contains("..")) return false;
        if (!string.IsNullOrEmpty(candidate.UserInfo)) return false;
        if (candidate.Scheme == Uri.UriSchemeHttp && !AllowInsecureHttp) return false;
        foreach (var p in _prefixes)
        {
            if (!string.Equals(p.Scheme, candidate.Scheme, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(p.Host, candidate.Host, StringComparison.OrdinalIgnoreCase) || p.Port != candidate.Port) continue;
            var cmp = candidate.IsFile ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (candidate.AbsolutePath.StartsWith(p.AbsolutePath, cmp)) return true;
        }
        return false;
    }
}

public sealed class Downloader(IContentFetcher fetcher, TrustedUrlPolicy policy, long maxBytes = 2L * 1024 * 1024 * 1024)
{
    /// <summary>Resolve artifact.Url (absolute or relative to the manifest) and require it to be under a trusted prefix.</summary>
    public Uri ResolveTrusted(ReleaseArtifact artifact, Uri? manifestLocation)
    {
        Uri u;
        if (!Uri.TryCreate(artifact.Url, UriKind.Absolute, out var abs))
        {
            if (manifestLocation is null || !Uri.TryCreate(manifestLocation, artifact.Url, out var rel))
                throw new UpdateVerificationException(VerificationFailure.UntrustedUrl, $"Cannot resolve artifact url '{artifact.Url}'.");
            u = rel;
        }
        else u = abs;
        if (!policy.IsTrusted(u)) throw new UpdateVerificationException(VerificationFailure.UntrustedUrl, $"Artifact url '{artifact.Url}' is not under a trusted prefix.");
        return u;
    }

    /// <summary>
    /// Download to <c>destDir/&lt;sha256&gt;.partial</c> (resuming if present), verify size and sha256, then rename to <c>&lt;sha256&gt;.bin</c>.
    /// The file name derives from the signed hash, never from the URL.
    /// </summary>
    public async Task<string> DownloadAsync(ReleaseArtifact artifact, Uri? manifestLocation, string destDir, CancellationToken ct = default)
    {
        if (artifact.Size > maxBytes) throw new UpdateVerificationException(VerificationFailure.SizeMismatch, "Artifact exceeds the allowed size.");
        var uri = ResolveTrusted(artifact, manifestLocation);
        Directory.CreateDirectory(destDir);
        var final = Path.Combine(destDir, artifact.Sha256.ToLowerInvariant() + ".bin");
        var partial = Path.Combine(destDir, artifact.Sha256.ToLowerInvariant() + ".partial");

        if (File.Exists(final))
        {
            try { await ReleaseVerifier.VerifyFileAsync(final, artifact, ct).ConfigureAwait(false); return final; }
            catch (UpdateVerificationException) { File.Delete(final); }
        }

        long have = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (have > artifact.Size) { File.Delete(partial); have = 0; }
        if (have < artifact.Size)
        {
            await using var src = await fetcher.OpenReadAsync(uri, have, ct).ConfigureAwait(false);
            await using var dst = new FileStream(partial, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            var buf = new byte[81920];
            int n;
            long total = have;
            while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                total += n;
                if (total > artifact.Size) { dst.Close(); File.Delete(partial); throw new UpdateVerificationException(VerificationFailure.SizeMismatch, "Download is larger than the signed size."); }
                await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
            }
        }
        try { await ReleaseVerifier.VerifyFileAsync(partial, artifact, ct).ConfigureAwait(false); }
        catch (UpdateVerificationException ex) when (ex.Failure == VerificationFailure.HashMismatch)
        {
            File.Delete(partial); // corrupt or tampered: never resume from it
            throw;
        }
        catch (UpdateVerificationException ex) when (ex.Failure == VerificationFailure.SizeMismatch)
        {
            // truncated: keep the partial so the next attempt can resume
            throw new UpdateVerificationException(VerificationFailure.Download, "Download incomplete (resumable): " + ex.Message);
        }
        File.Move(partial, final, true);
        return final;
    }
}
