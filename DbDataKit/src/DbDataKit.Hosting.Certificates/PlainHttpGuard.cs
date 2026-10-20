using System.Net;

namespace DbDataKit.Hosting.Certificates;

/// <summary>
/// Refuse plain HTTP (TlsMode.None) on any non-loopback bind unless explicitly overridden (Proxy S-5, Sync 9.6).
/// Wildcards (<c>+</c>, <c>*</c>, <c>0.0.0.0</c>, <c>[::]</c>) count as non-loopback.
/// </summary>
public static class PlainHttpGuard
{
    public static IReadOnlyList<string> FindViolations(IEnumerable<string> urls)
    {
        var bad = new List<string>();
        foreach (var raw in urls.SelectMany(u => u.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            if (!raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) continue;
            if (!IsLoopbackHost(HostOf(raw))) bad.Add(raw);
        }

        return bad;
    }

    /// <summary>Throws <see cref="InvalidOperationException"/> when plain HTTP is bound off-loopback and not overridden.</summary>
    public static void Enforce(IEnumerable<string> urls, bool allowInsecureOverride)
    {
        if (allowInsecureOverride) return;
        var bad = FindViolations(urls);
        if (bad.Count > 0)
        {
            throw new InvalidOperationException(
                $"Plain HTTP on a non-loopback address is refused: {string.Join(", ", bad)}. Use HTTPS or bind to loopback behind a TLS-terminating proxy, or set the explicit insecure override.");
        }
    }

    /// <summary>
    /// The request-time half of the guard, for a server that may be bound to plain HTTP behind a TLS-terminating
    /// proxy: a request is acceptable when it arrived over TLS, or from a loopback client (local tools, a proxy on the
    /// same host). Call it after forwarded headers from trusted proxies have been applied, so <paramref name="isHttps"/>
    /// reflects <c>X-Forwarded-Proto</c> and <paramref name="clientAddress"/> the forwarded client. A null address (a unix
    /// socket, an in-memory test server) counts as local.
    /// </summary>
    public static bool RequestAllowed(bool isHttps, IPAddress? clientAddress)
    {
        if (isHttps || clientAddress is null) return true;
        return IPAddress.IsLoopback(clientAddress.IsIPv4MappedToIPv6 ? clientAddress.MapToIPv4() : clientAddress);
    }

    public static bool IsLoopbackHost(string host)
    {
        if (host.StartsWith("unix:", StringComparison.OrdinalIgnoreCase)) return true;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        var h = host.Trim('[', ']');
        return IPAddress.TryParse(h, out var ip) && IPAddress.IsLoopback(ip);
    }

    private static string HostOf(string url)
    {
        var rest = url["http://".Length..];
        if (rest.StartsWith("unix:", StringComparison.OrdinalIgnoreCase)) return "unix:";
        var slash = rest.IndexOf('/');
        if (slash >= 0) rest = rest[..slash];
        if (rest.StartsWith('['))
        {
            var close = rest.IndexOf(']');
            return close > 0 ? rest[..(close + 1)] : rest;
        }

        var colon = rest.LastIndexOf(':');
        return colon >= 0 ? rest[..colon] : rest;
    }
}
