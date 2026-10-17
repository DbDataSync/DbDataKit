using DbDataKit.Connections;
using Grpc.Core;

namespace DbDataProxy.Server.Flight;

/// <summary>
/// The status a client sees for a database failure (kind, code and a fixed sentence, see <see cref="ProxyFlightServer.ToRpcException"/>), carrying the
/// classified failure so the call pipeline can react to it (a rejected pass-through login feeds the login throttle) without parsing text.
/// </summary>
public sealed class FailureRpcException(ConnectionFailure failure)
    : RpcException(new Status(ProxyFlightServer.StatusFor(failure.Kind), failure.PublicMessage))
{
    public ConnectionFailure Failure { get; } = failure;
}
