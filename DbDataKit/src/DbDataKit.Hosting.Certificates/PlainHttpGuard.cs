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
