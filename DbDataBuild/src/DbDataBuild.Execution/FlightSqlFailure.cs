namespace DbDataBuild.Execution;

/// <summary>
/// A Flight SQL failure in words that quote nothing: a login value that cannot be used or a token that cannot be had (our own sentence), or a gRPC status named by its kind
/// (unreachable, the TLS handshake, credentials refused, a deadline, a call the server does not offer). The server's message and the transport's text are never repeated: they can quote a user, a host or a value.
/// </summary>
public static class FlightSqlFailure
{
    /// <summary>The gRPC failure inside an exception, or null: the Flight SQL client wraps a call's RpcException in an InvalidOperationException.</summary>
    public static Grpc.Core.RpcException? Rpc(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException) if (e is Grpc.Core.RpcException rpc) return rpc;
        return null;
    }

    /// <summary>
    /// The sentence for a Flight SQL failure, or null when the exception is something else: a login value that cannot be used or a token that cannot be had (our own sentence), or a gRPC status named by its kind
    /// (unreachable, the TLS handshake, credentials refused, a deadline, a call the server does not offer). The server's message and the transport's text are never repeated: they can quote a user, a host or a value.
    /// </summary>
    public static string? Describe(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is FlightSqlLoginException login) return $"{login.Message} (FlightSqlLoginException)";
            if (e is not Grpc.Core.RpcException rpc) continue;
            var status = rpc.StatusCode;
            var tls = false;
            // the transport's failure to set up TLS comes as Unavailable from a call and as Internal from a handshake: it is an AuthenticationException somewhere down the chain either way
            foreach (var start in new[] { rpc.Status.DebugException, rpc.InnerException })
                for (var inner = start; inner != null; inner = inner.InnerException)
                    if (inner is System.Security.Authentication.AuthenticationException) tls = true;
            var sentence = status switch
            {
                _ when tls && status != Grpc.Core.StatusCode.Unauthenticated => "the TLS handshake with the Flight SQL server failed (the certificate is not trusted or does not match the host, or the server does not speak TLS: see ca_file and tls_skip_verify)",
                Grpc.Core.StatusCode.Unavailable => "the Flight SQL server could not be reached (nothing answered at that host and port, or it does not speak TLS where TLS was asked for, or the other way round)",
                Grpc.Core.StatusCode.Unauthenticated => "the Flight SQL server refused the credentials (the token or the user and password)",
                Grpc.Core.StatusCode.PermissionDenied => "the Flight SQL server refused the account permission for that",
                Grpc.Core.StatusCode.DeadlineExceeded => "the Flight SQL server did not answer in time",
                Grpc.Core.StatusCode.Unimplemented => "the Flight SQL server does not offer that call",
                Grpc.Core.StatusCode.Cancelled => "the call to the Flight SQL server was cancelled",
                _ => "the Flight SQL server reported an error",
            };
            return $"{sentence} (RpcException, gRPC {status})";
        }
        return null;
    }
}
