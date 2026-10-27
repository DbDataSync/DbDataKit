using Grpc.Core;
using Grpc.Core.Interceptors;

namespace DbDataKit.Serving.FlightSql;

/// <summary>
/// Denies every Flight call that does not carry credentials the core's identity service accepts: a bearer token (a core session's, or one from an identity provider the core trusts) or basic credentials, which pass-through sources need on every call.
/// The handshake is exempt because it is where credentials are exchanged for a token (the handler asks identity itself). The core decides what a failed or repeated attempt costs (a throttle keyed on user and address); this only carries the address.
/// The caller is kept in the call's user state for the handler and for any interceptor of the host that runs after this one.
/// </summary>
public sealed class FlightAuthInterceptor(FlightSqlModule module) : Interceptor
{
    private const string HandshakeMethod = "/arrow.flight.protocol.FlightService/Handshake";

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        await AuthorizeAsync(context);
        return await continuation(request, context);
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(TRequest request, IServerStreamWriter<TResponse> responseStream, ServerCallContext context, ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        await AuthorizeAsync(context);
        await continuation(request, responseStream, context);
    }

    public override async Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(IAsyncStreamReader<TRequest> requestStream, ServerCallContext context, ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        await AuthorizeAsync(context);
        return await continuation(requestStream, context);
    }

    public override async Task DuplexStreamingServerHandler<TRequest, TResponse>(IAsyncStreamReader<TRequest> requestStream, IServerStreamWriter<TResponse> responseStream, ServerCallContext context, DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        await AuthorizeAsync(context);
        await continuation(requestStream, responseStream, context);
    }

    private async Task AuthorizeAsync(ServerCallContext context)
    {
        if (context.Method == HandshakeMethod) return;
        module.EnsureServing();

        var caller = await AuthenticateAsync(module.Services.Identity, context.RequestHeaders.GetValue("authorization"), context.ClientAddress(), context.CancellationToken)
            ?? throw new RpcException(new Status(StatusCode.Unauthenticated, "Invalid or missing credentials."));
        context.UserState[FlightCallContext.ContextKey] = new ServingContext(caller, ClientAddress: context.ClientAddress());
    }

    /// <summary>Resolves an <c>authorization</c> header value (Bearer token or Basic credentials) to a caller; a refusal by the core (a lockout, say) reaches the client as the core worded it.</summary>
    public static async ValueTask<ServingCaller?> AuthenticateAsync(IServingIdentity identity, string? authorization, string clientAddress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(authorization)) return null;
        try
        {
            if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return await identity.AuthenticateAsync(new ServingCredentials.Bearer(authorization["Bearer ".Length..].Trim()), clientAddress, cancellationToken).ConfigureAwait(false);
            if (BasicCredentials.TryParseBasic(authorization, out var user, out var password))
                return await identity.AuthenticateAsync(new ServingCredentials.Password(user, password), clientAddress, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (ServingException ex)
        {
            throw FlightFailures.ToRpcException(ex);
        }
    }
}
