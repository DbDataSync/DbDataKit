using DbDataKit.Serving;
using DbDataKit.Secrets;
using DbDataKit.Serving.FlightSql;
using DbDataProxy.Server.Serving;
using Grpc.Core;
using Grpc.Core.Interceptors;

namespace DbDataProxy.Server.Secrets;

/// <summary>Wraps every logger so message text and exception text pass through the shared redactor.</summary>
internal sealed class RedactingLoggerFactory(ILoggerFactory inner, IRedactor redactor) : ILoggerFactory
{
    public ILogger CreateLogger(string categoryName) => new RedactingLogger(inner.CreateLogger(categoryName), redactor);
    public void AddProvider(ILoggerProvider provider) => inner.AddProvider(provider);
    public void Dispose() => inner.Dispose();
}

internal sealed class RedactingLogger(ILogger inner, IRedactor redactor) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!inner.IsEnabled(logLevel)) return;

        string message = formatter(state, exception);
        string redacted = redactor.Redact(message);

        Exception? safeException = exception;
        if (exception is not null)
        {
            string text = exception.ToString();
            string redactedText = redactor.Redact(text);
            if (!string.Equals(text, redactedText, StringComparison.Ordinal))
                safeException = new RedactedException(redactor.Redact(exception.Message), redactedText);
        }

        if (string.Equals(message, redacted, StringComparison.Ordinal))
            inner.Log(logLevel, eventId, state, safeException, formatter); // unchanged: keep the structured state
        else
            inner.Log(logLevel, eventId, redacted, safeException, static (text, _) => text);
    }
}

/// <summary>Stands in for an exception whose text carried a secret; keeps the redacted text and stack trace.</summary>
internal sealed class RedactedException(string message, string fullText) : Exception(message)
{
    public override string ToString() => fullText;
}

/// <summary>Redacts the detail text of every <see cref="RpcException"/> before it reaches the client.</summary>
public sealed class RedactingInterceptor(SecretsRuntime secrets) : Interceptor
{
    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        try { return await continuation(request, context); }
        catch (RpcException ex) { throw Scrub(ex, context); }
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request, IServerStreamWriter<TResponse> responseStream, ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        try { await continuation(request, responseStream, context); }
        catch (RpcException ex) { throw Scrub(ex, context); }
    }

    public override async Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream, ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        try { return await continuation(requestStream, context); }
        catch (RpcException ex) { throw Scrub(ex, context); }
    }

    public override async Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream, IServerStreamWriter<TResponse> responseStream, ServerCallContext context,
        DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        try { await continuation(requestStream, responseStream, context); }
        catch (RpcException ex) { throw Scrub(ex, context); }
    }

    private RpcException Scrub(RpcException ex, ServerCallContext context)
    {
        string? password = context.UserState.TryGetValue(FlightCallContext.ContextKey, out var c) && c is ServingContext { Caller: ProxyCaller caller }
            ? caller.Principal.Credentials?.Password
            : null;
        string redacted = secrets.Redact(ex.Status.Detail, password);
        return string.Equals(redacted, ex.Status.Detail, StringComparison.Ordinal)
            ? ex
            : new RpcException(new Status(ex.Status.StatusCode, redacted), ex.Trailers);
    }
}
