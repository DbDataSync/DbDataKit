using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using DbDataKit.Connections.Http;
using DbDataKit.Secrets;

namespace DbDataKit.Connections.Files;

/// <summary>
/// Amazon's Signature Version 4 for S3 (the one signing algorithm of S3 and every server that speaks its API): the <c>Authorization</c> header of a request from its parts. Kept pure so that it is tested against the examples Amazon's documentation gives, with their known signatures.
/// </summary>
internal static class AwsSigV4
{
    public const string Algorithm = "AWS4-HMAC-SHA256";

    /// <summary>The hash of an empty payload (a GET).</summary>
    public const string EmptyPayload = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    /// <summary>A URI component encoded as S3 wants: every byte but the unreserved ones as %XX (a <c>/</c> kept between the segments of a path).</summary>
    public static string Encode(string value, bool keepSlash)
    {
        var builder = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~' || (keepSlash && c == '/')) builder.Append(c);
            else builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    /// <summary>The signature and the header. <paramref name="headers"/> are the headers that are signed (lower-case name, trimmed value; <c>host</c>, <c>x-amz-content-sha256</c> and <c>x-amz-date</c> among them); <paramref name="query"/> the decoded query pairs.</summary>
    public static (string Authorization, string Signature) Authorization(string method, string path, IEnumerable<KeyValuePair<string, string>> query, IReadOnlyDictionary<string, string> headers, string payloadHash, string accessKey, string secretKey, string region, string service, DateTimeOffset now)
    {
        var date = now.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var stamp = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var canonicalQuery = string.Join('&', query.Select(p => (Encode(p.Key, false), Encode(p.Value, false))).OrderBy(p => p.Item1, StringComparer.Ordinal).ThenBy(p => p.Item2, StringComparer.Ordinal).Select(p => p.Item1 + "=" + p.Item2));
        var signed = headers.OrderBy(h => h.Key, StringComparer.Ordinal).ToList();
        var canonicalHeaders = string.Concat(signed.Select(h => h.Key + ":" + h.Value.Trim() + "\n"));
        var signedHeaders = string.Join(';', signed.Select(h => h.Key));
        var canonical = string.Join('\n', method, Encode(path, true) is { Length: > 0 } encodedPath ? encodedPath : "/", canonicalQuery, canonicalHeaders, signedHeaders, payloadHash);
        var scope = $"{date}/{region}/{service}/aws4_request";
        var toSign = string.Join('\n', Algorithm, stamp, scope, Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))));
        var key = Hmac(Hmac(Hmac(Hmac(Encoding.UTF8.GetBytes("AWS4" + secretKey), date), region), service), "aws4_request");
        var signature = Hex(Hmac(key, toSign));
        return ($"{Algorithm} Credential={accessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}", signature);
    }

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    public static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}

/// <summary>Azure Storage's Shared Key authorization: the <c>Authorization</c> header from the request's parts. Pure, so that it is checked against the emulator, which verifies every signature.</summary>
internal static class AzureSharedKey
{
    public const string Version = "2023-11-03";

    /// <param name="account">The storage account's name.</param>
    /// <param name="path">The path of the request as sent (<c>/container/blob</c>; against the emulator it starts with the account name too, which is why the account appears twice in what is signed).</param>
    /// <param name="query">The decoded query pairs.</param>
    /// <param name="msHeaders">The <c>x-ms-*</c> headers (lower-case name).</param>
    public static string Authorization(string account, string accountKeyBase64, string method, string path, IEnumerable<KeyValuePair<string, string>> query, IReadOnlyDictionary<string, string> msHeaders, long contentLength, string contentType)
    {
        var headers = string.Concat(msHeaders.OrderBy(h => h.Key, StringComparer.Ordinal).Select(h => h.Key + ":" + h.Value.Trim() + "\n"));
        var resource = "/" + account + path;
        foreach (var group in query.GroupBy(q => q.Key.ToLowerInvariant(), StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
            resource += "\n" + group.Key + ":" + string.Join(',', group.Select(q => q.Value).OrderBy(v => v, StringComparer.Ordinal));
        // verb, then the standard headers in their fixed order (all empty but the length and the type), then the x-ms headers and the resource
        var toSign = string.Join('\n', method, "", "", contentLength == 0 ? "" : contentLength.ToString(CultureInfo.InvariantCulture), "", contentType, "", "", "", "", "", "") + "\n" + headers + resource;
        var signature = Convert.ToBase64String(HMACSHA256.HashData(Convert.FromBase64String(accountKeyBase64), Encoding.UTF8.GetBytes(toSign)));
        return $"SharedKey {account}:{signature}";
    }
}

/// <summary>What the two object stores share: a path of the connection is a key under <c>object_store.prefix</c>; a call is one HTTP request signed as the connection says, with the connection's timeouts and TLS rules; a failed answer is the shared <see cref="ConnectionFailure"/>.</summary>
internal abstract class ObjectStoreBase : IFileStore
{
    protected readonly ConnectionDefinition Definition;
    protected readonly ISecretResolver Secrets;
    private readonly HttpMessageInvoker _invoker;
    protected readonly string Prefix;

    protected ObjectStoreBase(ConnectionDefinition definition, ISecretResolver secrets, HttpMessageHandler? handler)
    {
        (Definition, Secrets) = (definition, secrets);
        _invoker = handler is null ? HttpConnections.TokenInvoker(definition) : new HttpMessageInvoker(handler, disposeHandler: false);
        var prefix = definition.ObjectStore!.Prefix.Trim('/');
        Prefix = prefix.Length == 0 ? "" : prefix + "/";
    }

    /// <summary>The key of a path of the connection (no leading slash), under the prefix.</summary>
    protected string KeyOf(string path) => Prefix + FileStorePaths.Resolve("/", path)[1..];

    /// <summary>The path of the connection of a key (with a leading slash), or null for a key outside the prefix.</summary>
    protected string? PathOf(string key) => key.StartsWith(Prefix, StringComparison.Ordinal) ? "/" + key[Prefix.Length..] : null;

    protected async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Definition.Timeouts.CommandS));
        try
        {
            var response = await _invoker.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return response;
            using (response) throw new ConnectionFailureException(await ClassifyAsync(response, ct).ConfigureAwait(false));
        }
        catch (ConnectionFailureException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            // our own timeout (the connection's command timeout) is a timeout; anything else is told by its kind, never by its message (it holds the address)
            throw new ConnectionFailureException(e is OperationCanceledException ? new ConnectionFailure(ConnectionFailureKind.Timeout, "timeout", FileFailures.Engine) : HttpFailures.Classify(e, ct));
        }
    }

    /// <summary>The failure of an answer that is not a success: the status, and for a 403 the service's own code, which tells a wrong key from a missing permission.</summary>
    protected virtual async Task<ConnectionFailure> ClassifyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var failure = HttpFailures.Classify(response.StatusCode);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            var code = CodeOf(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) ?? (response.Headers.TryGetValues("x-ms-error-code", out var header) && header.FirstOrDefault() is { Length: < 64 } named && named.All(char.IsAsciiLetterOrDigit) ? named : null);
            if (code is "InvalidAccessKeyId" or "SignatureDoesNotMatch" or "AuthenticationFailed" or "InvalidAuthenticationInfo" or "ExpiredToken" or "InvalidToken" or "AccountIsDisabled" or "KeyVaultEncryptionKeyNotFound")
                return new ConnectionFailure(ConnectionFailureKind.Login, "http-403-" + code, HttpFailures.Engine);
            return new ConnectionFailure(ConnectionFailureKind.Permission, code is null ? failure.Code : "http-403-" + code, HttpFailures.Engine);
        }

        return failure;
    }

    /// <summary>The <c>Code</c> element of an error document, only when it is a plain name.</summary>
    protected static string? CodeOf(string body)
    {
        try { return XDocument.Parse(body).Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Code")?.Value is { Length: > 0 and < 64 } code && code.All(char.IsAsciiLetterOrDigit) ? code : null; }
        catch (System.Xml.XmlException) { return null; }
    }

    protected static async Task<MemoryStream> BufferAsync(Stream content, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct).ConfigureAwait(false);
        buffer.Position = 0;
        return buffer;
    }

    /// <summary>A stream of an answer's body that lets the answer go when it is disposed.</summary>
    protected sealed class BodyStream(HttpResponseMessage response, Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) { inner.Dispose(); response.Dispose(); }
            base.Dispose(disposing);
        }
    }

    public abstract Task<IReadOnlyList<FileEntry>> ListAsync(string folder = "/", bool includeTemporary = false, CancellationToken ct = default);
    public abstract Task<Stream> OpenReadAsync(string path, CancellationToken ct = default);
    public abstract Task WriteAsync(string path, Stream content, CancellationToken ct = default);
    public abstract Task DeleteAsync(string path, CancellationToken ct = default);

    public ValueTask DisposeAsync()
    {
        _invoker.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Object storage over the S3 API (plan 27a IX06, decision SD13): Amazon S3 and any server that speaks it (MinIO, Ceph, ...), signed with Signature Version 4 (<see cref="AwsSigV4"/>) and no SDK. A folder is a key prefix and a listing uses the delimiter; a missing object is the permanent failure
/// <see cref="ConnectionFailureKind.NotFound"/>. An object is put whole in one request, so <b>it exists under its name only when it is complete</b> (no <c>.part</c> is needed); that request is buffered in memory, so a single object is limited to what memory and S3's 5 GB single-put allow (multipart upload is not built).
/// A delete of an object that is not there succeeds, as S3's does.
/// </summary>
public sealed class S3FileStore : IFileStore
{
    private readonly Impl _impl;

    private S3FileStore(Impl impl) => _impl = impl;

    public static async Task<S3FileStore> OpenAsync(ConnectionDefinition definition, ISecretResolver secrets, CancellationToken ct = default) => await OpenAsync(definition, secrets, null, ct).ConfigureAwait(false);

    internal static async Task<S3FileStore> OpenAsync(ConnectionDefinition definition, ISecretResolver secrets, HttpMessageHandler? handler, CancellationToken ct)
    {
        var impl = new Impl(definition, secrets, handler);
        // the secrets are read now, so an unset one is told when the connection is opened
        await impl.PrepareAsync(ct).ConfigureAwait(false);
        return new S3FileStore(impl);
    }

    public Task<IReadOnlyList<FileEntry>> ListAsync(string folder = "/", bool includeTemporary = false, CancellationToken ct = default) => _impl.ListAsync(folder, includeTemporary, ct);
    public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default) => _impl.OpenReadAsync(path, ct);
    public Task WriteAsync(string path, Stream content, CancellationToken ct = default) => _impl.WriteAsync(path, content, ct);
    public Task DeleteAsync(string path, CancellationToken ct = default) => _impl.DeleteAsync(path, ct);
    public ValueTask DisposeAsync() => _impl.DisposeAsync();

    private sealed class Impl(ConnectionDefinition definition, ISecretResolver secrets, HttpMessageHandler? handler) : ObjectStoreBase(definition, secrets, handler)
    {
        private string? _accessKey, _secretKey;
        private readonly Uri _endpoint = new(definition.Endpoint!.TrimEnd('/'));
        private readonly TimeProvider _time = TimeProvider.System;

        public async Task PrepareAsync(CancellationToken ct)
        {
            var signIn = Definition.SignIn ?? new SignInSpec();
            if (signIn.Method != "access_key") return;
            _accessKey = signIn.User;
            _secretKey = await SecretReader.ReadAsync(Secrets, signIn.PasswordSecret!, ct).ConfigureAwait(false);
        }

        /// <summary>The request for a bucket or an object: the address in the style the connection says, signed (or not, for a public bucket).</summary>
        private HttpRequestMessage Request(HttpMethod method, string key, IReadOnlyList<KeyValuePair<string, string>> query, byte[]? body)
        {
            var o = Definition.ObjectStore!;
            var basePath = _endpoint.AbsolutePath.TrimEnd('/');
            string host = _endpoint.Authority, path;
            if (o.PathStyle) path = basePath + "/" + o.Bucket + (key.Length > 0 ? "/" + key : "");
            else { host = o.Bucket + "." + _endpoint.Authority; path = basePath + "/" + key; }
            var queryText = string.Join('&', query.Select(p => AwsSigV4.Encode(p.Key, false) + "=" + AwsSigV4.Encode(p.Value, false)));
            var uri = new Uri($"{_endpoint.Scheme}://{host}{AwsSigV4.Encode(path, true)}{(queryText.Length > 0 ? "?" + queryText : "")}");
            var request = new HttpRequestMessage(method, uri);
            if (body is not null) request.Content = new ByteArrayContent(body);
            if (_secretKey is null) return request;
            var now = _time.GetUtcNow();
            var payload = body is null ? AwsSigV4.EmptyPayload : AwsSigV4.Hex(SHA256.HashData(body));
            var stamp = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            var signed = new Dictionary<string, string>(StringComparer.Ordinal) { ["host"] = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}", ["x-amz-content-sha256"] = payload, ["x-amz-date"] = stamp };
            var (authorization, _) = AwsSigV4.Authorization(method.Method, path, query, signed, payload, _accessKey!, _secretKey, o.Region, "s3", now);
            request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payload);
            request.Headers.TryAddWithoutValidation("x-amz-date", stamp);
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
            return request;
        }

        public override async Task<IReadOnlyList<FileEntry>> ListAsync(string folder = "/", bool includeTemporary = false, CancellationToken ct = default)
        {
            var resolved = FileStorePaths.Resolve("/", folder);
            var prefix = Prefix + (resolved == "/" ? "" : resolved[1..] + "/");
            var entries = new List<FileEntry>();
            string? token = null;
            do
            {
                var query = new List<KeyValuePair<string, string>> { new("delimiter", "/"), new("list-type", "2"), new("prefix", prefix) };
                if (token is not null) query.Add(new("continuation-token", token));
                using var response = await SendAsync(Request(HttpMethod.Get, "", query, null), ct).ConfigureAwait(false);
                var root = XDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Root!;
                foreach (var item in root.Elements().Where(e => e.Name.LocalName == "Contents"))
                {
                    var key = item.Elements().First(e => e.Name.LocalName == "Key").Value;
                    var name = key[prefix.Length..];
                    if (name.Length == 0 || (!includeTemporary && FileNames.IsTemporary(name))) continue;   // the "folder" object itself, or a temporary name
                    var size = long.Parse(item.Elements().First(e => e.Name.LocalName == "Size").Value, CultureInfo.InvariantCulture);
                    var modified = DateTimeOffset.Parse(item.Elements().First(e => e.Name.LocalName == "LastModified").Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
                    entries.Add(new FileEntry(PathOf(key)!, name, size, modified, false));
                }

                foreach (var folderPrefix in root.Elements().Where(e => e.Name.LocalName == "CommonPrefixes").Select(e => e.Elements().First(x => x.Name.LocalName == "Prefix").Value))
                    entries.Add(new FileEntry(PathOf(folderPrefix.TrimEnd('/'))!, folderPrefix[prefix.Length..].TrimEnd('/'), 0, default, true));
                token = root.Elements().FirstOrDefault(e => e.Name.LocalName == "IsTruncated")?.Value == "true" ? root.Elements().FirstOrDefault(e => e.Name.LocalName == "NextContinuationToken")?.Value : null;
            }
            while (token is not null);
            return [.. entries.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.Ordinal)];
        }

        public override async Task<Stream> OpenReadAsync(string path, CancellationToken ct = default)
        {
            var response = await SendAsync(Request(HttpMethod.Get, KeyOf(path), [], null), ct).ConfigureAwait(false);
            return new BodyStream(response, await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
        }

        public override async Task WriteAsync(string path, Stream content, CancellationToken ct = default)
        {
            var body = (await BufferAsync(content, ct).ConfigureAwait(false)).ToArray();
            using var response = await SendAsync(Request(HttpMethod.Put, KeyOf(path), [], body), ct).ConfigureAwait(false);
        }

        public override async Task DeleteAsync(string path, CancellationToken ct = default)
        {
            using var response = await SendAsync(Request(HttpMethod.Delete, KeyOf(path), [], null), ct).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Object storage over Azure Blob Storage (plan 27a IX06, decision SD13), signed with the account's Shared Key (<see cref="AzureSharedKey"/>) or a SAS token, and no SDK. The container is the connection's <c>bucket</c>; a folder is a blob-name prefix and a listing uses the delimiter. A missing blob is the permanent failure
/// <see cref="ConnectionFailureKind.NotFound"/>. A blob is put whole in one request, so <b>it exists under its name only when it is complete</b>; that request is buffered in memory (one block blob put: up to 5000 MiB). A delete of a blob that is not there is a not-found failure, as Azure's is.
/// </summary>
public sealed class AzureBlobFileStore : IFileStore
{
    private readonly Impl _impl;

    private AzureBlobFileStore(Impl impl) => _impl = impl;

    public static async Task<AzureBlobFileStore> OpenAsync(ConnectionDefinition definition, ISecretResolver secrets, CancellationToken ct = default) => await OpenAsync(definition, secrets, null, ct).ConfigureAwait(false);

    internal static async Task<AzureBlobFileStore> OpenAsync(ConnectionDefinition definition, ISecretResolver secrets, HttpMessageHandler? handler, CancellationToken ct)
    {
        var impl = new Impl(definition, secrets, handler);
        await impl.PrepareAsync(ct).ConfigureAwait(false);
        return new AzureBlobFileStore(impl);
    }

    /// <summary>Makes the container when it is not there (what a test, or a first run, needs); true when it was made.</summary>
    internal Task<bool> EnsureContainerAsync(CancellationToken ct = default) => _impl.EnsureContainerAsync(ct);

    public Task<IReadOnlyList<FileEntry>> ListAsync(string folder = "/", bool includeTemporary = false, CancellationToken ct = default) => _impl.ListAsync(folder, includeTemporary, ct);
    public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default) => _impl.OpenReadAsync(path, ct);
    public Task WriteAsync(string path, Stream content, CancellationToken ct = default) => _impl.WriteAsync(path, content, ct);
    public Task DeleteAsync(string path, CancellationToken ct = default) => _impl.DeleteAsync(path, ct);
    public ValueTask DisposeAsync() => _impl.DisposeAsync();

    private sealed class Impl(ConnectionDefinition definition, ISecretResolver secrets, HttpMessageHandler? handler) : ObjectStoreBase(definition, secrets, handler)
    {
        private string? _account, _key, _sas;
        private readonly Uri _endpoint = new(definition.Endpoint!.TrimEnd('/'));

        public async Task PrepareAsync(CancellationToken ct)
        {
            var signIn = Definition.SignIn ?? new SignInSpec();
            if (signIn.Method == "shared_key") { _account = signIn.User; _key = await SecretReader.ReadAsync(Secrets, signIn.KeySecret!, ct).ConfigureAwait(false); }
            else if (signIn.Method == "sas_token") _sas = (await SecretReader.ReadAsync(Secrets, signIn.KeySecret!, ct).ConfigureAwait(false)).TrimStart('?');
        }

        private HttpRequestMessage Request(HttpMethod method, string blob, IReadOnlyList<KeyValuePair<string, string>> query, byte[]? body, bool createBlob = false)
        {
            var path = _endpoint.AbsolutePath.TrimEnd('/') + "/" + Definition.ObjectStore!.Bucket + (blob.Length > 0 ? "/" + blob : "");
            var queryText = string.Join('&', query.Select(p => AwsSigV4.Encode(p.Key, false) + "=" + AwsSigV4.Encode(p.Value, false)));
            if (_sas is not null) queryText = queryText.Length == 0 ? _sas : queryText + "&" + _sas;
            var request = new HttpRequestMessage(method, new Uri($"{_endpoint.Scheme}://{_endpoint.Authority}{AwsSigV4.Encode(path, true)}{(queryText.Length > 0 ? "?" + queryText : "")}"));
            var type = body is null ? "" : "application/octet-stream";
            if (body is not null) { request.Content = new ByteArrayContent(body); request.Content.Headers.TryAddWithoutValidation("Content-Type", type); }
            var ms = new Dictionary<string, string>(StringComparer.Ordinal) { ["x-ms-date"] = DateTime.UtcNow.ToString("R", CultureInfo.InvariantCulture), ["x-ms-version"] = AzureSharedKey.Version };
            if (createBlob) ms["x-ms-blob-type"] = "BlockBlob";
            foreach (var (name, value) in ms) request.Headers.TryAddWithoutValidation(name, value);
            if (_key is not null)
                request.Headers.TryAddWithoutValidation("Authorization", AzureSharedKey.Authorization(_account!, _key, method.Method, AwsSigV4.Encode(path, true), query, ms, body?.Length ?? 0, type));
            return request;
        }

        public async Task<bool> EnsureContainerAsync(CancellationToken ct)
        {
            using var request = Request(HttpMethod.Put, "", [new("restype", "container")], null);
            try { using var response = await SendAsync(request, ct).ConfigureAwait(false); return true; }
            catch (ConnectionFailureException e) when (e.Failure.Code == "http-409") { return false; }   // already there
        }

        public override async Task<IReadOnlyList<FileEntry>> ListAsync(string folder = "/", bool includeTemporary = false, CancellationToken ct = default)
        {
            var resolved = FileStorePaths.Resolve("/", folder);
            var prefix = Prefix + (resolved == "/" ? "" : resolved[1..] + "/");
            var entries = new List<FileEntry>();
            string? marker = null;
            do
            {
                var query = new List<KeyValuePair<string, string>> { new("comp", "list"), new("delimiter", "/"), new("prefix", prefix), new("restype", "container") };
                if (marker is not null) query.Add(new("marker", marker));
                using var response = await SendAsync(Request(HttpMethod.Get, "", query, null), ct).ConfigureAwait(false);
                var root = XDocument.Parse((await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).TrimStart('﻿')).Root!;
                var blobs = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Blobs");
                foreach (var blob in blobs?.Elements().Where(e => e.Name.LocalName == "Blob") ?? [])
                {
                    var key = blob.Elements().First(e => e.Name.LocalName == "Name").Value;
                    var name = key[prefix.Length..];
                    if (name.Length == 0 || (!includeTemporary && FileNames.IsTemporary(name))) continue;
                    var properties = blob.Elements().First(e => e.Name.LocalName == "Properties");
                    var size = long.Parse(properties.Elements().First(e => e.Name.LocalName == "Content-Length").Value, CultureInfo.InvariantCulture);
                    var modified = DateTimeOffset.Parse(properties.Elements().First(e => e.Name.LocalName == "Last-Modified").Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
                    entries.Add(new FileEntry(PathOf(key)!, name, size, modified, false));
                }

                foreach (var folderPrefix in blobs?.Elements().Where(e => e.Name.LocalName == "BlobPrefix").Select(e => e.Elements().First(x => x.Name.LocalName == "Name").Value) ?? [])
                    entries.Add(new FileEntry(PathOf(folderPrefix.TrimEnd('/'))!, folderPrefix[prefix.Length..].TrimEnd('/'), 0, default, true));
                marker = root.Elements().FirstOrDefault(e => e.Name.LocalName == "NextMarker")?.Value is { Length: > 0 } next ? next : null;
            }
            while (marker is not null);
            return [.. entries.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.Ordinal)];
        }

        public override async Task<Stream> OpenReadAsync(string path, CancellationToken ct = default)
        {
            var response = await SendAsync(Request(HttpMethod.Get, KeyOf(path), [], null), ct).ConfigureAwait(false);
            return new BodyStream(response, await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
        }

        public override async Task WriteAsync(string path, Stream content, CancellationToken ct = default)
        {
            var body = (await BufferAsync(content, ct).ConfigureAwait(false)).ToArray();
            using var response = await SendAsync(Request(HttpMethod.Put, KeyOf(path), [], body, createBlob: true), ct).ConfigureAwait(false);
        }

        public override async Task DeleteAsync(string path, CancellationToken ct = default)
        {
            using var response = await SendAsync(Request(HttpMethod.Delete, KeyOf(path), [], null), ct).ConfigureAwait(false);
        }
    }
}
