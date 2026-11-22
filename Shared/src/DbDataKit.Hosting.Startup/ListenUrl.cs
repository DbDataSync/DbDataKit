using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace DbDataKit.Hosting.Startup;

/// <summary>
/// One address a server listens on, written the way Kestrel writes it: <c>https://0.0.0.0:7820</c>, <c>https://127.0.0.1:9443</c>, <c>https://[::1]:9443</c>,
/// <c>https://localhost:7800</c> or <c>http://*:8080</c>. The scheme and the port are always written; there is no path. The host is an IP address,
/// <c>localhost</c> (the loopback address), <c>*</c> or <c>+</c> (every address), or a name of this machine (every address, as the servers always read it).
/// Design: docs/install.md#ports
/// </summary>
public sealed record ListenUrl
{
    private ListenUrl(string scheme, string host, IPAddress address, int port)
    {
        Scheme = scheme;
        Host = host;
        Address = address;
        Port = port;
    }

    /// <summary><c>https</c> or <c>http</c>, in lower case.</summary>
    public string Scheme { get; }

    /// <summary>The host as written, without brackets: <c>0.0.0.0</c>, <c>::1</c>, <c>localhost</c>, <c>*</c>.</summary>
    public string Host { get; }

    /// <summary>The address a listener binds for <see cref="Host"/>.</summary>
    public IPAddress Address { get; }

    /// <summary>The port, 0 to 65535; 0 lets the operating system choose (a test's).</summary>
    public int Port { get; }

    public bool IsHttps => Scheme == "https";

    /// <summary>True when the listener answers on every address of the machine.</summary>
    public bool IsEveryAddress => Address.Equals(IPAddress.Any) || Address.Equals(IPAddress.IPv6Any);

    /// <summary>The text as written in a settings file: <c>https://0.0.0.0:7820</c>.</summary>
    public override string ToString() => $"{Scheme}://{(Host.Contains(':') ? $"[{Host}]" : Host)}:{Port.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The same address as a <see cref="Uri"/>; <c>*</c> and <c>+</c> become <c>0.0.0.0</c>, which a Uri can hold.</summary>
    public Uri ToUri() => new(Host is "*" or "+" ? $"{Scheme}://0.0.0.0:{Port}" : ToString());

    /// <summary>An address of the machine itself that a client can use: <c>localhost</c> in place of "every address".</summary>
    public Uri ToClientUri() => new UriBuilder(Scheme, IsEveryAddress ? "localhost" : Host, Port).Uri;

    public static ListenUrl From(Uri uri) => Parse(uri.GetLeftPart(UriPartial.Authority));

    /// <exception cref="FormatException">The text is not a listen address; the message says why.</exception>
    public static ListenUrl Parse(string text) =>
        TryParse(text, out var url, out var problem) ? url! : throw new FormatException(problem);

    /// <summary>Reads one listen address; <paramref name="problem"/> says what is wrong with <paramref name="text"/> when it is not one.</summary>
    public static bool TryParse(string? text, out ListenUrl? url, out string problem)
    {
        url = null;
        var value = (text ?? "").Trim().Trim('"', '\'');
        var example = "such as https://0.0.0.0:7820 or https://127.0.0.1:9443";
        if (value.Length == 0)
        {
            problem = $"The address is empty; write the scheme, the address and the port, {example}.";
            return false;
        }
        var schemeEnd = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
        {
            problem = $"'{value}' is not an address with a scheme and a port ({example}); a bare port or host name is not one.";
            return false;
        }
        var scheme = value[..schemeEnd].ToLowerInvariant();
        if (scheme is not ("https" or "http"))
        {
            problem = $"'{value}' starts with '{scheme}'; a listener is https or http.";
            return false;
        }
        var rest = value[(schemeEnd + 3)..].TrimEnd('/');
        if (rest.Contains('/') || rest.Contains('?') || rest.Contains('#') || rest.Contains('@'))
        {
            problem = $"'{value}' has more than a host and a port; a listen address has no path or query ({example}).";
            return false;
        }
        string host, portText;
        if (rest.StartsWith('['))
        {
            var close = rest.IndexOf(']');
            if (close < 0 || close + 1 >= rest.Length || rest[close + 1] != ':')
            {
                problem = $"'{value}' needs a port after the bracketed address ({example}).";
                return false;
            }
            host = rest[1..close];
            portText = rest[(close + 2)..];
        }
        else
        {
            var colon = rest.LastIndexOf(':');
            if (colon < 0)
            {
                problem = $"'{value}' has no port; write it ({example}).";
                return false;
            }
            host = rest[..colon];
            portText = rest[(colon + 1)..];
        }
        if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 0 or > 65535)
        {
            problem = $"'{value}' has the port '{portText}'; a port is a whole number from 0 to 65535.";
            return false;
        }
        if (host.Length == 0)
        {
            problem = $"'{value}' has no address; write one, or * for every address ({example}).";
            return false;
        }
        IPAddress address;
        if (host is "*" or "+") address = IPAddress.Any;
        else if (IPAddress.TryParse(host, out var ip) && (ip.AddressFamily is AddressFamily.InterNetwork || ip.AddressFamily is AddressFamily.InterNetworkV6)) address = ip;
        else if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) { address = IPAddress.Loopback; host = "localhost"; }
        else if (Uri.CheckHostName(host) == UriHostNameType.Dns) address = IPAddress.Any;
        else
        {
            problem = $"'{host}' is not an address or a host name.";
            return false;
        }
        url = new ListenUrl(scheme, host, address, port);
        problem = "";
        return true;
    }

    /// <summary>
    /// The addresses of a <c>--listen</c> flag: one, or several separated by a semicolon or a comma.
    /// </summary>
    /// <exception cref="FormatException">One of them is not a listen address.</exception>
    public static IReadOnlyList<ListenUrl> ParseFlag(string text) =>
        [.. text.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Parse)];
}
