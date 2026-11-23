using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.State;

namespace DbDataKit.Deploy.Tests;

/// <summary>
/// A link that answers in process, as a Gateway or a Runner would, from <see cref="Answer"/>: each call is recorded (method, path relative to <c>/api/v1/</c>, body) and answered with a status and a JSON body. A path with no answer is a 404.
/// <see cref="Unreachable"/> makes every call fail the way a server that cannot be reached does.
/// </summary>
internal sealed class FakeLink(string name = "https://target:8443") : IDefinitionsLink, IDisposable
{
    public string Name => name;

    public List<(string Method, string Path, string? Body)> Calls { get; } = [];

    /// <summary>The answer to a call, by method and path; null for a 404 with no body.</summary>
    public Func<string, string, string?, (int Status, JsonNode? Body)?> Answer { get; set; } = (_, _, _) => null;

    public bool Unreachable { get; set; }

    public bool Disposed { get; private set; }

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        var body = content is null ? null : await content.ReadAsStringAsync(ct);
        Calls.Add((method.Method, path, body));
        if (Unreachable) throw new HttpRequestException("Connection refused");
        var answer = Answer(method.Method, path, body);
        var response = new HttpResponseMessage(answer is { } a ? (HttpStatusCode)a.Status : HttpStatusCode.NotFound);
        if (answer?.Body is { } json) response.Content = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json");
        return response;
    }

    public void Dispose() => Disposed = true;

    /// <summary>The path of a call without its query.</summary>
    public static string PathOf(string path) => path.Split('?', 2)[0];
}
