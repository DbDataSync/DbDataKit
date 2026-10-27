using DbDataKit.Serving;
using DbDataKit.Serving.FlightSql;
using DbDataProxy.Server.Serving;
using Grpc.Core;
using Grpc.Core.Interceptors;

namespace DbDataProxy.Server.Auth;

/// <summary>
/// Pass-through credentials are only checked by the database, at query time, so this is where a rejected database login feeds the login throttle (a classified Login failure from the handler counts as one failed attempt for that user and client)
/// and where a locked-out user is refused before any connection is made (S-2). It runs after the Flight module's authentication, which leaves the caller in the call's state.
/// </summary>
public sealed class LoginThrottleInterceptor(LoginThrottle throttle) : Interceptor
{
    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        Check(context);
        try { return await continuation(request, context); }
        catch (RpcException ex) { NoteFailure(ex, context); throw; }
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(TRequest request, IServerStreamWriter<TResponse> responseStream, ServerCallContext context, ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Check(context);
        try { await continuation(request, responseStream, context); }
        catch (RpcException ex) { NoteFailure(ex, context); throw; }
    }

    public override async Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(IAsyncStreamReader<TRequest> requestStream, ServerCallContext context, ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Check(context);
        try { return await continuation(requestStream, context); }
        catch (RpcException ex) { NoteFailure(ex, context); throw; }
    }

    public override async Task DuplexStreamingServerHandler<TRequest, TResponse>(IAsyncStreamReader<TRequest> requestStream, IServerStreamWriter<TResponse> responseStream, ServerCallContext context, DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Check(context);
        try { await continuation(requestStream, responseStream, context); }
        catch (RpcException ex) { NoteFailure(ex, context); throw; }
    }

    private static ProxyPrincipal? PassThroughPrincipal(ServerCallContext context) =>
        context.UserState.TryGetValue(FlightCallContext.ContextKey, out var c) && c is ServingContext { Caller: ProxyCaller { Principal.Credentials: not null } caller } ? caller.Principal : null;

    // a pass-through principal carries the caller's own database credentials: after too many rejected logins refuse it here, without touching the database, exactly like the handshake does for passwords it can check itself
    private void Check(ServerCallContext context)
    {
        if (PassThroughPrincipal(context) is { } principal && throttle.IsLockedOut(context.ClientAddress(), principal.Name))
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Too many failed logins; try again later."));
    }

    private void NoteFailure(RpcException ex, ServerCallContext context)
    {
        if (ex is not FailureRpcException { Failure.Kind: DbDataKit.Connections.ConnectionFailureKind.Login }) return;
        if (PassThroughPrincipal(context) is { } principal) throttle.RecordFailure(context.ClientAddress(), principal.Name);
    }
}
