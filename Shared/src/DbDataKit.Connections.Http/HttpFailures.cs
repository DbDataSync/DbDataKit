using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using DbDataKit.Dialect;

namespace DbDataKit.Connections.Http;

/// <summary>
/// How a failure of an HTTP call is classified (plan 27a IX01 and IX02): the same <see cref="ConnectionFailure"/> a database gives, so a retry reads <see cref="ConnectionFailure.IsTemporary"/> and a screen shows a fixed sentence. A rejected sign-in (401), a missing permission
/// (403), a missing address or object (404, 410) and a request the system will not accept (the other 4xx) are permanent; a busy system (429), a system that fails or is down (5xx) and a network failure or timeout are temporary. The code is <c>http-&lt;status&gt;</c>:
/// nothing of the response body, a header or the request ever reaches the message.
/// </summary>
public static class HttpFailures
{
    public static readonly EngineId Engine = new("http");

    /// <summary>The classification of a response status that is not a success.</summary>
    public static ConnectionFailure Classify(HttpStatusCode status)
    {
        var code = "http-" + (int)status;
        var kind = (int)status switch
        {
            401 => ConnectionFailureKind.Login,
            403 => ConnectionFailureKind.Permission,
            404 or 410 => ConnectionFailureKind.NotFound,
            408 => ConnectionFailureKind.Timeout,
            429 => ConnectionFailureKind.RateLimited,
            >= 500 => ConnectionFailureKind.Unavailable,
            400 or 405 or 406 or 409 or 411 or 413 or 414 or 415 or 422 => ConnectionFailureKind.Syntax,
            _ => ConnectionFailureKind.Other,
        };
        return new ConnectionFailure(kind, code, Engine);
    }

    /// <summary>The classification of a failure of the token address of OAuth 2: a refusal of the client (400, 401, 403: a wrong id or secret) is a rejected login, whatever else is as for any call.</summary>
    public static ConnectionFailure ClassifyToken(HttpStatusCode status) =>
        (int)status is 400 or 401 or 403 ? new ConnectionFailure(ConnectionFailureKind.Login, "oauth-" + (int)status, Engine) : Classify(status);

    /// <summary>The classification of an exception of a call: a socket or an I/O failure is the network, a refused or untrusted certificate is TLS, a timeout of the client is a timeout; a cancellation by the caller is neither.</summary>
    public static ConnectionFailure Classify(Exception exception, CancellationToken caller = default)
    {
        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            switch (ex)
            {
                case ConnectionFailureException known: return known.Failure;
                case SecretNotSetException unset: return unset.Failure;
                case OperationCanceledException when caller.IsCancellationRequested: return new(ConnectionFailureKind.Other, "canceled", Engine);
                case TimeoutException: return new(ConnectionFailureKind.Timeout, nameof(TimeoutException), Engine);
                case AuthenticationException: return new(ConnectionFailureKind.Tls, ex.GetType().Name, Engine);
                case SocketException se: return new(ConnectionFailureKind.Network, "socket-" + (int)se.SocketErrorCode, Engine);
                case HttpRequestException { StatusCode: { } status }: return Classify(status);
            }
        }
        // HttpClient.Timeout is a TaskCanceledException with no TimeoutException inside on older paths
        if (exception is TaskCanceledException && !caller.IsCancellationRequested) return new(ConnectionFailureKind.Timeout, "timeout", Engine);
        if (exception is HttpRequestException) return new(ConnectionFailureKind.Network, "http-request", Engine);
        return new(ConnectionFailureKind.Other, exception.GetType().Name, Engine);
    }

    /// <summary>Throws <see cref="ConnectionFailureException"/> for a response that is not a success; the response is left to the caller.</summary>
    public static void ThrowIfFailed(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode) throw new ConnectionFailureException(Classify(response.StatusCode));
    }
}
