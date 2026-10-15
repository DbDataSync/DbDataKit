using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace DbData.Hosting;

/// <summary>
/// Forwarded-headers guidance as data. Rule: never trust <c>X-Forwarded-*</c> from arbitrary peers (it lets any
/// client spoof its address and scheme, defeating network-trust checks such as Sync 9.6). Trust only listed proxies
/// and do not enable it at all unless a proxy is configured.
/// </summary>
public sealed class ForwardedHeadersPolicy
{
    /// <summary>Exact proxy addresses to trust (usually loopback or one reverse proxy).</summary>
    public IList<IPAddress> TrustedProxies { get; } = [];

    /// <summary>Trusted proxy networks as (prefix, prefix length).</summary>
    public IList<(IPAddress Prefix, int PrefixLength)> TrustedNetworks { get; } = [];

    /// <summary>Number of proxy hops honoured. Default 1.</summary>
    public int ForwardLimit { get; set; } = 1;

    public bool Enabled => TrustedProxies.Count > 0 || TrustedNetworks.Count > 0;

    public const string Guidance =
        "Forwarded headers are OFF unless TrustedProxies/TrustedNetworks are configured. When on, only those peers are " +
        "believed, ForwardLimit bounds hops, and RemoteIpAddress/Scheme then reflect the original client. Never add " +
        "0.0.0.0/0 or clear the trusted lists; use the proxy's own authentication for anything security relevant.";

    /// <summary>Parse "10.0.0.5" or "10.0.0.0/24" entries. A /0 network is refused.</summary>
    public static ForwardedHeadersPolicy Parse(IEnumerable<string> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var policy = new ForwardedHeadersPolicy();
        foreach (var raw in entries)
        {
            var entry = raw.Trim();
            if (entry.Length == 0) continue;
            var slash = entry.IndexOf('/');
            if (slash < 0)
            {
                policy.TrustedProxies.Add(IPAddress.Parse(entry));
                continue;
            }

            var prefix = IPAddress.Parse(entry[..slash]);
            var len = int.Parse(entry[(slash + 1)..], CultureInfo.InvariantCulture);
            var max = prefix.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
            if (len < 0 || len > max) throw new FormatException($"Invalid prefix length in '{entry}'.");
            if (len == 0) throw new FormatException("A /0 network would trust every peer and is refused.");
            policy.TrustedNetworks.Add((prefix, len));
        }

        return policy;
    }
}
