using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DbDataKit.Runner.Kit.Testing;

/// <summary>A recorded answer: status, headers and body of the response, and what the request must look like to get it.</summary>
/// <param name="Method">GET, POST, ...</param>
/// <param name="Url">The absolute address, query included. The order of the query's parameters does not matter.</param>
/// <param name="RequestBody">When set, the request's body must be this text (or, for <see cref="RequestJson"/>, this JSON structurally).</param>
/// <param name="RequestJson">When set, the request's body must be this JSON (compared as JSON, not as text).</param>
/// <param name="Status">The status code of the answer.</param>
/// <param name="Headers">Response headers (never a cookie or a credential: the loader refuses them).</param>
/// <param name="Body">The answer's body as text, or null.</param>
/// <param name="BodyBase64">The answer's body as bytes, for a body that is not text.</param>
/// <param name="Repeat">The entry answers every request that matches it, not only the first; entries without it are used once, in file order.</param>
public sealed record RecordedExchange(string Method, string Url, string? RequestBody, JsonNode? RequestJson, int Status, IReadOnlyDictionary<string, string> Headers, string? Body, string? BodyBase64, bool Repeat);

/// <summary>A recording could not be read, or a recording was not honoured (a request without an answer, an answer nobody asked for). The message names the file, the entry or the request; it never holds a body or a header value.</summary>
public sealed class RecordingException(string message) : Exception(message);

/// <summary>
/// A recording of an outside API (format <c>http-recording/1</c>, plan 27a IX15): the exchanges a job's test replays so it runs with no network, as JSON with the entries in the order they are asked:
/// <code>
/// { "schema": "http-recording/1",
///   "entries": [
///     { "request": { "method": "GET", "url": "https://api.example.test/v1/orders?cursor=a" },
///       "response": { "status": 200, "headers": { "Content-Type": "application/json" }, "json": { "items": [1, 2], "next": "b" } } } ] }
/// </code>
/// A response body is <c>body</c> (text), <c>json</c> (a value written inline, so a recording reads as the API did) or <c>body_base64</c>. A request may say <c>body</c> or <c>json</c> to be matched on its body too. An entry with <c>"repeat": true</c> answers every matching request.
/// A recording holds no secret: a response header that is a credential or a cookie, a user name in an address and a query parameter that names a credential make the file unreadable, naming the entry. Capturing a recording from a live API is not offered; write the file from the API's
/// documentation or from an answer you have read and cleaned.
/// </summary>
public sealed class HttpRecording
{
    /// <summary>The format's version, registered in <c>docs/formats.json</c>.</summary>
    public const string Schema = "http-recording/1";

    private static readonly string[] SecretHeaders = ["authorization", "proxy-authorization", "cookie", "set-cookie", "x-api-key", "x-auth-token", "x-csrf-token"];
    private static readonly string[] SecretQuery = ["api_key", "apikey", "access_token", "token", "secret", "password", "client_secret", "signature", "sig", "key"];

    private readonly List<RecordedExchange> _entries = [];

    public IReadOnlyList<RecordedExchange> Entries => _entries;

    /// <summary>Adds an answer by hand: <paramref name="json"/> is the body as JSON text.</summary>
    public HttpRecording Add(string method, string url, int status = 200, string? json = null, IReadOnlyDictionary<string, string>? headers = null, string? requestBody = null, bool repeat = false)
    {
        var entry = new RecordedExchange(method.ToUpperInvariant(), url, requestBody, null, status, headers ?? new Dictionary<string, string>(), json, null, repeat);
        Check(entry, _entries.Count + 1, "the recording");
        if (json is not null && headers?.Keys.Any(k => k.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) != true)
            entry = entry with { Headers = new Dictionary<string, string>(entry.Headers) { ["Content-Type"] = "application/json" } };
        _entries.Add(entry);
        return this;
    }

    public static HttpRecording Load(string path) => Parse(File.ReadAllText(path), path);

    /// <summary>Reads the JSON text of a recording; <paramref name="file"/> is only the name used in messages.</summary>
    public static HttpRecording Parse(string json, string file = "recording.json")
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException ex) { throw new RecordingException($"The recording '{file}' is not JSON ({ex.Message.Split('.')[0]})."); }
        if (root is not JsonObject top || top["schema"]?.GetValue<string>() is not { } schema)
            throw new RecordingException($"The recording '{file}' has no 'schema': {Schema} is the one this version reads.");
        if (schema != Schema) throw new RecordingException($"The recording '{file}' is '{schema}'; this version reads '{Schema}'.");
        if (top["entries"] is not JsonArray entries) throw new RecordingException($"The recording '{file}' has no 'entries' list.");

        var recording = new HttpRecording();
        var number = 0;
        foreach (var node in entries)
        {
            number++;
            if (node is not JsonObject entry || entry["request"] is not JsonObject request || entry["response"] is not JsonObject response)
                throw new RecordingException($"Entry {number} of the recording '{file}' needs a 'request' and a 'response'.");
            var method = request["method"]?.GetValue<string>() ?? "GET";
            var url = request["url"]?.GetValue<string>() ?? throw new RecordingException($"Entry {number} of the recording '{file}' has no request 'url'.");
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (response["headers"] is JsonObject map)
                foreach (var (name, value) in map) headers[name] = value?.ToString() ?? "";
            string? body = response["body"]?.GetValue<string>();
            if (response["json"] is { } inline)
            {
                body = inline.ToJsonString();
                headers.TryAdd("Content-Type", "application/json");
            }

            var exchange = new RecordedExchange(method.ToUpperInvariant(), url, request["body"]?.GetValue<string>(), request["json"], response["status"]?.GetValue<int>() ?? 200, headers, body, response["body_base64"]?.GetValue<string>(), entry["repeat"]?.GetValue<bool>() ?? false);
            Check(exchange, number, $"the recording '{file}'");
            recording._entries.Add(exchange);
        }

        return recording;
    }

    /// <summary>The recording as the JSON text of the file.</summary>
    public string ToJson()
    {
        var entries = new JsonArray();
        foreach (var e in _entries)
        {
            var request = new JsonObject { ["method"] = e.Method, ["url"] = e.Url };
            if (e.RequestBody is not null) request["body"] = e.RequestBody;
            if (e.RequestJson is not null) request["json"] = e.RequestJson.DeepClone();
            var response = new JsonObject { ["status"] = e.Status };
            if (e.Headers.Count > 0) response["headers"] = new JsonObject(e.Headers.Select(h => KeyValuePair.Create(h.Key, (JsonNode?)h.Value)));
            if (e.BodyBase64 is not null) response["body_base64"] = e.BodyBase64;
            else if (e.Body is not null) response["body"] = e.Body;
            var entry = new JsonObject { ["request"] = request, ["response"] = response };
            if (e.Repeat) entry["repeat"] = true;
            entries.Add(entry);
        }

        return new JsonObject { ["schema"] = Schema, ["entries"] = entries }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static void Check(RecordedExchange e, int number, string where)
    {
        if (!Uri.TryCreate(e.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new RecordingException($"Entry {number} of {where} needs an absolute http or https address.");
        if (uri.UserInfo.Length > 0) throw new RecordingException($"Entry {number} of {where} has a user name in its address; a recording holds no credential.");
        foreach (var (name, _) in Query(uri))
            if (IsSecretQuery(name)) throw new RecordingException($"Entry {number} of {where} has the query parameter '{name}', which names a credential; a recording holds none (leave it out: requests are matched without it only if the job does not send it).");
        foreach (var name in e.Headers.Keys)
            if (SecretHeaders.Contains(name.ToLowerInvariant())) throw new RecordingException($"Entry {number} of {where} has the response header '{name}', which is a credential or a cookie; a recording holds none.");
        if (e.Status is < 100 or > 599) throw new RecordingException($"Entry {number} of {where} has the status {e.Status}.");
    }

    internal static bool IsSecretQuery(string name) => SecretQuery.Contains(name.ToLowerInvariant());

    internal static IEnumerable<(string Name, string Value)> Query(Uri uri) =>
        uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2)).Select(p => (Uri.UnescapeDataString(p[0].Replace('+', ' ')), p.Length > 1 ? Uri.UnescapeDataString(p[1].Replace('+', ' ')) : ""));
}

/// <summary>
/// Replays an <see cref="HttpRecording"/> as an <see cref="HttpMessageHandler"/>, so a job's HTTP client (<c>new OutsideHttp(new HttpClient(recorded) { BaseAddress = ... })</c>, or the handler of <c>HttpConnectionOptions</c>) talks to the recording and never the network (plan 27a IX15).
/// A request is matched on method, address (scheme, host, path and the query as a set) and, if the entry says so, body; each entry answers once, in file order, unless it is <c>repeat</c>. **A request with no answer is not sent anywhere:** the handler throws
/// <see cref="RecordingException"/> naming the request and remembers it, and <see cref="Verify"/> (call it at the end of the test, or <c>using</c> the handler) fails the test with every such request and every entry nobody asked for, so a job that swallows the failure still fails the test.
/// </summary>
public sealed class RecordedHttp : HttpMessageHandler
{
    private readonly List<(RecordedExchange Entry, int Used)> _entries;
    private readonly List<string> _misses = [];
    private readonly List<string> _served = [];
    private readonly object _gate = new();

    public RecordedHttp(HttpRecording recording)
    {
        ArgumentNullException.ThrowIfNull(recording);
        _entries = [.. recording.Entries.Select(e => (e, 0))];
    }

    public static RecordedHttp FromFile(string path) => new(HttpRecording.Load(path));

    public static RecordedHttp FromJson(string json) => new(HttpRecording.Parse(json));

    /// <summary>The requests that were answered, as <c>GET https://host/path?query</c>, in order.</summary>
    public IReadOnlyList<string> Served
    {
        get { lock (_gate) return [.. _served]; }
    }

    /// <summary>The requests that had no answer.</summary>
    public IReadOnlyList<string> Misses
    {
        get { lock (_gate) return [.. _misses]; }
    }

    /// <summary>The entries (1 is the first) nobody asked for.</summary>
    public IReadOnlyList<int> Unused
    {
        get { lock (_gate) return [.. _entries.Select((e, i) => (e, i)).Where(x => x.e.Used == 0).Select(x => x.i + 1)]; }
    }

    /// <summary>A client on this handler.</summary>
    public HttpClient Client(string? baseAddress = null) => new(this, disposeHandler: false) { BaseAddress = baseAddress is null ? null : new Uri(baseAddress) };

    /// <summary>Fails with every request that had no answer (and, when <paramref name="allUsed"/>, every entry nobody asked for).</summary>
    public void Verify(bool allUsed = true)
    {
        var misses = Misses;
        if (misses.Count > 0) throw new RecordingException("No recording for " + string.Join("; ", misses.Select(m => m)) + ".");
        if (allUsed && Unused.Count > 0) throw new RecordingException($"The recording has entries nobody asked for: {string.Join(", ", Unused)} (the job did not make those requests).");
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri ?? throw new RecordingException("A request has no address.");
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var name = $"{request.Method.Method} {Describe(uri)}";
        RecordedExchange? found = null;
        lock (_gate)
        {
            for (var i = 0; i < _entries.Count && found is null; i++)
            {
                var (entry, used) = _entries[i];
                if ((used > 0 && !entry.Repeat) || !Matches(entry, request.Method.Method, uri, body)) continue;
                _entries[i] = (entry, used + 1);
                found = entry;
            }

            if (found is null) _misses.Add(name);
            else _served.Add(name);
        }

        if (found is null) throw new RecordingException($"No recording for {name}" + (body is null ? "." : " with that body."));
        var response = new HttpResponseMessage((HttpStatusCode)found.Status) { RequestMessage = request };
        if (found.BodyBase64 is not null) response.Content = new ByteArrayContent(Convert.FromBase64String(found.BodyBase64));
        else if (found.Body is not null) response.Content = new StringContent(found.Body, Encoding.UTF8);
        else response.Content = new ByteArrayContent([]);
        foreach (var (header, value) in found.Headers)
        {
            if (header.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) response.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(value);
            else if (!response.Headers.TryAddWithoutValidation(header, value)) response.Content.Headers.TryAddWithoutValidation(header, value);
        }

        return response;
    }

    private static bool Matches(RecordedExchange entry, string method, Uri uri, string? body)
    {
        if (!entry.Method.Equals(method, StringComparison.OrdinalIgnoreCase)) return false;
        var wanted = new Uri(entry.Url);
        if (!wanted.Scheme.Equals(uri.Scheme, StringComparison.OrdinalIgnoreCase) || !wanted.Authority.Equals(uri.Authority, StringComparison.OrdinalIgnoreCase) || wanted.AbsolutePath != uri.AbsolutePath) return false;
        if (!HttpRecording.Query(wanted).Order().SequenceEqual(HttpRecording.Query(uri).Order())) return false;
        if (entry.RequestBody is not null && entry.RequestBody != body) return false;
        if (entry.RequestJson is not null)
        {
            try { if (!JsonNode.DeepEquals(entry.RequestJson, JsonNode.Parse(body ?? "null"))) return false; }
            catch (JsonException) { return false; }
        }

        return true;
    }

    /// <summary>The address as a message names it: path and query, except that the value of a parameter that names a credential is hidden (a job that sends its key in the query must not print it in a failing test).</summary>
    private static string Describe(Uri uri) => uri.GetLeftPart(UriPartial.Path) + (uri.Query.Length > 1 ? "?" + string.Join("&", HttpRecording.Query(uri).Select(q => q.Name + "=" + (HttpRecording.IsSecretQuery(q.Name) ? "***" : q.Value))) : "");
}
