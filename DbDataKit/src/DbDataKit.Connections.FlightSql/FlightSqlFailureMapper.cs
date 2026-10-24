using DbDataKit.Connections;
using DbDataKit.Dialect;
using Grpc.Core;

namespace DbDataKit.Connections.FlightSql;

/// <summary>Maps a Flight SQL call's gRPC status to the shared failure kinds (plan 13, A10). The status name is the public code; the server's detail text is never used.</summary>
public sealed class FlightSqlFailureMapper : IFailureMapper
{
    public ConnectionFailure? Map(Exception exception, EngineId engine)
    {
        if (exception is not FlightSqlException flight) return null;
        var kind = flight.IsTls ? ConnectionFailureKind.Tls : flight.StatusCode switch
        {
            StatusCode.Unauthenticated => ConnectionFailureKind.Login,
            StatusCode.PermissionDenied => ConnectionFailureKind.Permission,
            StatusCode.Unavailable => ConnectionFailureKind.Network,
            StatusCode.DeadlineExceeded => ConnectionFailureKind.Timeout,
            StatusCode.Unimplemented => ConnectionFailureKind.Unsupported,
            StatusCode.InvalidArgument or StatusCode.NotFound or StatusCode.AlreadyExists => ConnectionFailureKind.Syntax,
            _ => ConnectionFailureKind.Other,
        };
        return new ConnectionFailure(kind, flight.StatusCode.ToString(), engine);
    }
}
