using System.Text.RegularExpressions;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Http;

namespace DbDataKit.Apps;

/// <summary>A replay cache that accepts every token: an app's token is a bearer token for five minutes and is used for many calls, which the one-use rule of a console's tokens would refuse.</summary>
internal sealed class ReusableTokens : IReplayCache
{
    public bool TryAdd(string consoleId, string tokenId, DateTimeOffset forgetAfter) => true;
}

/// <summary>
/// The way a sandboxed app reaches a Store (plan 26 section 3): <c>/apps/&lt;instance&gt;/_data/&lt;store&gt;/&lt;path&gt;</c> on the apps host. The app sends the token the shell brokered (audience <c>app:&lt;instance&gt;</c>); the host checks it, and
/// forwards the call to the paired Store with a token of its own for that Store, carrying the person, the app and the scope of the manifest's <c>models</c>, so the Store enforces the scope as well. The Store is never reached by the browser,
/// needs no CORS or public address, and the app never holds a token the Store accepts. Only row reads and the changes endpoint are forwarded.
/// </summary>
public sealed class AppDataGateway(AppInstanceStore instances, AppStores stores, ConsoleIdentity self, TimeProvider? clock = null)
{
    private static readonly Regex DataPath = new(@"^api/v1/models/(?<model>[^/]+)(?<instance>/instances/[^/]+)?/(entities/[^/]+/rows?|changes)$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly ConsoleTokenIssuer _issuer = self.Issuer(clock);
    private readonly IPairedClients _self = new Self(new PairedClient(self.Id, "apps host", self.Key.PublicKey, ServerRole.Viewer | ServerRole.Operator | ServerRole.Author | ServerRole.Approver | ServerRole.Admin, ClientKind.Server));

    private sealed class Self(PairedClient client) : IPairedClients
    {
        public PairedClient? Find(string clientId) => clientId == client.Id ? client : null;
    }

    /// <summary>Forwards one call and returns the Store's answer, or the host's own refusal.</summary>
    public async Task<AppResponse> ForwardAsync(string instance, string storeId, string path, string method, string query, string? authorization, string? contentType, byte[] body, CancellationToken ct)
    {
        if (instances.Resolve(instance) is not { } resolved) return Refuse(404, "No such app.");
        var validated = new ConsoleTokenValidator(AppInstanceAudience.Of(instance), _self, new ReusableTokens(), clock).Validate(authorization is { } h && h.StartsWith("Bearer ", StringComparison.Ordinal) ? h[7..] : null);
        if (validated.Token is not { } token) return Refuse(401, "A token for this app is needed.");
        if (stores.Find(storeId) is not { } store) return Refuse(404, "No such store.");

        var match = DataPath.Match(path.Trim('/'));
        if (!match.Success) return Refuse(403, "Apps reach row reads and changes only.");
        if (!(HttpMethods.IsGet(method) || HttpMethods.IsPost(method))) return Refuse(405, "GET or POST.");

        // the instance's binding says which instance of the model's data this app uses
        var forwarded = path.Trim('/');
        var model = Uri.UnescapeDataString(match.Groups["model"].Value);
        if (!match.Groups["instance"].Success && resolved.Instance.Bindings.TryGetValue(model, out var bound))
            forwarded = forwarded.Insert(match.Groups["model"].Index + match.Groups["model"].Length, "/instances/" + Uri.EscapeDataString(bound));

        var forStore = _issuer.Issue(new ConsoleTokenRequest(store.Id, token.SubjectId, token.SubjectName, token.Roles, App: instance, Scope: AppTokenBroker.ScopeOf(resolved.Manifest)));
        using var http = new HttpClient(PairingClient.Pinned(store.Fingerprint)) { Timeout = TimeSpan.FromSeconds(60) };
        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(store.Url, forwarded + query));
        request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, forStore);
        if (body.Length > 0) request.Content = new ByteArrayContent(body) { Headers = { ContentType = contentType is null ? null : System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType) } };
        try
        {
            using var response = await http.SendAsync(request, ct);
            return new AppResponse((int)response.StatusCode, response.Content.Headers.ContentType?.ToString() ?? "application/json", Cors, await response.Content.ReadAsByteArrayAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IncompatibleServerException)
        {
            return Refuse(502, "The store could not be reached.");
        }
    }

    public static readonly IReadOnlyDictionary<string, string> Cors = new Dictionary<string, string>
    {
        // the app's frame has an opaque origin ("null"); the token, not the origin, is the credential, and no cookie is involved
        ["Access-Control-Allow-Origin"] = "*", ["Access-Control-Allow-Headers"] = "Authorization, Content-Type", ["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS",
        ["Cache-Control"] = "no-store", ["X-Content-Type-Options"] = "nosniff",
    };

    private static AppResponse Refuse(int status, string message) => new(status, "text/plain; charset=utf-8", Cors, System.Text.Encoding.UTF8.GetBytes(message));
}
