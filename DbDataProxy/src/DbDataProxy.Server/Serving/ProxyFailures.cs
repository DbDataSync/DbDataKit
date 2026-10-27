using DbDataKit.Serving;
using DbDataKit.Odbc.Metadata;
using DbDataProxy.Data;
using Grpc.Core;

namespace DbDataProxy.Server.Serving;

/// <summary>
/// What Proxy's data layer throws, said as the serving layer's exceptions, so a protocol turns each into its own words: the gRPC statuses the catalog of data sources raises, a message Proxy wrote for the client, an ODBC policy violation
/// and a catalog larger than the configured limit. A database failure (<c>ConnectionFailureException</c>) and anything unrecognised pass through unchanged for the protocol to classify.
/// </summary>
public static class ProxyFailures
{
    public static Exception ToServing(Exception ex) => ex switch
    {
        ServingException => ex,
        IClientVisible or OdbcPolicyViolationException => new ServingInvalidException(ex.Message),
        OdbcResultTruncatedException => new ServingLimitException("The data source's catalog is larger than the configured limit.", "catalog"),
        RpcException { StatusCode: StatusCode.NotFound } rpc => new ServingNotFoundException(rpc.Status.Detail),
        RpcException { StatusCode: StatusCode.PermissionDenied } rpc => new ServingDeniedException(rpc.Status.Detail),
        RpcException { StatusCode: StatusCode.InvalidArgument } rpc => new ServingInvalidException(rpc.Status.Detail),
        RpcException { StatusCode: StatusCode.Unauthenticated } rpc => new ServingUnauthenticatedException(rpc.Status.Detail),
        _ => ex,
    };

    /// <summary>Runs <paramref name="work"/>, saying a failure as <see cref="ToServing"/> does.</summary>
    public static async Task<T> TranslateAsync<T>(Func<Task<T>> work)
    {
        try { return await work().ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException && ToServing(ex) is var serving && !ReferenceEquals(serving, ex)) { throw serving; }
    }
}
