using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DbDataKit.Telemetry;

/// <summary>The request span and request counters of a host (plan 22a GP28), and the continuation of a caller's W3C trace (GP29).</summary>
public static class RequestTelemetry
{
    /// <summary>
    /// Adds the middleware that makes one span per request and counts it. The span is named <c>METHOD route-template</c>; its attributes are the method, the route template (never the path or the query string), the status code, the product and,
    /// when the request failed, the exception's type. A <c>traceparent</c> header on the request makes the span a child of the caller's, so a call the console relays and the server it reaches are one trace. Health and version calls are left out.
    /// </summary>
    public static IApplicationBuilder UseRequestTelemetry(this IApplicationBuilder app, string product)
    {
        ArgumentNullException.ThrowIfNull(app);
        var name = HostTelemetry.Name(product);
        return app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/health") || context.Request.Path.StartsWithSegments("/version"))
            {
                await next(context);
                return;
            }

            var method = KnownMethod(context.Request.Method);
            ActivityContext parent = default;
            if (context.Request.Headers.TryGetValue("traceparent", out var header)) ActivityContext.TryParse(header.ToString(), context.Request.Headers["tracestate"].ToString(), isRemote: true, out parent);
            var started = Stopwatch.GetTimestamp();
            // the span is named when it ends, once the route is known: it starts with the method alone
            using var span = SuiteTelemetry.Source.StartActivity(method, ActivityKind.Server, parent == default ? Activity.Current?.Context ?? default : parent);
            string? failure = null;
            try
            {
                await next(context);
            }
            catch (Exception ex)
            {
                failure = ex.GetType().FullName;
                throw;
            }
            finally
            {
                var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unmatched";
                var status = failure is null ? context.Response.StatusCode : 500;
                if (span is not null)
                {
                    span.DisplayName = method + " " + route;
                    span.SetTag("http.request.method", method);
                    span.SetTag("http.route", route);
                    span.SetTag("http.response.status_code", status);
                    span.SetTag(HostTelemetry.ProductTag, name);
                    if (failure is not null) span.SetTag("error.type", failure);
                    if (status >= 500) span.SetStatus(ActivityStatusCode.Error);
                }

                var tags = new TagList { { "http.request.method", method }, { "http.route", route }, { "http.response.status_code", status }, { HostTelemetry.ProductTag, name } };
                HostTelemetry.Requests.Add(1, tags);
                HostTelemetry.RequestDuration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, tags);
            }
        });
    }

    private static string KnownMethod(string method) =>
        HttpMethods.IsGet(method) ? "GET" : HttpMethods.IsPost(method) ? "POST" : HttpMethods.IsPut(method) ? "PUT" : HttpMethods.IsDelete(method) ? "DELETE" : HttpMethods.IsPatch(method) ? "PATCH" : HttpMethods.IsHead(method) ? "HEAD" : "OTHER";
}
