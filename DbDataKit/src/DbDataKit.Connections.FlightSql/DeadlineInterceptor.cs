using Grpc.Core;
using Grpc.Core.Interceptors;

namespace DbDataKit.Connections.FlightSql;

/// <summary>
/// Puts the current time limit on every call as a gRPC <b>deadline</b> (the server gets it as <c>grpc-timeout</c> and can stop). The Flight SQL library's own <c>FlightCallOptions.Timeout</c> is not used by its client, so the limit is
/// applied here. The limit is set before each command; a call that already has a deadline keeps it.
/// </summary>
internal sealed class DeadlineInterceptor : Interceptor
{
    private long _ticks;

    public TimeSpan? Timeout { set => Interlocked.Exchange(ref _ticks, value?.Ticks ?? 0); }

    private ClientInterceptorContext<TRequest, TResponse> With<TRequest, TResponse>(ClientInterceptorContext<TRequest, TResponse> context)
        where TRequest : class where TResponse : class =>
        Interlocked.Read(ref _ticks) is var t and > 0 && context.Options.Deadline is null
            ? new(context.Method, context.Host, context.Options.WithDeadline(DateTime.UtcNow + TimeSpan.FromTicks(t))) : context;

    public override TResponse BlockingUnaryCall<TRequest, TResponse>(TRequest request, ClientInterceptorContext<TRequest, TResponse> context, BlockingUnaryCallContinuation<TRequest, TResponse> continuation) =>
        continuation(request, With(context));

    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(TRequest request, ClientInterceptorContext<TRequest, TResponse> context, AsyncUnaryCallContinuation<TRequest, TResponse> continuation) =>
        continuation(request, With(context));

    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(TRequest request, ClientInterceptorContext<TRequest, TResponse> context, AsyncServerStreamingCallContinuation<TRequest, TResponse> continuation) =>
        continuation(request, With(context));

    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(ClientInterceptorContext<TRequest, TResponse> context, AsyncClientStreamingCallContinuation<TRequest, TResponse> continuation) =>
        continuation(With(context));

    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(ClientInterceptorContext<TRequest, TResponse> context, AsyncDuplexStreamingCallContinuation<TRequest, TResponse> continuation) =>
        continuation(With(context));
}
