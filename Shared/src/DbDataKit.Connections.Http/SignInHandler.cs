using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DbDataKit.Secrets;

namespace DbDataKit.Connections.Http;

/// <summary>Reads the value of a secret reference for a request and gives it back to be dropped. A secret that is not set is a classified failure, never an exception that holds the reference's value.</summary>
internal static class SecretText
{
    public static async Task<string> ReadAsync(ISecretResolver secrets, string reference, CancellationToken ct)
    {
        try
        {
            using var value = await secrets.ResolveAsync(SecretRef.Parse(reference), ct).ConfigureAwait(false);
            return value.RevealString();
        }
        catch (SecretNotFoundException)
        {
            // the reference is not a secret; the value never was in it: it is safe to say which one is missing
            throw new SecretNotSetException(reference);
        }
    }
}

/// <summary>A secret the connection names is not set. It is a permanent login failure: the connection cannot sign in until a person sets it. The message names the reference (which holds no value), never a value.</summary>
public sealed class SecretNotSetException(string reference) : Exception($"The secret `{reference}` is not set.")
{
    public string Reference { get; } = reference;

    public ConnectionFailure Failure { get; } = new(ConnectionFailureKind.Login, "secret-not-set", HttpFailures.Engine);
}

/// <summary>
/// Gets and keeps the access token of OAuth 2 client credentials (plan 27a IX02). The token is cached until shortly before it expires (<see cref="Skew"/>); a caller that was refused with it asks for a new one with <c>stale</c> naming the one it used, and
/// when another caller already replaced it that one is used: <b>an expired token is refreshed once</b>, however many callers meet the expiry together. A refusal of the client (a wrong id or secret) is a permanent login failure and nothing is cached.
/// </summary>
internal sealed class OAuthTokenSource(SignInSpec spec, ISecretResolver secrets, HttpMessageInvoker invoker, TimeProvider time)
{
    public static readonly TimeSpan Skew = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _expires;

    /// <summary>How many times the token address was called: what a test counts.</summary>
    public int Fetches { get; private set; }

    public async Task<(string Token, bool Fresh)> GetAsync(string? stale, CancellationToken ct)
    {
        if (_token is { } cached && cached != stale && time.GetUtcNow() < _expires - Skew) return (cached, false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // another caller may have refreshed while this one waited
            if (_token is { } current && current != stale && time.GetUtcNow() < _expires - Skew) return (current, false);
            var (token, lifetime) = await FetchAsync(ct).ConfigureAwait(false);
            (_token, _expires) = (token, time.GetUtcNow() + lifetime);
            return (token, true);
        }
        finally { _gate.Release(); }
    }

    private async Task<(string Token, TimeSpan Lifetime)> FetchAsync(CancellationToken ct)
    {
        var clientSecret = await SecretText.ReadAsync(secrets, spec.ClientSecretSecret!, ct).ConfigureAwait(false);
        var form = new List<KeyValuePair<string, string>> { new("grant_type", "client_credentials") };
        if (!string.IsNullOrEmpty(spec.Scope)) form.Add(new("scope", spec.Scope));
        using var request = new HttpRequestMessage(HttpMethod.Post, spec.TokenUrl);
        if (spec.ClientAuth == "body") { form.Add(new("client_id", spec.ClientId!)); form.Add(new("client_secret", clientSecret)); }
        else request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(spec.ClientId!)}:{Uri.EscapeDataString(clientSecret)}")));
        request.Content = new FormUrlEncodedContent(form);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        Fetches++;
        using var response = await invoker.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new ConnectionFailureException(HttpFailures.ClassifyToken(response.StatusCode));
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = document.RootElement;
            if (root.TryGetProperty("access_token", out var token) && token.ValueKind == JsonValueKind.String && token.GetString() is { Length: > 0 } text)
            {
                var seconds = root.TryGetProperty("expires_in", out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n) && n > 0 ? n : 300;
                return (text, TimeSpan.FromSeconds(seconds));
            }
        }
        catch (JsonException) { }
        // an answer that is not a token is a refusal the same way: the system does not sign this client in
        throw new ConnectionFailureException(new ConnectionFailure(ConnectionFailureKind.Login, "oauth-no-token", HttpFailures.Engine));
    }
}

/// <summary>
/// Signs each request of a connection of kind <c>http</c> in (plan 27a IX02): an API key in a header or the query, basic, or a bearer token of OAuth 2 client credentials. With OAuth 2 a request that the system refuses with 401 while it used a cached token is sent once more with
/// a new one (the request is copied before it is sent, so it can be). A client certificate is not a header: it is on the connection (<see cref="HttpConnections"/>). Secrets are read for each request and dropped; none is kept here, in a message or a log.
/// </summary>
internal sealed class SignInHandler(SignInSpec spec, ISecretResolver secrets, OAuthTokenSource? tokens, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        switch (spec.Method)
        {
            case "api_key":
                {
                    var key = await SecretText.ReadAsync(secrets, spec.KeySecret!, ct).ConfigureAwait(false);
                    if (spec.In == "query")
                    {
                        var name = spec.Name ?? "api_key";
                        var uri = request.RequestUri!;
                        var builder = new UriBuilder(uri) { Query = (uri.Query.Length > 1 ? uri.Query[1..] + "&" : "") + Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(key) };
                        request.RequestUri = builder.Uri;
                    }
                    else request.Headers.Remove(spec.Name ?? "X-Api-Key");
                    if (spec.In != "query") request.Headers.TryAddWithoutValidation(spec.Name ?? "X-Api-Key", (spec.Prefix ?? "") + key);
                    break;
                }
            case "basic":
                {
                    var password = await SecretText.ReadAsync(secrets, spec.PasswordSecret!, ct).ConfigureAwait(false);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{spec.User}:{password}")));
                    break;
                }
            case "oauth2_client_credentials":
                return await SendWithTokenAsync(request, ct).ConfigureAwait(false);
        }
        return await base.SendAsync(request, ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendWithTokenAsync(HttpRequestMessage request, CancellationToken ct)
    {
        HttpRequestMessage? retry = await CopyAsync(request, ct).ConfigureAwait(false);
        try
        {
            var (token, fresh) = await tokens!.GetAsync(null, ct).ConfigureAwait(false);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var response = await base.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Unauthorized || fresh) return response;

            // the cached token was refused (revoked, or expired earlier than it said): a new one, once
            response.Dispose();
            (token, _) = await tokens.GetAsync(token, ct).ConfigureAwait(false);
            retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var again = retry;
            retry = null;   // sent now: the handler's caller owns and disposes it
            return await base.SendAsync(again, ct).ConfigureAwait(false);
        }
        finally { retry?.Dispose(); }
    }

    /// <summary>A copy of the request that can be sent after the first one was (the content is read into memory once).</summary>
    private static async Task<HttpRequestMessage> CopyAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var copy = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version, VersionPolicy = request.VersionPolicy };
        foreach (var header in request.Headers) copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        if (request.Content is { } content)
        {
            var bytes = await content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var buffered = new ByteArrayContent(bytes);
            foreach (var header in content.Headers) buffered.Headers.TryAddWithoutValidation(header.Key, header.Value);
            copy.Content = buffered;
            request.Content = new ByteArrayContent(bytes);
            foreach (var header in content.Headers) request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        return copy;
    }
}
