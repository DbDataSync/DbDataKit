using System.Text;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.AspNetCore.Http;

namespace DbDataProxy.Server.Auth;

/// <summary>Resolves an <c>authorization</c> header value (Bearer token or Basic credentials) to a principal.</summary>
public sealed class RequestAuthenticator(SessionStore sessions, ChainedCredentialValidator validators, IJwtValidator? jwt = null)
{
    public async ValueTask<ProxyPrincipal?> AuthenticateAsync(string? authorization, CancellationToken cancellationToken, string client = "")
    {
        if (string.IsNullOrWhiteSpace(authorization)) return null;

        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            string token = authorization["Bearer ".Length..].Trim();
            if (sessions.TryGet(token) is { } session) return session;

            // Not one of our session tokens: a JWT from an external identity provider, if that is enabled.
            return jwt is { Enabled: true } && token.Count(c => c == '.') == 2
                ? await jwt.ValidateAsync(token, cancellationToken).ConfigureAwait(false)
                : null;
        }

        if (TryParseBasic(authorization, out string user, out string password))
            return await validators.ValidateAsync(user, password, cancellationToken, client).ConfigureAwait(false);

        return null;
    }

    public static bool TryParseBasic(string? authorization, out string user, out string password)
    {
        user = password = "";
        if (authorization is null || !authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            string encoded = authorization["Basic ".Length..].Trim();
            // Some clients (the Go-based ADBC driver) send base64 without the trailing padding.
            encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
            string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            int colon = decoded.IndexOf(':');
            if (colon <= 0) return false;
            user = decoded[..colon];
            password = decoded[(colon + 1)..];
            return true;
        }
        catch (FormatException) { return false; }
    }
}

/// <summary>
/// Denies every Flight call that does not carry valid credentials. The handshake call is exempt because it is
/// where credentials are exchanged for a bearer token (the handler validates them itself).
/// Pass-through credentials are only checked by the database, at query time, so this is also where a rejected database login
/// feeds the login throttle (a classified Login failure from the handler counts as one failed attempt for that user and client) and
/// where a locked-out user is refused before any connection is made (S-2).
/// </summary>
public sealed class AuthInterceptor(RequestAuthenticator authenticator, LoginThrottle? throttle = null) : Interceptor
{
    public const string PrincipalKey = "principal";
    private const string HandshakeMethod = "/arrow.flight.protocol.FlightService/Handshake";

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        await AuthorizeAsync(context);
        try { return await continuation(request, context); }
        catch (RpcException ex) { NoteFailure(ex, context); throw; }
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request, IServerStreamWriter<TResponse> responseStream, ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        await AuthorizeAsync(context);
        try { await continuation(request, responseStream, context); }
        catch (RpcException ex) { NoteFailure(ex, context); throw; }
    }

    public override async Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream, ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        await AuthorizeAsync(context);
        try { return await continuation(requestStream, context); }
        catch (RpcException ex) { NoteFailure(ex, context); throw; }
    }

    public override async Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream, IServerStreamWriter<TResponse> responseStream, ServerCallContext context,
        DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        await AuthorizeAsync(context);
        try { await continuation(requestStream, responseStream, context); }
        catch (RpcException ex) { NoteFailure(ex, context); throw; }
    }

    private async Task AuthorizeAsync(ServerCallContext context)
    {
        if (context.Method == HandshakeMethod) return;

        string? header = context.RequestHeaders.GetValue("authorization");
        var principal = await authenticator.AuthenticateAsync(header, context.CancellationToken, context.ClientAddress());
        if (principal is null)
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Invalid or missing credentials."));
        // A pass-through principal carries the caller's own database credentials: after too many rejected logins refuse it here, without
        // touching the database, exactly like the handshake does for passwords it can check itself.
        if (principal.Credentials is not null && throttle?.IsLockedOut(context.ClientAddress(), principal.Name) == true)
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Too many failed logins; try again later."));
        context.UserState[PrincipalKey] = principal;
    }

    private void NoteFailure(RpcException ex, ServerCallContext context)
    {
        if (throttle is null || ex is not DbDataKit.Serving.FlightSql.FailureRpcException { Failure.Kind: DbDataKit.Connections.ConnectionFailureKind.Login }) return;
        if (context.UserState.TryGetValue(PrincipalKey, out var p) && p is ProxyPrincipal { Credentials: not null } principal)
            throttle.RecordFailure(context.ClientAddress(), principal.Name);
    }
}

public static class ServerCallContextExtensions
{
    /// <summary>The caller's network address without the port, used to scope brute-force throttling.</summary>
    public static string ClientAddress(this ServerCallContext context) =>
        context.GetHttpContext().Connection.RemoteIpAddress?.ToString() ?? "";

    public static ProxyPrincipal GetPrincipal(this ServerCallContext context) =>
        context.UserState.TryGetValue(AuthInterceptor.PrincipalKey, out var p) && p is ProxyPrincipal principal
            ? principal
            : throw new RpcException(new Status(StatusCode.Unauthenticated, "Not authenticated."));
}
