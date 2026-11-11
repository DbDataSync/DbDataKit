using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using DbDataKit.Connections;
using DbDataKit.Connections.Http;
using DbDataKit.Dialect;
using DbDataKit.Secrets;

namespace DbDataKit.Runner.Kit.Http;

/// <summary>How <see cref="OutsideHttp"/> behaves. The defaults are the careful ones.</summary>
public sealed record OutsideHttpOptions
{
    /// <summary>The connection's name, for the rate limit shared by every task of the process that uses it.</summary>
    public string Connection { get; init; } = "http";

    /// <summary>How many times a request is sent in all when a failure is temporary: 4 is the first try and three more.</summary>
    public int MaxAttempts { get; init; } = 4;

    /// <summary>The wait before try <c>n + 1</c> (n from 1) when the system did not say how long: 0.5 s, 1 s, 2 s.</summary>
    public Func<int, TimeSpan> Backoff { get; init; } = n => TimeSpan.FromMilliseconds(500 * Math.Pow(2, n - 1));

    /// <summary>The longest wait inside one call, for a back-off or for what Retry-After says. A system that asks for more than this is not waited for: the call fails as temporary and the runner's retry of the task (with its own schedule) comes back later.</summary>
    public TimeSpan MaxWait { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>At most this many requests in this period, shared by the process (<see cref="RateLimits"/>); null for none.</summary>
    public (int Permits, TimeSpan Period)? Limit { get; init; }

    /// <summary>A POST or PATCH is sent again after a temporary failure only when it carries an idempotency key (the header <see cref="IdempotencyHeader"/>) or this is true: a repeated create is a duplicate.</summary>
    public bool RetryUnsafeMethods { get; init; }

    public string IdempotencyHeader { get; init; } = "Idempotency-Key";

    /// <summary>A response held in memory (<see cref="ApiResponse"/>) is at most this large; a download is a stream (<see cref="OutsideHttp.OpenReadAsync"/>).</summary>
    public long MaxBufferedBytes { get; init; } = 64L << 20;

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>How a wait is made; a test records the waits instead of sleeping.</summary>
    public Func<TimeSpan, CancellationToken, Task>? Delay { get; init; }
}

/// <summary>A response read to its end: the status, the headers, the body as bytes. No secret is in it and none of it is put in a failure message.</summary>
public sealed class ApiResponse(HttpStatusCode status, IReadOnlyDictionary<string, string[]> headers, byte[] body)
{
    public HttpStatusCode Status { get; } = status;

    public IReadOnlyDictionary<string, string[]> Headers { get; } = headers;

    public byte[] Body { get; } = body;

    public string Text => Encoding.UTF8.GetString(Body);

    public string? Header(string name) => Headers.TryGetValue(name, out var values) ? string.Join(", ", values) : null;

    /// <summary>The body as JSON; the element stays valid after the response is dropped.</summary>
    public JsonElement Json()
    {
        try { using var document = JsonDocument.Parse(Body); return document.RootElement.Clone(); }
        catch (JsonException ex) { throw new ApiShapeException("The response is not JSON.", ex); }
    }

    public T? Json<T>(JsonSerializerOptions? options = null)
    {
        try { return JsonSerializer.Deserialize<T>(Body, options ?? JsonSerializerOptions.Web); }
        catch (JsonException ex) { throw new ApiShapeException($"The response is not the JSON of a {typeof(T).Name}.", ex); }
    }
}

/// <summary>A response that is not what the call expected (not JSON, not an array where a page of items was expected). Permanent: asking again gets the same answer. The message holds no part of the body.</summary>
public sealed class ApiShapeException(string message, Exception? inner = null) : Exception(message, inner)
{
    public bool IsTemporary => false;
}

/// <summary>
/// The HTTP client of an integration (plan 27a IX10) over a connection's signed-in <see cref="HttpClient"/>: it waits for a rate limit, sends, and **classifies** what goes wrong with the connection's own classification (<see cref="HttpFailures"/>), so the runner's retry reads it:
/// a temporary failure (429, 5xx, 408, a network failure or timeout) is first retried inside the call with a back-off, and a 429 waits as Retry-After says (the whole connection waits: <see cref="RateLimit.Hold"/>); what is still failing after that, or a wait longer than
/// <see cref="OutsideHttpOptions.MaxWait"/>, is thrown as a <see cref="ConnectionFailureException"/> with <c>IsTemporary</c> true, which the runner retries as a task. 401, 403, 404 and the other 4xx are thrown at once and are permanent. No response body, header or address is put in a failure.
/// </summary>
public sealed class OutsideHttp : IDisposable
{
    private readonly HttpClient _client;
    private readonly HttpConnectionClient? _owned;
    private readonly OutsideHttpOptions _o;
    private readonly RateLimit? _limit;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public OutsideHttp(HttpClient client, OutsideHttpOptions? options = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _o = options ?? new OutsideHttpOptions();
        _delay = _o.Delay ?? ((d, ct) => Task.Delay(d, _o.Time, ct));
        _limit = _o.Limit is { } l ? RateLimits.For(_o.Connection, l.Permits, l.Period) : null;
    }

    private OutsideHttp(HttpConnectionClient owned, OutsideHttpOptions options) : this(owned.Client, options) => _owned = owned;

    /// <summary>
    /// The one place the kit makes an HTTP connection's client (<see cref="HttpConnections.Create"/>): the connection's sign-in, timeouts and TLS, with its name as the key of the rate limit. A job that gets the client from elsewhere (a test's fake handler) uses the constructor.
    /// </summary>
    public static OutsideHttp Open(ConnectionDefinition definition, ISecretResolver secrets, OutsideHttpOptions? options = null, HttpConnectionOptions? connection = null) =>
        new(HttpConnections.Create(definition, secrets, connection), (options ?? new OutsideHttpOptions()) with { Connection = definition.Name });

    public Uri? BaseAddress => _client.BaseAddress;

    /// <summary>GET a path (relative to the connection's address), with a query.</summary>
    public Task<ApiResponse> GetAsync(string path, IEnumerable<KeyValuePair<string, string?>>? query = null, CancellationToken ct = default) =>
        SendAsync(() => new HttpRequestMessage(HttpMethod.Get, Url(path, query)), ct);

    /// <summary>Send a JSON body. <paramref name="idempotencyKey"/> makes the request safe to repeat: it is sent in the idempotency header and the request is then retried after a temporary failure even when it is a POST.</summary>
    public Task<ApiResponse> SendJsonAsync(HttpMethod method, string path, object? body, string? idempotencyKey = null, CancellationToken ct = default) =>
        SendAsync(() =>
        {
            var request = new HttpRequestMessage(method, Url(path, null)) { Content = body is null ? null : new StringContent(JsonSerializer.Serialize(body, JsonSerializerOptions.Web), Encoding.UTF8, "application/json") };
            if (idempotencyKey is not null) request.Headers.TryAddWithoutValidation(_o.IdempotencyHeader, idempotencyKey);
            return request;
        }, ct);

    /// <summary>Sends a request made by <paramref name="make"/> (called once per try, so the body is new each time) and reads the whole response (up to <see cref="OutsideHttpOptions.MaxBufferedBytes"/>).</summary>
    public async Task<ApiResponse> SendAsync(Func<HttpRequestMessage> make, CancellationToken ct = default)
    {
        using var response = await SendCoreAsync(make, ct).ConfigureAwait(false);
        var length = response.Content.Headers.ContentLength;
        if (length > _o.MaxBufferedBytes) throw new ApiShapeException($"The response is {length} bytes; a response held in memory is at most {_o.MaxBufferedBytes}. Read it as a stream (OpenReadAsync).");
        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int n;
        while ((n = await body.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, n);
            if (buffer.Length > _o.MaxBufferedBytes) throw new ApiShapeException($"The response is larger than {_o.MaxBufferedBytes} bytes; a response held in memory is at most that. Read it as a stream (OpenReadAsync).");
        }

        return new ApiResponse(response.StatusCode, Headers(response), buffer.ToArray());
    }

    /// <summary>A download as a stream: the request is sent, classified and retried as <see cref="SendAsync"/> until the headers are in; the body is then read as the caller reads it (a failure in the middle of a body is the caller's, and the step that holds it recovers).</summary>
    public async Task<Stream> OpenReadAsync(string path, IEnumerable<KeyValuePair<string, string?>>? query = null, CancellationToken ct = default)
    {
        var response = await SendCoreAsync(() => new HttpRequestMessage(HttpMethod.Get, Url(path, query)), ct).ConfigureAwait(false);
        return new ResponseStream(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), response);
    }

    internal async Task<HttpResponseMessage> SendCoreAsync(Func<HttpRequestMessage> make, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            if (_limit is not null) await _limit.WaitAsync(_o.Time, _delay, ct).ConfigureAwait(false);
            ConnectionFailure failure;
            TimeSpan? retryAfter = null;
            var repeatable = true;
            try
            {
                using var request = make();
                repeatable = IsRepeatable(request);
                var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return response;
                failure = HttpFailures.Classify(response.StatusCode);
                retryAfter = RetryAfter(response);
                response.Dispose();
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                if (ex is ConnectionFailureException known) throw;
                failure = HttpFailures.Classify(ex, ct);
            }

            if (!failure.IsTemporary || !repeatable) throw new ConnectionFailureException(failure);
            var wait = failure.Kind == ConnectionFailureKind.RateLimited && retryAfter is { } told ? told : _o.Backoff(attempt);
            if (failure.Kind == ConnectionFailureKind.RateLimited && _limit is not null) _limit.Hold(_o.Time, wait);
            if (attempt >= _o.MaxAttempts || wait > _o.MaxWait) throw new ConnectionFailureException(failure);
            await _delay(wait, ct).ConfigureAwait(false);
        }
    }

    private bool IsRepeatable(HttpRequestMessage request) =>
        request.Method == HttpMethod.Get || request.Method == HttpMethod.Head || request.Method == HttpMethod.Options || request.Method == HttpMethod.Put || request.Method == HttpMethod.Delete
        || _o.RetryUnsafeMethods || request.Headers.Contains(_o.IdempotencyHeader);

    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header is null) return null;
        if (header.Delta is { } delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (header.Date is { } date) { var wait = date - _o.Time.GetUtcNow(); return wait < TimeSpan.Zero ? TimeSpan.Zero : wait; }
        return null;
    }

    private static Dictionary<string, string[]> Headers(HttpResponseMessage response)
    {
        var all = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in response.Headers) all[h.Key] = [.. h.Value];
        foreach (var h in response.Content.Headers) all[h.Key] = [.. h.Value];
        return all;
    }

    private static string Url(string path, IEnumerable<KeyValuePair<string, string?>>? query)
    {
        var text = path.TrimStart('/');
        if (query is null) return text;
        var sb = new StringBuilder(text);
        var first = !text.Contains('?', StringComparison.Ordinal);
        foreach (var (key, value) in query)
        {
            if (value is null) continue;
            sb.Append(first ? '?' : '&').Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
            first = false;
        }

        return sb.ToString();
    }

    public void Dispose()
    {
        if (_owned is not null) _owned.Dispose();
        else _client.Dispose();
    }

    private sealed class ResponseStream(Stream inner, HttpResponseMessage response) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
