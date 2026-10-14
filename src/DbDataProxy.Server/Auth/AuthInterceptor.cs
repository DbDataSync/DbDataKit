using System.Text;
using Grpc.Core;
using Grpc.Core.Interceptors;

namespace DbDataProxy.Server.Auth;

/// <summary>Resolves an <c>authorization</c> header value (Bearer token or Basic credentials) to a principal.</summary>
public sealed class RequestAuthenticator(SessionStore sessions, ChainedCredentialValidator validators)
{
    public async ValueTask<ProxyPrincipal?> AuthenticateAsync(string? authorization, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(authorization)) return null;

        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return sessions.TryGet(authorization["Bearer ".Length..].Trim());

        if (TryParseBasic(authorization, out string user, out string password))
            return await validators.ValidateAsync(user, password, cancellationToken).ConfigureAwait(false);

        return null;
    }

    public static bool TryParseBasic(string? authorization, out string user, out string password)
    {
        user = password = "";
        if (authorization is null || !authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authorization["Basic ".Length..].Trim()));
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
/// </summary>
public sealed class AuthInterceptor(RequestAuthenticator authenticator) : Interceptor
{
    public const string PrincipalKey = "principal";
    private const string HandshakeMethod = "/arrow.flight.protocol.FlightService/Handshake";

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        await AuthorizeAsync(context);
        return await continuation(request, context);
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request, IServerStreamWriter<TResponse> responseStream, ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        await AuthorizeAsync(context);
        await continuation(request, responseStream, context);
    }

    public override async Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream, ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        await AuthorizeAsync(context);
        return await continuation(requestStream, context);
    }

    public override async Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream, IServerStreamWriter<TResponse> responseStream, ServerCallContext context,
        DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        await AuthorizeAsync(context);
        await continuation(requestStream, responseStream, context);
    }

    private async Task AuthorizeAsync(ServerCallContext context)
    {
        if (context.Method == HandshakeMethod) return;

        string? header = context.RequestHeaders.GetValue("authorization");
        var principal = await authenticator.AuthenticateAsync(header, context.CancellationToken);
        if (principal is null)
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Invalid or missing credentials."));
        context.UserState[PrincipalKey] = principal;
    }
}

public static class ServerCallContextExtensions
{
    public static ProxyPrincipal GetPrincipal(this ServerCallContext context) =>
        context.UserState.TryGetValue(AuthInterceptor.PrincipalKey, out var p) && p is ProxyPrincipal principal
            ? principal
            : throw new RpcException(new Status(StatusCode.Unauthenticated, "Not authenticated."));
}
