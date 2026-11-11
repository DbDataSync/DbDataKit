using DbDataKit.Connections;
using Grpc.Core;

namespace DbDataKit.Serving.FlightSql;

/// <summary>
/// The status a client sees for a database failure (kind, code and a fixed sentence, see <see cref="FlightFailures.ToRpcException"/>), carrying the classified failure so the call pipeline can react to it (a rejected pass-through login
/// feeds the login throttle) without parsing text.
/// </summary>
public sealed class FailureRpcException(ConnectionFailure failure)
    : RpcException(new Status(FlightFailures.StatusFor(failure.Kind), failure.PublicMessage))
{
    public ConnectionFailure Failure { get; } = failure;
}

/// <summary>How a failure reaches a Flight client: as a gRPC status the client can act on, never as the driver's own text, which can name tables, columns, servers and file paths (that goes to the host's log).</summary>
public static class FlightFailures
{
    /// <summary>The gRPC status for a failure kind.</summary>
    public static StatusCode StatusFor(ConnectionFailureKind kind) => kind switch
    {
        ConnectionFailureKind.Login => StatusCode.Unauthenticated,
        ConnectionFailureKind.Network => StatusCode.Unavailable,
        ConnectionFailureKind.Tls => StatusCode.Unavailable,
        ConnectionFailureKind.Timeout => StatusCode.DeadlineExceeded,
        ConnectionFailureKind.Permission => StatusCode.PermissionDenied,
        ConnectionFailureKind.Syntax => StatusCode.InvalidArgument,
        ConnectionFailureKind.Unsupported => StatusCode.InvalidArgument, // a driver or server that cannot do what was asked (a missing ODBC driver is IM002)
        _ => StatusCode.Internal,
    };

    /// <summary>
    /// A database failure arrives classified and the client gets its kind, the engine's error number or SQLSTATE and a fixed sentence (for example "The login was rejected. [sqlserver 18456]"); a refusal, a missing source, an invalid request or a limit
    /// reached becomes the status that says so, with the host's own message; anything else is an internal error naming only the type.
    /// </summary>
    public static RpcException ToRpcException(Exception ex) => ex switch
    {
        RpcException rpc => rpc,
        ConnectionFailureException failure => new FailureRpcException(failure.Failure),
        ServingBackendException backend => new FailureRpcException(backend.Failure),
        ServingUnauthenticatedException => new RpcException(new Status(StatusCode.Unauthenticated, ex.Message)),
        ServingDeniedException => new RpcException(new Status(StatusCode.PermissionDenied, ex.Message)),
        ServingNotFoundException => new RpcException(new Status(StatusCode.NotFound, ex.Message)),
        ServingInvalidException => new RpcException(new Status(StatusCode.InvalidArgument, ex.Message)),
        ServingLimitException => new RpcException(new Status(StatusCode.ResourceExhausted, ex.Message)),
        _ => new FailureRpcException(new ConnectionFailure(ConnectionFailureKind.Other, ex.GetType().Name, null)),
    };

    /// <summary>True for a failure nobody classified (a bug, not a database error), which a host logs in full.</summary>
    public static bool IsUnclassified(RpcException rpc, Exception cause) =>
        rpc is FailureRpcException { Failure.Kind: ConnectionFailureKind.Other } && cause is not ConnectionFailureException and not ServingBackendException;
}
