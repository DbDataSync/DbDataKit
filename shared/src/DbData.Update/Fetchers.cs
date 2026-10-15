using System.Net;
using System.Net.Http.Headers;

namespace DbData.Update;

public sealed class FileContentFetcher : IContentFetcher
{
    public Task<Stream> OpenReadAsync(Uri location, long offset, CancellationToken ct)
    {
        if (!location.IsFile) throw new NotSupportedException("Not a file location.");
        var fs = new FileStream(location.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        if (offset > 0) fs.Seek(Math.Min(offset, fs.Length), SeekOrigin.Begin);
        return Task.FromResult<Stream>(fs);
    }
}

public sealed class HttpContentFetcher(HttpClient http) : IContentFetcher
{
    public async Task<Stream> OpenReadAsync(Uri location, long offset, CancellationToken ct)
    {
        if (location.Scheme != Uri.UriSchemeHttps && location.Scheme != Uri.UriSchemeHttp) throw new NotSupportedException("Not an http location.");
        using var req = new HttpRequestMessage(HttpMethod.Get, location);
        if (offset > 0) req.Headers.Range = new RangeHeaderValue(offset, null);
        var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        try
        {
            if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) { resp.Dispose(); return Stream.Null; }
            resp.EnsureSuccessStatusCode();
            var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var wrapped = new ResponseStream(s, resp);
            // Server ignored Range (200 instead of 206): skip the bytes we already have.
            if (offset > 0 && resp.StatusCode == HttpStatusCode.OK)
            {
                var buf = new byte[81920];
                var left = offset;
                while (left > 0)
                {
                    var n = await wrapped.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, left)), ct).ConfigureAwait(false);
                    if (n == 0) break;
                    left -= n;
                }
            }
            return wrapped;
        }
        catch { resp.Dispose(); throw; }
    }

    private sealed class ResponseStream(Stream inner, HttpResponseMessage resp) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { inner.Dispose(); resp.Dispose(); }
            base.Dispose(disposing);
        }
    }
}

/// <summary>Routes by URI scheme: file to the file fetcher, http(s) to the http fetcher.</summary>
public sealed class CompositeContentFetcher(IContentFetcher? http, IContentFetcher? file) : IContentFetcher
{
    public Task<Stream> OpenReadAsync(Uri location, long offset, CancellationToken ct)
    {
        var f = location.IsFile ? file : http;
        return f is null ? throw new NotSupportedException($"No fetcher for scheme '{location.Scheme}'.") : f.OpenReadAsync(location, offset, ct);
    }
}

public static class ContentFetcherExtensions
{
    public static async Task<byte[]> ReadAllBytesAsync(this IContentFetcher fetcher, Uri location, int maxBytes, CancellationToken ct)
    {
        await using var s = await fetcher.OpenReadAsync(location, 0, ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = await s.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > maxBytes) throw new InvalidDataException($"Content from {location} exceeds {maxBytes} bytes.");
        }
        return ms.ToArray();
    }
}
