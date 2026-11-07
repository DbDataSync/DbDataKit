using Grpc.Core;
using Grpc.Core.Interceptors;

namespace DbDataKit.Runner.Protocol.Server;

/// <summary>
/// Binds every call to a run (plan 28a DR3): the token in the metadata is looked up before the handler runs, for every method of every kind, so no call reaches a store without naming a live run. A call with no token, an
/// unknown one, one that expired and one whose run ended is refused with <c>Unauthenticated</c>; the handler finds its run in <see cref="SessionOf"/>.
/// </summary>
internal sealed class RunTokenInterceptor(RunSessions sessions) : Interceptor
{
    private const string UserStateKey = "dbdatakit.run-session";

    public static RunSession SessionOf(ServerCallContext context) =>
        context.UserState.TryGetValue(UserStateKey, out var session) ? (RunSession)session : throw new RpcException(new Status(StatusCode.Unauthenticated, "The call is not bound to a run."));

    private RunSession Bind(ServerCallContext context)
    {
        var token = context.RequestHeaders.GetValue(RunnerProtocolNames.TokenHeader);
        if (!sessions.TryGet(token, out var session, out var reason)) throw new RpcException(new Status(StatusCode.Unauthenticated, reason));
        context.UserState[UserStateKey] = session;
        return session;
    }

    public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        Bind(context);
        return continuation(request, context);
    }

    public override Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(IAsyncStreamReader<TRequest> requestStream, ServerCallContext context, ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Bind(context);
        return continuation(requestStream, context);
    }

    public override Task ServerStreamingServerHandler<TRequest, TResponse>(TRequest request, IServerStreamWriter<TResponse> responseStream, ServerCallContext context, ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Bind(context);
        return continuation(request, responseStream, context);
    }

    public override Task DuplexStreamingServerHandler<TRequest, TResponse>(IAsyncStreamReader<TRequest> requestStream, IServerStreamWriter<TResponse> responseStream, ServerCallContext context, DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Bind(context);
        return continuation(requestStream, responseStream, context);
    }
}
