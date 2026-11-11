using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DbDataKit.Identity.Tests;

/// <summary>
/// The home's public endpoints (discovery, keys, token) answered by a provider in this process, for a relying party that is given an <see cref="HttpClient"/> (<see cref="OpenIdClient"/>): the same provider code the host serves, with
/// the form parsed as <see cref="OpenIdEndpoints"/> does and an <see cref="OAuthException"/> answered as the endpoint answers it. <see cref="Unreachable"/> makes every call fail as a home that is down does.
/// </summary>
internal sealed class HomeHandler(OpenIdProvider provider, SigningKeys keys) : HttpMessageHandler
{
    public bool Unreachable { get; set; }

    public List<string> Grants { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Unreachable) throw new HttpRequestException("The home is down.");
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/.well-known/openid-configuration") return Json(provider.Discovery());
        if (path == "/identity/jwks") return Json(keys.Jwks());
        if (path != "/identity/token") return new HttpResponseMessage(HttpStatusCode.NotFound);
        var form = (await request.Content!.ReadAsStringAsync(cancellationToken)).Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p.Length > 1 ? p[1].Replace('+', ' ') : ""));
        string? Of(string key) => form.GetValueOrDefault(key);
        Grants.Add(Of("grant_type") ?? "");
        try
        {
            var t = provider.Exchange(new TokenRequest(Of("grant_type"), Of("code"), Of("redirect_uri"), Of("client_id"), Of("client_secret"), Of("code_verifier"), Of("refresh_token"), Of("subject_token"), Of("audience"), Of("subject_token_type")));
            return Of("grant_type") == OpenIdProvider.TokenExchangeGrant
                ? Json(new { access_token = t.AccessToken, issued_token_type = OpenIdProvider.AccessTokenType, token_type = "Bearer", expires_in = t.ExpiresIn, scope = t.Scope })
                : Json(new { access_token = t.AccessToken, token_type = "Bearer", expires_in = t.ExpiresIn, id_token = t.IdToken, scope = t.Scope, refresh_token = t.RefreshToken });
        }
        catch (OAuthException e)
        {
            var answer = Json(new { error = e.Error, error_description = e.Message });
            answer.StatusCode = (HttpStatusCode)e.Status;
            return answer;
        }
    }

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body, options: new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNamingPolicy = null }) };
}
