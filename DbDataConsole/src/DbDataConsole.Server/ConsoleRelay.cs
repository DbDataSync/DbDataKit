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
/// Only <c>api/...</c> and <c>hubs/...</c> are relayed, only to a registered server, and the path is not interpreted: nothing the browser sends can name another host.
/// </para>
/// </summary>
public sealed class ConsoleRelay(ConsoleInstances instances, ConsoleKeyStore keys, CurrentUser current, ILogger<ConsoleRelay> log)
{
    public const string Prefix = "/relay";

    private static readonly string[] ForwardedRequestHeaders = ["Accept", "Accept-Language", "If-Match", "If-None-Match", "Content-Type"];
    private static readonly string[] ForwardedResponseHeaders = ["ETag", "Content-Disposition", "Cache-Control", "Last-Modified", "Location"];

    private readonly ConcurrentDictionary<string, HttpClient> _clients = new(StringComparer.Ordinal);

    /// <summary>What a person may do on a server managed from here, until the identity home (U13) assigns roles per server: an administrator everything but publishing, a viewer reading.</summary>
    public static ServerRole RolesOf(UserRole role) => role == UserRole.Admin
        ? ServerRole.Admin | ServerRole.Operator | ServerRole.Author | ServerRole.Approver
        : ServerRole.Viewer;

    public async Task RelayAsync(HttpContext context, string server, string path)
    {
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

        // The key this server knows: the current one, or the one a rotation is moving to if the server already has that.
        var identity = (instance.KeyFingerprint is { } fingerprint ? await keys.ForKeyAsync(fingerprint, context.RequestAborted) : null) ?? await keys.GetOrCreateAsync(context.RequestAborted);
        // What this person may do there is the lower of their role here and what the server lets this console grant; a server paired before the ceiling was recorded is asked for reading only.
        var roles = ServerRoles.Lower(RolesOf(role), instance.Ceiling == ServerRole.None ? ServerRole.Viewer : instance.Ceiling);
        var token = identity.Issuer().Issue(new ConsoleTokenRequest(instance.InstanceId, userId, current.Name, roles));

        if (context.WebSockets.IsWebSocketRequest)
        {
            await RelayWebSocketAsync(context, instance, path, token);
            return;
        }

        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), new Uri(instance.Url, path + context.Request.QueryString));
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
            using var response = await ClientFor(instance).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
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

    private HttpClient ClientFor(RegisteredInstance instance) =>
        _clients.GetOrAdd(instance.CertificateFingerprint, fingerprint => new HttpClient(PairingClient.Pinned(fingerprint)) { Timeout = TimeSpan.FromMinutes(2) });

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
