using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DbDataKit.Outbound;

/// <summary>A destination that is not on the service's outbound list; nothing was connected.</summary>
public sealed class OutboundRefusedException(string service, string operation, OutboundDestination destination)
    : Exception($"The {service} service may not connect to {destination} for `{operation}`: it is not on the outbound list (the `{OutboundSettings.Key}:` setting of the service's settings file).")
{
    public string Service { get; } = service;
    public string Operation { get; } = operation;
    public OutboundDestination Destination { get; } = destination;
}

/// <summary>What the audit records for a refusal: when, which service, the operation and the destination. Never a path, a query, a login or a secret.</summary>
public sealed record OutboundRefusal(DateTimeOffset At, string Service, string Operation, string Host, int Port);

/// <summary>Where a service writes its refusals to its audit; the host adapts it to the service's audit log.</summary>
public interface IOutboundAudit
{
    void Refused(OutboundRefusal refusal);
}

/// <summary>
/// The one door of a service's own code to the network: <see cref="Check(string, OutboundDestination)"/> before any connection, or a handler or connect helper that checks for it. A destination that is not on the
/// <see cref="OutboundList"/> is refused before anything connects, logged as a warning and written to the audit with the operation and the destination. The gate sees the code of the service itself: a program the
/// service starts (git, <c>dotnet restore</c>) opens its own connections, so the destinations of its operation are checked by the caller with <see cref="Check(string, OutboundDestination)"/> before it starts.
/// </summary>
public sealed class OutboundGate(OutboundList list, string service, IOutboundAudit? audit = null, ILogger? logger = null, TimeProvider? time = null)
{
    public OutboundList List { get; } = list;
    public string Service { get; } = service;

    /// <summary>Whether the destination is on the list; nothing is logged or audited.</summary>
    public bool IsAllowed(OutboundDestination destination) => List.Allows(destination);

    /// <summary>Throws <see cref="OutboundRefusedException"/>, after logging and auditing it, when the destination is not on the list.</summary>
    public void Check(string operation, OutboundDestination destination)
    {
        if (List.Allows(destination)) return;
        var refusal = new OutboundRefusal((time ?? TimeProvider.System).GetUtcNow(), Service, operation, destination.Host, destination.Port);
        logger?.LogWarning("Outbound refused: the {Service} service may not connect to {Destination} for {Operation}.", Service, destination.ToString(), operation);
        try { audit?.Refused(refusal); }
        catch (Exception ex) { logger?.LogError(ex, "The outbound refusal of {Destination} for {Operation} could not be written to the audit.", destination.ToString(), operation); }
        throw new OutboundRefusedException(Service, operation, destination);
    }

    public void Check(string operation, string host, int port) => Check(operation, new OutboundDestination(OutboundDestination.NormalizeHost(host), port));

    /// <summary>Checks an absolute address (its host and port, never its path or query); an address with no usable port is refused.</summary>
    public void Check(string operation, Uri address)
    {
        var destination = OutboundDestination.FromUri(address) ?? new OutboundDestination(address.IsAbsoluteUri ? OutboundDestination.NormalizeHost(address.Host) : "", 0);
        Check(operation, destination);
    }

    /// <summary>
    /// Checks the remote of a git operation. A folder on this machine passes (it opens no connection).
    /// </summary>
    public void CheckGitRemote(string operation, string remote)
    {
        if (OutboundDestination.FromGitRemote(remote) is { } destination) Check(operation, destination);
    }

    /// <summary>
    /// A handler for an <see cref="HttpClient"/> that checks every connection it opens, redirects included, before it connects. The destination is the one the handler connects to: behind a proxy that is the proxy.
    /// </summary>
    public SocketsHttpHandler CreateHttpHandler(string operation, SocketsHttpHandler? inner = null)
    {
        var handler = inner ?? new SocketsHttpHandler();
        handler.ConnectCallback = (context, cancellation) => ConnectAsync(operation, context.DnsEndPoint.Host, context.DnsEndPoint.Port, cancellation);
        return handler;
    }

    /// <summary>Checks, then opens a TCP connection and returns its stream; for a client that takes a stream, or a caller that connects by hand.</summary>
    public async ValueTask<Stream> ConnectAsync(string operation, string host, int port, CancellationToken cancellation = default)
    {
        Check(operation, host, port);
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new DnsEndPoint(host, port), cancellation).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

public static class OutboundGateExtensions
{
    /// <summary>Registers the service's gate. The audit sink is the <see cref="IOutboundAudit"/> the host registered, when it did.</summary>
    public static IServiceCollection AddOutboundGate(this IServiceCollection services, OutboundList list, string service) =>
        services.AddSingleton(sp => new OutboundGate(list, service, sp.GetService<IOutboundAudit>(), sp.GetService<ILoggerFactory>()?.CreateLogger("DbDataKit.Outbound"), sp.GetService<TimeProvider>()));

    /// <summary>The named client's connections go through the service's <see cref="OutboundGate"/> under <paramref name="operation"/> (for example <c>nuget.restore</c>).</summary>
    public static IHttpClientBuilder AddOutboundGate(this IHttpClientBuilder builder, string operation) =>
        builder.ConfigurePrimaryHttpMessageHandler(sp => sp.GetRequiredService<OutboundGate>().CreateHttpHandler(operation));
}
