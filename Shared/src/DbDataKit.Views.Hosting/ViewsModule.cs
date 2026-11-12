using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Hosting;
using DbDataKit.Serving;
using DbDataKit.State;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DbDataKit.Views.Hosting;

/// <summary>
/// The serving module <b>views</b> (plan 32 DBR11; name <c>views</c> in <c>server.yaml</c>'s <c>modules:</c>): published dashboards and reports over REST, on a listener of its own (J28's design: off unless enabled, authenticated with the tokens of the
/// serving layer's identity and apps modules) or as routes a host mounts with <see cref="Map"/>.
/// <code>
/// GET  /api/v1/dashboards                 {items: [{name, title, version}]}          (and /api/v1/reports)
/// GET  /api/v1/dashboards/{name}          {kind, name, title, version, as_of, text, document, layouts: [{name, type, default}]}
/// POST /api/v1/dashboards/{name}/render   {parameters?, layout?, only?, viewport?: {width, height}, medium?} gives the rendered-widgets document
/// POST /api/v1/views/preview              {document|text|widget, widget_id?, layout?, only?, parameters?} the rendered widgets of an unsaved draft, as the caller, stored nowhere (plan 32 DBR28)
/// GET  /api/v1/dashboards/{name}/export?format=pdf&amp;layout=&amp;p.name=   the PDF (501 export_unavailable naming the setting when the host has no browser)
/// GET  /api/v1/apps/{instance}/pages/{name}/export?format=pdf|png&amp;p.name=   a custom page of an app (plan 32 DBR33), exported as the person: the parameters are read against the page's manifest entry
/// GET  /api/v1/dashboards/{name}/export?format=csv|xlsx|svg&amp;widget=&amp;layout=&amp;p.name=   a widget's rows (csv, xlsx: no browser), a dashboard's sheets (xlsx without widget), a chart's SVG
/// GET  /api/v1/dashboards/{name}/print?layout=   the print page as HTML (the browser's own print)
/// GET  /api/v1/views/print/{file}         print.js, print.css and the bundled fonts (no token: code, no data)
/// GET  /api/v1/my/views?scope=mine|shared|all   a person's own dashboards and reports, and the ones shared with them (plan 32 DBR12); under /api/v1/my/dashboards/{name} and /reports/{name}:
///      GET ?owner= (open), PUT {text, expected_version?} (save), DELETE, POST /render?owner=, GET /history[?version=], POST /restore {version, expected_version}, GET|PUT /shares, GET /promote ({path, text}, a file the views loader reads)
/// GET  /api/v1/catalog[?folder=&amp;search=&amp;types=], GET /api/v1/catalog/item?key=, POST /api/v1/catalog/open {key, parameters?, layout?, format?}   the merged report catalog (plan 32 RC03, see <see cref="ViewsApi"/>)
/// GET  /health, /health/ready, /health/live
/// </code>
/// A refusal is <c>{code, message}</c> with its status: 400 invalid, 401 unauthenticated, 403 denied (an app's token narrowed past the document's models), 404 not_found, 413 too large, 500, 501 export_unavailable, 503 draining.
/// The module serves what a <see cref="DefinitionFollower"/> hands <see cref="ViewsPublications"/>; a version it cannot load leaves the one served and shows in <see cref="Health"/>.
/// </summary>
public sealed class ViewsModule(ViewsOptions options, ViewsPublications published, IViewsCallers callers, IViewsAudit audit, ILoggerFactory? loggers = null) : IServingModule, IAsyncDisposable
{
    public const string ModuleName = "views";

    private readonly IDrainCoordinator drain = options.Drain ?? new DrainCoordinator();
    private readonly ConcurrentDictionary<string, string> problems = new(StringComparer.Ordinal);
    private ViewsApi? api;
    private DefinitionFollower? follower;
    private WebApplication? app;
    private CancellationTokenSource? polling;
    private Task? pollLoop;
    private volatile bool stopped;

    public string Name => ModuleName;

    /// <summary>What the module answers with; null until it started. The apps host calls it in process with a narrowed principal.</summary>
    public ViewsApi? Api => api;

    /// <summary>The base address of the module's own listener once it started (<c>http://127.0.0.1:41234</c>), or null when it has none.</summary>
    public string? Address { get; private set; }

    public async Task StartAsync(IServingServices services, CancellationToken cancellationToken = default)
    {
        api = new ViewsApi(published, services, options, audit, drain);
        if (options.Follow)
        {
            follower = published.Follower(OnProblem);
            follower.Start();
            polling = new CancellationTokenSource();
            pollLoop = Task.Run(() => PollAsync(polling.Token), CancellationToken.None);
        }
        stopped = false;
        if (options.Exporter is { } exporter)
        {
            // the browser is looked at once now (it starts, is the pinned version, the fonts match); a host that cannot export says why in /health and keeps serving screens
            try { await exporter.CheckAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception e) when (e is not (OperationCanceledException or OutOfMemoryException)) { loggers?.CreateLogger("Views").LogWarning(e, "The export browser could not be checked."); }
            if (!exporter.Availability.Available) loggers?.CreateLogger("Views").LogWarning("Export is not available: {Reason}", exporter.Availability.Reason);
        }
        if (options.Listen is { } listen) await ListenAsync(listen, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Applies what the group published since the last look, now rather than at the next poll (a host that polls itself calls its own follower instead).</summary>
    public int Refresh() => follower?.Poll() ?? 0;

    /// <summary>A host that runs the follower itself tells the module of a version that could not be used, so the health says why.</summary>
    public void OnProblem(string kind, string name, int? version, Exception error)
    {
        problems[$"{kind}/{name}"] = $"{kind} '{name}' version {version?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "(retired)"}: {error.Message}";
        loggers?.CreateLogger("Views").LogWarning(error, "The {Kind} {Name} could not be used; the version served before keeps serving.", kind, name);
    }

    private async Task PollAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(options.PollInterval, options.Time);
        try
        {
            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
            {
                try { follower!.Poll(); }
                catch (Exception e) when (e is not OutOfMemoryException) { OnProblem("definitions", "group", null, e); }
            }
        }
        catch (OperationCanceledException) { }
    }

    // ---- drain, stop, health ----

    public async Task DrainAsync(CancellationToken cancellationToken = default)
    {
        if (!await drain.DrainAsync(options.DrainTimeout, cancellationToken).ConfigureAwait(false))
            loggers?.CreateLogger("Views").LogWarning("The views module was still serving {Count} request(s) when the drain timeout ended.", drain.ActiveWork);
    }

    /// <summary>Takes the module out of the drain (a cancelled upgrade).</summary>
    public void Resume() => drain.Resume();

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        stopped = true;
        if (polling is not null) { await polling.CancelAsync().ConfigureAwait(false); if (pollLoop is not null) await pollLoop.ConfigureAwait(false); }
        if (app is not null) { await app.StopAsync(cancellationToken).ConfigureAwait(false); Address = null; }
    }

    public ServingHealth Health()
    {
        if (api is null || stopped) return new ServingHealth(ServingHealthState.Down, api is null ? "The module has not started." : "The module is stopped.");
        if (drain.IsDraining) return new ServingHealth(ServingHealthState.Degraded, "Draining: new requests are refused and the ones in flight finish.");
        try
        {
            var unserved = published.Unserved();
            if (unserved.Count > 0)
                return new ServingHealth(ServingHealthState.Degraded, string.Join(" | ", unserved.Select(u =>
                    problems.GetValueOrDefault($"{u.Kind}/{u.Name}") ?? $"{u.Kind} '{u.Name}': the group serves version {u.Head} and this instance serves {(u.Served is { } s ? s.ToString(System.Globalization.CultureInfo.InvariantCulture) : "none")}")));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new ServingHealth(ServingHealthState.Degraded, $"The group's definitions could not be read ({e.GetType().Name}); what was loaded keeps serving.");
        }
        return ServingHealth.Healthy;
    }

    // ---- the listener and the routes ----

    private async Task ListenAsync(ViewsListen listen, CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = ["--hostBuilder:reloadConfigOnChange=false"], ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Listen(IPAddress.Parse(listen.Address), listen.Port, l =>
            {
                l.Protocols = HttpProtocols.Http1AndHttp2;
                if (listen.Certificate is { } certificate) l.UseHttps(certificate);
            });
        });
        var built = builder.Build();
        Map(built);
        await built.StartAsync(cancellationToken).ConfigureAwait(false);
        app = built;
        var bound = built.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        Address = bound.Replace("[::]", listen.Address, StringComparison.Ordinal).TrimEnd('/');
    }

    /// <summary>Maps the routes and the health endpoints onto a host's own routing (or the module's listener).</summary>
    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/health/live", () => Results.Text("Healthy"));
        foreach (var path in new[] { "/health", "/health/ready" })
            routes.MapGet(path, () =>
            {
                if (api is null) return Json(new ViewsAnswer(503, new JsonObject { ["module"] = ModuleName, ["state"] = "down", ["detail"] = "The module has not started." }), null);
                var state = Health();
                var answer = new ViewsAnswer(state.State == ServingHealthState.Down || drain.IsDraining ? 503 : 200, api.Health(state));
                return Json(answer, answer.Status == 503 ? new Dictionary<string, string> { ["Retry-After"] = "5" } : null);
            });

        // the files of the print page (the visuals' print bundle and the bundled fonts): code and fonts, no data, so no token
        routes.MapGet(ViewsApi.PrintAssetRoute + "{**file}", (string file) =>
            api?.PrintAsset(file) is { } asset ? Results.Bytes(asset.Content, asset.ContentType) : Results.NotFound());

        // the preview of an unsaved document or widget (plan 32 DBR28): resolved as the caller, stored nowhere
        routes.MapPost("/api/v1/views/preview", (Func<HttpContext, Task<IResult>>)(c => As(c, async (who, trace) =>
        {
            var (body, refusal) = await ReadBodyAsync(c, options.MaxPreviewBytes, "A preview request").ConfigureAwait(false);
            return refusal ?? await api!.PreviewAsync(who, body, c.Connection.RemoteIpAddress?.ToString(), trace, c.RequestAborted).ConfigureAwait(false);
        })));

        MapMine(routes);

        // the merged report catalog (plan 32 RC03): our own documents and the external report servers', filtered for the person
        routes.MapGet("/api/v1/catalog", (Func<HttpContext, Task<IResult>>)(c => As(c, (who, trace) => api!.CatalogBrowseAsync(who, Query(c, "folder"), Query(c, "search"), Query(c, "types"), trace, c.RequestAborted))));
        routes.MapGet("/api/v1/catalog/item", (Func<HttpContext, Task<IResult>>)(c => As(c, (who, trace) => api!.CatalogItemAsync(who, Query(c, "key"), trace, c.RequestAborted))));
        routes.MapPost("/api/v1/catalog/create", (Func<HttpContext, Task<IResult>>)(c => As(c, async (who, trace) =>
        {
            var (body, refusal) = await ReadBodyAsync(c, options.MaxBodyBytes, "A create request").ConfigureAwait(false);
            return refusal ?? await api!.CatalogCreateAsync(who, body, trace, c.RequestAborted).ConfigureAwait(false);
        })));
        routes.MapPost("/api/v1/catalog/open", (Func<HttpContext, Task<IResult>>)(c => As(c, async (who, trace) =>
        {
            var (body, refusal) = await ReadBodyAsync(c, options.MaxBodyBytes, "An open request").ConfigureAwait(false);
            return refusal ?? await api!.CatalogOpenAsync(who, body, trace, c.RequestAborted).ConfigureAwait(false);
        })));

        // a custom page of an app (plan 32 DBR33): its export as the person, the parameters as `p.name=value` read against the manifest entry
        routes.MapGet("/api/v1/apps/{instance}/pages/{name}/export", (Func<string, string, HttpContext, Task<IResult>>)((instance, name, c) =>
            As(c, (who, trace) => api!.ExportPageAsync(who, instance, name, Query(c, "format"), PageParameters(c), c.Connection.RemoteIpAddress?.ToString(), trace, c.RequestAborted))));

        foreach (var kind in ViewDefinitions.Kinds)
        {
            var prefix = "/api/v1/" + ViewsApi.Collection(kind);
            routes.MapGet(prefix, (Func<HttpContext, Task<IResult>>)(c => As(c, (who, trace) => Task.FromResult(api!.List(who, kind, trace)))));
            routes.MapGet(prefix + "/{name}", (Func<string, HttpContext, Task<IResult>>)((name, c) => As(c, (who, trace) => Task.FromResult(api!.Open(who, kind, name, trace)))));
            routes.MapGet(prefix + "/{name}/export", (Func<string, HttpContext, Task<IResult>>)((name, c) =>
                As(c, (who, trace) => api!.ExportAsync(who, kind, name, Query(c, "format"), Query(c, "layout"), Query(c, "widget"), Parameters(c), c.Connection.RemoteIpAddress?.ToString(), trace, c.RequestAborted))));
            routes.MapGet(prefix + "/{name}/print", (Func<string, HttpContext, Task<IResult>>)((name, c) =>
                As(c, (who, trace) => api!.PrintPageAsync(who, kind, name, Query(c, "layout"), Parameters(c), c.Connection.RemoteIpAddress?.ToString(), trace, c.RequestAborted))));
            routes.MapPost(prefix + "/{name}/render", (Func<string, HttpContext, Task<IResult>>)((name, c) => As(c, async (who, trace) =>
            {
                var (body, refusal) = await ReadBodyAsync(c, options.MaxBodyBytes, "A render request").ConfigureAwait(false);
                return refusal ?? await api!.RenderAsync(who, kind, name, body, c.Connection.RemoteIpAddress?.ToString(), trace, c.RequestAborted).ConfigureAwait(false);
            })));
        }
    }

    /// <summary>The routes of a person's own dashboards and reports (plan 32 DBR12; see <see cref="ViewsApi"/>): the same token as every route, and the caller is the owner of what they save.</summary>
    private void MapMine(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/v1/my/views", (Func<HttpContext, Task<IResult>>)(c => As(c, (who, trace) => Task.FromResult(api!.MyList(who, Query(c, "scope"), Query(c, "kind"), trace)))));
        foreach (var kind in ViewDefinitions.Kinds)
        {
            var prefix = ViewsApi.MyPrefix + ViewsApi.Collection(kind);
            routes.MapGet(prefix + "/{name}", (Func<string, HttpContext, Task<IResult>>)((name, c) => As(c, (who, trace) => Task.FromResult(api!.MyOpen(who, kind, name, Query(c, "owner"), trace)))));
            routes.MapDelete(prefix + "/{name}", (Func<string, HttpContext, Task<IResult>>)((name, c) => As(c, (who, trace) => Task.FromResult(api!.MyDelete(who, kind, name, trace)))));
            routes.MapPut(prefix + "/{name}", (Func<string, HttpContext, Task<IResult>>)((name, c) => As(c, async (who, trace) =>
            {
                var (body, refusal) = await ReadBodyAsync(c, options.MaxPreviewBytes, "A save").ConfigureAwait(false);
                return refusal ?? api!.MySave(who, kind, name, body, trace);
            })));
            routes.MapPost(prefix + "/{name}/render", (Func<string, HttpContext, Task<IResult>>)((name, c) => As(c, async (who, trace) =>
            {
                var (body, refusal) = await ReadBodyAsync(c, options.MaxBodyBytes, "A render request").ConfigureAwait(false);
                return refusal ?? await api!.MyRenderAsync(who, kind, name, Query(c, "owner"), body, c.Connection.RemoteIpAddress?.ToString(), trace, c.RequestAborted).ConfigureAwait(false);
            })));
            routes.MapGet(prefix + "/{name}/history", (Func<string, HttpContext, Task<IResult>>)((name, c) => As(c, (who, trace) => Task.FromResult(api!.MyHistory(who, kind, name, Query(c, "version"), trace)))));
            routes.MapPost(prefix + "/{name}/restore", (Func<string, HttpContext, Task<IResult>>)((name, c) => As(c, async (who, trace) =>
            {
                var (body, refusal) = await ReadBodyAsync(c, options.MaxBodyBytes, "A restore").ConfigureAwait(false);
                return refusal ?? api!.MyRestore(who, kind, name, body, trace);
            })));
            routes.MapGet(prefix + "/{name}/shares", (Func<string, HttpContext, Task<IResult>>)((name, c) => As(c, (who, trace) => Task.FromResult(api!.MyShares(who, kind, name, null, write: false, trace)))));
            routes.MapPut(prefix + "/{name}/shares", (Func<string, HttpContext, Task<IResult>>)((name, c) => As(c, async (who, trace) =>
            {
                var (body, refusal) = await ReadBodyAsync(c, options.MaxBodyBytes, "A share list").ConfigureAwait(false);
                return refusal ?? api!.MyShares(who, kind, name, body, write: true, trace);
            })));
            routes.MapGet(prefix + "/{name}/promote", (Func<string, HttpContext, Task<IResult>>)((name, c) => As(c, (who, trace) => Task.FromResult(api!.MyPromote(who, kind, name, trace)))));
        }
    }

    private static string? Query(HttpContext c, string key) => c.Request.Query[key].ToString() is { Length: > 0 } v ? v : null;

    /// <summary>The document's parameters from the address: <c>p.region=West</c>, repeated for a list (<c>p.region=West&amp;p.region=East</c>).</summary>
    private static Dictionary<string, JsonNode?> Parameters(HttpContext c)
    {
        var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var (key, list) in c.Request.Query)
        {
            if (!key.StartsWith("p.", StringComparison.Ordinal) || key.Length == 2) continue;
            values[key[2..]] = list.Count == 1 ? JsonValue.Create(list[0]) : new JsonArray([.. list.Select(v => (JsonNode?)JsonValue.Create(v))]);
        }
        return values;
    }

    /// <summary>A page's parameters from the address: <c>p.region=West</c>, repeated for a list; the manifest's declaration gives each its type.</summary>
    private static List<KeyValuePair<string, string[]>> PageParameters(HttpContext c) =>
        [.. c.Request.Query.Where(q => q.Key.StartsWith("p.", StringComparison.Ordinal) && q.Key.Length > 2).Select(q => KeyValuePair.Create(q.Key[2..], q.Value.Select(v => v ?? "").ToArray()))];

    private async Task<IResult> As(HttpContext context, Func<ViewsPrincipal, string, Task<ViewsAnswer>> call)
    {
        if (api is null || stopped) return Json(ViewsAnswer.Error(503, "unavailable", "The views module is not running."), new Dictionary<string, string> { ["Retry-After"] = "5" });
        var address = context.Connection.RemoteIpAddress?.ToString();
        var who = await callers.AuthenticateAsync(context.Request.Headers.Authorization.ToString() is { Length: > 0 } h ? h : null, address, context.RequestAborted).ConfigureAwait(false);
        if (who is null)
            return Json(ViewsAnswer.Error(401, "unauthenticated", "A token of the identity home is needed (a personal access token, or an access token it issued)."), new Dictionary<string, string> { ["WWW-Authenticate"] = "Bearer" });
        var trace = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
        try
        {
            return Json(await call(who, trace).ConfigureAwait(false), null);
        }
        catch (Exception e) when (ViewsApi.IsAuditFailure(e))
        {
            loggers?.CreateLogger("Views").LogError(e.InnerException, "An audit entry could not be written; the answer was not given.");
            return Json(ViewsAnswer.Error(500, "audit_unavailable", "The request was not answered because it could not be recorded in the audit."), null);
        }
    }

    private async Task<(JsonObject? Body, ViewsAnswer? Refusal)> ReadBodyAsync(HttpContext context, int limit, string what)
    {
        if (context.Request.ContentLength > limit) return (null, ViewsAnswer.Error(413, "too_large", $"{what} is at most {limit} bytes."));
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > limit) return (null, ViewsAnswer.Error(413, "too_large", $"{what} is at most {limit} bytes."));
        }
        if (buffer.Length == 0) return (null, null);
        try
        {
            return JsonNode.Parse(buffer.ToArray()) is JsonObject o ? (o, null) : (null, ViewsAnswer.Error(400, "invalid", "The body must be a JSON object."));
        }
        catch (JsonException e)
        {
            return (null, ViewsAnswer.Error(400, "invalid", $"The body is not JSON: {e.Message}"));
        }
    }

    private static IResult Json(ViewsAnswer answer, IReadOnlyDictionary<string, string>? headers) =>
        new JsonResult(answer, headers);

    private sealed class JsonResult(ViewsAnswer answer, IReadOnlyDictionary<string, string>? extra) : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            context.Response.StatusCode = answer.Status;
            context.Response.ContentType = answer.File?.ContentType ?? "application/json; charset=utf-8";
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            if (answer.File?.FileName is { } fileName) context.Response.Headers.ContentDisposition = new System.Net.Mime.ContentDisposition { FileName = fileName, DispositionType = "attachment" }.ToString();
            if (answer.File is { } file && file.ContentType.StartsWith("text/html", StringComparison.Ordinal)) context.Response.Headers.ContentSecurityPolicy = "default-src 'self' data: blob:; script-src 'self'; style-src 'self' 'unsafe-inline'; connect-src 'none'";
            foreach (var (key, value) in (answer.Headers ?? new Dictionary<string, string>()).Concat(extra ?? new Dictionary<string, string>())) context.Response.Headers[key] = value;
            if (answer.File is { } bytes)
            {
                await context.Response.Body.WriteAsync(bytes.Content, context.RequestAborted).ConfigureAwait(false);
                return;
            }
            await context.Response.WriteAsync(answer.Body.ToJsonString(), Encoding.UTF8, context.RequestAborted).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        stopped = true;
        polling?.Cancel();
        if (app is not null) await app.DisposeAsync().ConfigureAwait(false);
        polling?.Dispose();
    }
}
