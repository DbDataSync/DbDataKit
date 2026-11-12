using System.Diagnostics;
using System.Net;
using DbDataKit.Secrets;

namespace DbDataKit.Connections.Http;

/// <summary>
/// What <b>Test</b> of a connection of kind <c>http</c> found (plan 27a IX02): whether the system was reached, whether it accepted the sign-in, and a sentence in words that says what to do. <see cref="Reached"/> is null when the call was never made (a secret is not set);
/// <see cref="SignIn"/> is <c>none</c> (the connection signs in with nothing), <c>accepted</c>, <c>rejected</c> or <c>not checked</c>. <see cref="Kind"/> and <see cref="Temporary"/> are the classification a retry reads. Nothing in it is a secret, a header or a response body.
/// </summary>
public sealed record HttpTestResult(bool Ok, bool? Reached, string SignIn, string Message, ConnectionFailureKind? Kind, bool Temporary, int? Status, long Milliseconds);

/// <summary>Tests a connection of kind <c>http</c>: signs in as the connection says (for OAuth 2 that is getting a token) and calls the endpoint (or <c>test_path</c> beneath it) once.</summary>
public static class HttpConnectionTester
{
    public static async Task<HttpTestResult> TestAsync(ConnectionDefinition definition, ISecretResolver secrets, HttpConnectionOptions? options = null, CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew();
        HttpTestResult Done(bool ok, bool? reached, string signIn, string message, ConnectionFailureKind? kind = null, int? status = null) =>
            new(ok, reached, signIn, message, kind, kind is { } k && ConnectionFailure.IsTemporaryKind(k), status, clock.ElapsedMilliseconds);

        var method = definition.SignIn?.Method ?? "none";
        var shown = EndpointTemplate.Display(definition.Endpoint);   // the template (or "address hidden"), never the composed address
        var signIn = method == "none" ? "none" : "not checked";
        HttpConnectionClient connection;
        try { connection = HttpConnections.Create(definition, secrets, options); }
        catch (SecretNotSetException e) { return Done(false, null, signIn, Unset(definition, e)); }
        catch (ConnectionFailureException e) when (e.Failure.Code == "client-certificate") { return Done(false, null, signIn, "The client certificate could not be loaded: check the file or the secret that holds it, and its password.", ConnectionFailureKind.Tls); }

        using (connection)
        {
            try
            {
                var path = (definition.TestPath ?? "").TrimStart('/');
                using var request = new HttpRequestMessage(HttpMethod.Get, path);
                using var response = await connection.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                return Judge(definition, shown, response.StatusCode, method, Done);
            }
            catch (SecretNotSetException e)
            {
                return Done(false, null, signIn, Unset(definition, e));
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                var failure = HttpFailures.Classify(e, ct);
                // a refused client of OAuth 2 is the sign-in being refused; the endpoint was not tried
                if (failure.Code == TokenEndpoint.GrantRevoked) return Done(false, null, "rejected", TokenEndpoint.GrantRevokedText, failure.Kind);
                if (failure.Kind == ConnectionFailureKind.Login) return Done(false, failure.Code?.StartsWith("oauth-", StringComparison.Ordinal) == true ? null : true, "rejected", $"The system did not accept the sign-in ({failure.Code}). Check the client id and its secret, and that this client may ask for a token.", failure.Kind);
                var words = failure.Kind switch
                {
                    ConnectionFailureKind.Network => $"{shown} could not be reached: nothing answered there. Check the address and that the system is up.",
                    ConnectionFailureKind.Timeout => $"{shown} did not answer in time.",
                    ConnectionFailureKind.Tls => "A secure connection could not be established: the system's certificate is not trusted (or the client certificate is not accepted). Give the authority in `tls.ca_file`, or turn verification off for this connection if you must.",
                    _ => ConnectionFailure.TextFor(failure.Kind),
                };
                return Done(false, false, signIn, words, failure.Kind);
            }
        }
    }

    /// <summary>What a secret that is not set means: for the refresh token of an authorization code connection nobody has given consent yet, for any other it is typed on the screen.</summary>
    private static string Unset(ConnectionDefinition d, SecretNotSetException e) =>
        d.SignIn is { Method: "oauth2_authorization_code" } s && s.RefreshTokenSecret == e.Reference
            ? "Consent has not been given yet: press Authorize on the Connections screen and sign in at the provider (the refresh token it gives is kept in the console's secret store)."
            : $"{e.Message} Type it on this screen (it is kept in the console's secret store), or set the secret or variable that names.";

    private static HttpTestResult Judge(ConnectionDefinition d, string shown, HttpStatusCode status, string method, Func<bool, bool?, string, string, ConnectionFailureKind?, int?, HttpTestResult> done)
    {
        var code = (int)status;
        var at = d.TestPath is { Length: > 0 } p ? $"{shown}/{p.TrimStart('/')}" : shown;
        var accepted = method == "none" ? "none" : "accepted";
        if (code is >= 200 and < 400) return done(true, true, accepted, $"{at} answered {code}" + (method == "none" ? "." : " and accepted the sign-in."), null, code);
        if (code == 401) return done(false, true, method == "none" ? "none" : "rejected", method == "none" ? $"{at} answered 401: it wants a sign-in, and this connection has none." : $"{at} answered, but did not accept the sign-in (401). Check the secret, and that it is the one for this system.", ConnectionFailureKind.Login, code);
        if (code == 403) return done(false, true, accepted, $"{at} answered 403: the sign-in was accepted, but it is not allowed to read this address.", ConnectionFailureKind.Permission, code);
        if (code is 404 or 405 or 410)
            return done(true, true, accepted, $"{at} answered {code}: the system is there and did not refuse the sign-in. Set `test_path` to an address that exists to check the sign-in itself.", null, code);
        var failure = HttpFailures.Classify(status);
        return done(false, true, accepted, $"{at} answered {code}. {ConnectionFailure.TextFor(failure.Kind)}", failure.Kind, code);
    }
}
