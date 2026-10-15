using Microsoft.AspNetCore.Http;

namespace DbData.Hosting;

/// <summary>
/// Generalised <c>UpdateDrainMiddleware</c>: while draining, new work is refused (mutations only, or everything,
/// per options) except exempt prefixes. Admitted requests are tracked so the drain waits for them.
/// </summary>
public sealed class DrainMiddleware(RequestDelegate next, IDrainCoordinator drain, DbDataHostOptions options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var exempt = IsExempt(context.Request.Path);
        if (drain.IsDraining && !exempt)
        {
            if (options.DrainRejection == DrainRejection.All)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.Headers.RetryAfter = "5";
                await context.Response.WriteAsJsonAsync(new { error = "The service is stopping and not accepting new requests." });
                return;
            }

            if (IsMutation(context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                await context.Response.WriteAsJsonAsync(new { error = "The service is draining for a restart or update, so changes are paused." });
                return;
            }
        }

        using var work = drain.TrackWork(allowWhileDraining: true);
        await next(context);
    }

    private static bool IsMutation(string method) =>
        !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method);

    private bool IsExempt(PathString path) =>
        options.DrainExemptPrefixes.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Caps concurrent in-flight requests; the overflow gets 503 instead of queueing without bound.</summary>
public sealed class ConcurrencyLimitMiddleware(RequestDelegate next, DbDataHostOptions options) : IDisposable
{
    private readonly SemaphoreSlim _slots = new(options.Limits.MaxConcurrentRequests, options.Limits.MaxConcurrentRequests);

    public async Task InvokeAsync(HttpContext context)
    {
        if (options.DrainExemptPrefixes.Any(p => context.Request.Path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase)))
        {
            await next(context);
            return;
        }

        if (!await _slots.WaitAsync(0, context.RequestAborted))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.RetryAfter = "1";
            return;
        }

        try
        {
            await next(context);
        }
        finally
        {
            _slots.Release();
        }
    }

    public void Dispose() => _slots.Dispose();
}
