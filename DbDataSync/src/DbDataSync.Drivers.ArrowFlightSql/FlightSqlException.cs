using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Grpc.Core;

namespace DbDataSync.Drivers.ArrowFlightSql;

/// <summary>
/// A failed call to a Flight SQL server, as a <see cref="DbException"/>. gRPC reports failure with an
/// <see cref="RpcException"/>, which is not one — and every caller in this codebase that is prepared for
/// a database error (<c>IConnectionTester.TestAsync</c> in every driver, among others) catches
/// <see cref="DbException"/>, so letting the gRPC type through would turn "wrong password" from a
/// reported test failure into an unhandled exception.
/// </summary>
public sealed class FlightSqlException : DbException
{
    public StatusCode StatusCode { get; }

    /// <summary>True when the failure was the TLS handshake (gRPC reports it as Internal, with the cause only in the detail text).</summary>
    public bool IsTls { get; }

    public FlightSqlException(string message, StatusCode statusCode, Exception? inner = null, bool isTls = false)
        : base(message, inner)
    {
        StatusCode = statusCode;
        IsTls = isTls;
    }

    /// <summary>
    /// <see cref="From"/> for any exception that is, or wraps, a gRPC failure. The Flight SQL client
    /// reports a failed call as an <see cref="InvalidOperationException"/> ("Failed to get flight info")
    /// with the <see cref="RpcException"/> as its inner exception, so catching <see cref="RpcException"/>
    /// alone would miss most failures — including a wrong password. A cancelled call is left alone: that is
    /// the caller's own cancellation and must stay one.
    /// </summary>
    public static bool TryWrap(Exception ex, [NotNullWhen(true)] out FlightSqlException? wrapped)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is RpcException { StatusCode: not StatusCode.Cancelled } rpc)
            {
                wrapped = From(rpc);
                return true;
            }
        }

        wrapped = null;
        return false;
    }

    public static FlightSqlException From(RpcException ex)
    {
        var detail = string.IsNullOrWhiteSpace(ex.Status.Detail) ? "" : $": {ex.Status.Detail}";
        var hint = ex.StatusCode switch
        {
            StatusCode.Unauthenticated => " (the server rejected the credentials)",
            StatusCode.PermissionDenied => " (authenticated, but not permitted to do that)",
            StatusCode.Unavailable => " (the server could not be reached)",
            // The TLS handshake fails before any RPC, which gRPC reports as Internal with the cause
            // only in the detail text.
            _ when IsTlsFailure(ex.Status.Detail) =>
                " (TLS failed — if the server's certificate is self-signed or from a private authority, " +
                "enable 'Skip certificate validation' or trust the authority on this machine)",
            _ => "",
        };

        return new FlightSqlException($"Flight SQL call failed — {ex.StatusCode}{hint}{detail}", ex.StatusCode, ex,
            isTls: ex.StatusCode is not (StatusCode.Unauthenticated or StatusCode.PermissionDenied or StatusCode.Unavailable) && IsTlsFailure(ex.Status.Detail));
    }

    private static bool IsTlsFailure(string detail) =>
        detail.Contains("SSL", StringComparison.OrdinalIgnoreCase)
        || detail.Contains("certificate", StringComparison.OrdinalIgnoreCase)
        || detail.Contains("AuthenticationException", StringComparison.OrdinalIgnoreCase);
}
