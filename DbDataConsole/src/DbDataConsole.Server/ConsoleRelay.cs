using System.Collections.Concurrent;
using DbDataKit.State;
using DbDataKit.Web.Auth;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace DbDataConsole.Server;

/// <summary>
/// The console's relay (plan 13, D16 and U08): a module of the web app talks to a server it manages by sending its usual <c>/api/...</c> call to <c>/relay/{server}/api/...</c> on the console, which forwards it over the pin to
/// that server with a short-lived signed token naming the signed-in person, and answers with what the server answered. The browser never holds a credential for a server and never reaches one directly; the server decides
/// what the person may do from the roles in the token, which the console's ceiling for that server limits.
/// <para>
/// Only <c>api/...</c> and <c>hubs/...</c> are relayed, only to a registered server, and the path is not interpreted: nothing the browser sends can name another host. A runner is the one server whose calls are limited further
/// (<see cref="ServerProducts.RunnerRoutes"/>): only the routes of its table pass, and only for a person who holds the role the runner asks for there; a product's reports and a webhook are not a console's to make.
/// </para>
/// </summary>
public sealed class ConsoleRelay(ConsoleInstances instances, ConsoleKeyStore keys, CurrentUser current, ILogger<ConsoleRelay> log, DbDataKit.Identity.FederatedUsers? federated = null)
{
    public const string Prefix = "/relay";

    private static readonly string[] ForwardedRequestHeaders = ["Accept", "Accept-Language", "If-Match", "If-None-Match", "Content-Type"];
    private static readonly string[] ForwardedResponseHeaders = ["ETag", "Content-Disposition", "Cache-Control", "Last-Modified", "Location"];

    private readonly ConcurrentDictionary<string, HttpClient> _clients = new(StringComparer.Ordinal);

    /// <summary>What each server said it is, kept for a few minutes so a relayed call does not ask first every time (a stale answer is used when the server cannot be asked).</summary>
    private readonly ConcurrentDictionary<string, (string? Product, DateTimeOffset At)> _products = new(StringComparer.Ordinal);
    private static readonly TimeSpan ProductAge = TimeSpan.FromMinutes(5);

    private async Task<string?> ProductOfAsync(RegisteredInstance instance, CancellationToken ct)
    {
        if (_products.TryGetValue(instance.InstanceId, out var known) && DateTimeOffset.UtcNow - known.At < ProductAge) return known.Product;
        var status = await instances.StatusAsync(instance, ct);
        if (status.Product is { } product)
        {
            _products[instance.InstanceId] = (product, DateTimeOffset.UtcNow);
            return product;
        }
        // not asked successfully: what it said before, or nothing (the call then fails as it would have)
        return known.Product;
    }

    /// <summary>What a person may do on a server managed from here, until the identity home (U13) assigns roles per server: an administrator everything but publishing, a viewer reading.</summary>
    public static ServerRole RolesOf(UserRole role) => role == UserRole.Admin
        ? ServerRole.Admin | ServerRole.Operator | ServerRole.Author | ServerRole.Approver
        : ServerRole.Viewer;

    /// <summary>What the signed-in person holds on <paramref name="instance"/> (the lower of their role here and what the server lets this console grant), and per project when the identity home says so.</summary>
    private (ServerRole Roles, Dictionary<string, ServerRole>? Projects) HeldOn(RegisteredInstance instance, string userId, UserRole role)
    {
        // A server paired before the ceiling was recorded is asked for reading only.
        var ceiling = instance.Ceiling == ServerRole.None ? ServerRole.Viewer : instance.Ceiling;
        // A person who signs in through the identity home holds what the home says on this server and its projects; the console's ceiling still limits it.
        if (federated?.RolesOn(userId, instance.InstanceId) is { } held)
            return (ServerRoles.Lower(held.Roles, ceiling), held.Projects.ToDictionary(p => p.Key, p => ServerRoles.Lower(p.Value, ceiling)));
        return (ServerRoles.Lower(RolesOf(role), ceiling), null);
    }

    /// <summary>The token that says the signed-in person is calling <paramref name="instance"/>: the key that server knows, and the roles the person has there (the lower of their role here and what the server lets this console grant).</summary>
    private async Task<string> TokenForAsync(RegisteredInstance instance, string userId, string? userName, UserRole role, CancellationToken ct)
    {
        // The key this server knows: the current one, or the one a rotation is moving to if the server already has that.
        var identity = (instance.KeyFingerprint is { } fingerprint ? await keys.ForKeyAsync(fingerprint, ct) : null) ?? await keys.GetOrCreateAsync(ct);
        var (roles, projects) = HeldOn(instance, userId, role);
        return identity.Issuer().Issue(projects is null
            ? new ConsoleTokenRequest(instance.InstanceId, userId, userName, roles)
            : new ConsoleTokenRequest(instance.InstanceId, userId, userName, roles, projects));
    }

    /// <summary>
    /// A call the console makes itself on the signed-in person's behalf (T11 brokers a pairing with two of them): the same token a relayed call carries, so the server decides what the person may do. The answer is the status and the JSON body, or a 502 saying
    /// the server did not answer.
    /// </summary>
    public async Task<(int Status, System.Text.Json.Nodes.JsonNode? Body)> CallAsync(RegisteredInstance instance, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        if (current.Id is not { } userId || current.Role is not { } role) return (401, new System.Text.Json.Nodes.JsonObject { ["error"] = "Sign in first." });
        using var request = new HttpRequestMessage(method, new Uri(instance.Url, path));
        request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, await TokenForAsync(instance, userId, current.Name, role, ct));
        if (body is not null) request.Content = System.Net.Http.Json.JsonContent.Create(body);
        try
        {
            using var response = await ClientFor(instance).SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            System.Text.Json.Nodes.JsonNode? json = null;
            try { json = text.Length > 0 ? System.Text.Json.Nodes.JsonNode.Parse(text) : null; } catch (System.Text.Json.JsonException) { }
            return ((int)response.StatusCode, json);
        }
        catch (IncompatibleServerException ex)
        {
            return (502, new System.Text.Json.Nodes.JsonObject { ["error"] = ex.Message });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning("{Server} did not answer a {Method} {Path}: {Reason}", instance.Name, method, path, ex.GetType().Name);
            return (502, new System.Text.Json.Nodes.JsonObject { ["error"] = $"{instance.Name} did not answer." });
        }
    }

    public async Task RelayAsync(HttpContext context, string server, string path)
    {
        if (server == ConsoleRunnerSettings.ServerId)
        {
            // the embedded runner is answered in this process by its own routes (ConsoleRunnerApi); what those do not answer is not a call a console relays, and there is no server behind this id to forward it to
            var allowed = ServerProducts.Relayable(ServerProducts.Runner, context.Request.Method, path);
            await Refuse(context, allowed ? StatusCodes.Status404NotFound : StatusCodes.Status403Forbidden,
                allowed ? "The embedded runner has no such call." : "The console's embedded runner is a runner: the console serves only its management calls (status, instances, runs, tasks, packages and their life cycle).");
            return;
        }
        if (instances.Registry.Find(server) is not { } instance)
        {
            await Refuse(context, StatusCodes.Status404NotFound, "No such server is registered.");
            return;
        }
        if (!(path.StartsWith("api/", StringComparison.Ordinal) || path.StartsWith("hubs/", StringComparison.Ordinal)) || path.Split('/').Any(s => s is ".." or "."))
        {
            await Refuse(context, StatusCodes.Status400BadRequest, "Only the server's api/... and hubs/... calls are relayed.");
            return;
        }
        if (current.Id is not { } userId || current.Role is not { } role)
        {
            await Refuse(context, StatusCodes.Status401Unauthorized, "Sign in first.");
            return;
        }
        // a runner has calls for its publishers and the serving layer that a console does not make: only the documented management calls pass
        var product = await ProductOfAsync(instance, context.RequestAborted);
        if (!ServerProducts.Relayable(product, context.Request.Method, path))
        {
            await Refuse(context, StatusCodes.Status403Forbidden, ServerProducts.RefusalFor(product, instance.Name));
            return;
        }
        // the person makes the call with their own roles (owner decision of 2026-11-08): a call the person's roles do not cover is refused here, before the runner is asked; the runner checks the same roles again from the token
        if (product == ServerProducts.Runner && ServerProducts.RunnerRouteOf(context.Request.Method, path) is { } wanted)
        {
            var held = HeldOn(instance, userId, role).Roles;
            if (!wanted.Allows(held))
            {
                var holds = held == ServerRole.None ? "nothing" : string.Join(" and ", ServerRoles.Names(ServerRoles.Close(held)));
                await Refuse(context, StatusCodes.Status403Forbidden, $"This needs the {wanted.Needs} role on {instance.Name}, and you hold {holds} there.");
                return;
            }
        }

        // a Workspace server's routes each name the role they need: the relay checks it against what the person holds (in the route's project too) before forwarding, and the server checks it again
        if (ServerProducts.RoleNeeded(product, context.Request.Method, path) is { } needed)
        {
            var (roles, projects) = HeldOn(instance, userId, role);
            if (ServerProducts.ProjectOf(product, context.Request.Method, path) is { } project && projects?.TryGetValue(project, out var inProject) == true) roles |= inProject;
            if (!ServerRoles.Allows(roles, needed))
            {
                await Refuse(context, StatusCodes.Status403Forbidden, $"This needs the {needed.ToString().ToLowerInvariant()} role.");
                return;
            }
        }

        // a file of a package (plan 33, UE10) can be larger than Kestrel's default limit on a request body and takes longer than a call: the runner has its own size cap and answers 413 past it
        var upload = path.StartsWith("api/v1/artifacts/", StringComparison.Ordinal) && context.Request.Method == HttpMethods.Put;
        if (upload) context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize = null;

        var token = await TokenForAsync(instance, userId, current.Name, role, context.RequestAborted);

        if (context.WebSockets.IsWebSocketRequest)
        {
            await RelayWebSocketAsync(context, instance, path, token);
            return;
        }

        // a Workspace route names files: the decoded path goes on with each part escaped again
        var forwarded = product == ServerProducts.Workspace ? ServerProducts.EscapedPath(path) : path;
        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), new Uri(instance.Url, forwarded + context.Request.QueryString));
        request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, token);
        foreach (var name in ForwardedRequestHeaders.Where(n => n != "Content-Type"))
            if (context.Request.Headers.TryGetValue(name, out var values)) request.Headers.TryAddWithoutValidation(name, (IEnumerable<string>)values.ToArray()!);
        if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            request.Content = new StreamContent(context.Request.Body);
            if (context.Request.ContentType is { } type) request.Content.Headers.TryAddWithoutValidation("Content-Type", type);
        }

        try
        {
            using var response = await ClientFor(instance, upload).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            context.Response.StatusCode = (int)response.StatusCode;
            foreach (var name in ForwardedResponseHeaders)
                if (response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values)) context.Response.Headers[name] = values.ToArray();
            if (response.Content.Headers.ContentType is { } contentType) context.Response.ContentType = contentType.ToString();
            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
        catch (IncompatibleServerException ex)
        {
            await Refuse(context, StatusCodes.Status502BadGateway, ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !context.RequestAborted.IsCancellationRequested)
        {
            log.LogWarning("{Server} did not answer a relayed {Method} {Path}: {Reason}", instance.Name, context.Request.Method, path, ex.GetType().Name);
            await Refuse(context, StatusCodes.Status502BadGateway, $"{instance.Name} did not answer.");
        }
    }

    /// <summary>
    /// A WebSocket (the realtime channel of a server's screens, plan 13, U09): the browser's socket is accepted, one to the server is opened with the same token in its header, and what arrives on either is sent on
    /// to the other until one closes. The server's certificate is checked against the pin, exactly as for a call.
    /// </summary>
    private async Task RelayWebSocketAsync(HttpContext context, RegisteredInstance instance, string path, string token)
    {
        using var upstream = new System.Net.WebSockets.ClientWebSocket();
        upstream.Options.SetRequestHeader(ManagementAuthentication.TokenHeader, token);
        upstream.Options.SetRequestHeader(ManagementApi.VersionHeader, ManagementApi.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
        upstream.Options.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            certificate is not null && string.Equals(PairingProof.CertificateFingerprint(certificate), instance.CertificateFingerprint, StringComparison.Ordinal);
        foreach (var protocol in context.WebSockets.WebSocketRequestedProtocols) upstream.Options.AddSubProtocol(protocol);

        var address = new UriBuilder(new Uri(instance.Url, path + context.Request.QueryString)) { Scheme = "wss" }.Uri;
        try
        {
            await upstream.ConnectAsync(address, context.RequestAborted);
        }
        catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException or HttpRequestException or TaskCanceledException && !context.RequestAborted.IsCancellationRequested)
        {
            log.LogWarning("{Server} did not accept a relayed WebSocket on {Path}: {Reason}", instance.Name, path, ex.GetType().Name);
            await Refuse(context, StatusCodes.Status502BadGateway, $"{instance.Name} did not accept the connection.");
            return;
        }

        using var downstream = await context.WebSockets.AcceptWebSocketAsync(upstream.SubProtocol);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var toServer = Pump(downstream, upstream, stop);
        var toBrowser = Pump(upstream, downstream, stop);
        await Task.WhenAny(toServer, toBrowser);
        stop.Cancel();
        try { await Task.WhenAll(toServer, toBrowser); } catch (Exception ex) when (ex is OperationCanceledException or System.Net.WebSockets.WebSocketException) { /* one side closed or went away: the other is closed below */ }
        foreach (var socket in new[] { downstream, (System.Net.WebSockets.WebSocket)upstream })
            if (socket.State is System.Net.WebSockets.WebSocketState.Open or System.Net.WebSockets.WebSocketState.CloseReceived)
                try { await socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); } catch (System.Net.WebSockets.WebSocketException) { }
    }

    private static async Task Pump(System.Net.WebSockets.WebSocket from, System.Net.WebSockets.WebSocket to, CancellationTokenSource stop)
    {
        var buffer = new byte[16 * 1024];
        while (!stop.IsCancellationRequested)
        {
            var received = await from.ReceiveAsync(buffer, stop.Token);
            if (received.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) return;
            await to.SendAsync(buffer.AsMemory(0, received.Count), received.MessageType, received.EndOfMessage, stop.Token);
        }
    }

    private HttpClient ClientFor(RegisteredInstance instance, bool upload = false) =>
        _clients.GetOrAdd(upload ? "upload:" + instance.CertificateFingerprint : instance.CertificateFingerprint,
            _ => new HttpClient(PairingClient.Pinned(instance.CertificateFingerprint)) { Timeout = upload ? TimeSpan.FromMinutes(30) : TimeSpan.FromMinutes(2) });

    private static async Task Refuse(HttpContext context, int status, string error)
    {
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { error });
    }
}

public static class ConsoleRelayEndpoints
{
    /// <summary>Maps <c>/relay/{server}/{**path}</c> for every method. Any signed-in person may use it: what they may do is the server's decision, from the roles in the token.</summary>
    public static IEndpointConventionBuilder MapRelay(this IEndpointRouteBuilder endpoints) =>
        endpoints.Map($"{ConsoleRelay.Prefix}/{{server}}/{{**path}}", (HttpContext context, string server, string? path, ConsoleRelay relay) => relay.RelayAsync(context, server, path ?? ""))
            .RequireAuthorization(Policies.Viewer);
}
