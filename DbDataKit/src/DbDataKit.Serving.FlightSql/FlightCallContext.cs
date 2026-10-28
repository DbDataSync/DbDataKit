using Grpc.Core;
using Microsoft.AspNetCore.Http;

namespace DbDataKit.Serving.FlightSql;

/// <summary>The serving context of the call being served, kept in the call's user state by the module's interceptor so a handler (and a host's own interceptors, which run after it) can read who is calling.</summary>
public static class FlightCallContext
{
    public const string ContextKey = "dbdatakit.serving.context";

    /// <summary>The caller's network address without the port, used to scope brute-force throttling.</summary>
    public static string ClientAddress(this ServerCallContext context) => context.GetHttpContext().Connection.RemoteIpAddress?.ToString() ?? "";

    public static ServingContext GetServingContext(this ServerCallContext context) =>
        context.UserState.TryGetValue(ContextKey, out var c) && c is ServingContext serving
            ? serving
            : throw new RpcException(new Status(StatusCode.Unauthenticated, "Not authenticated."));

    public static ServingCaller GetCaller(this ServerCallContext context) => context.GetServingContext().Caller;

    /// <summary>The data source the call names in its <c>x-datasource</c> header, or an empty name for the caller's default.</summary>
    public static string RequestedSource(this ServerCallContext context) => context.RequestHeaders.GetValue(FlightSqlOptions.DataSourceHeader) ?? "";

    public static string? GetValue(this Grpc.Core.Metadata headers, string key) => headers.FirstOrDefault(h => string.Equals(h.Key, key, StringComparison.OrdinalIgnoreCase) && !h.IsBinary)?.Value;
}
