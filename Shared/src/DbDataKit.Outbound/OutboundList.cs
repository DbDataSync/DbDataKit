using System.Globalization;
using System.Net;

namespace DbDataKit.Outbound;

/// <summary>The <c>outbound:</c> setting is wrong; the message names the file, the line and the entry.</summary>
public sealed class OutboundSettingsException(string message) : Exception(message);

/// <summary>A place a service connects to: a host (a name or an address, lower case, no trailing dot) and a TCP port.</summary>
public readonly record struct OutboundDestination(string Host, int Port)
{
    /// <summary>The ports a scheme stands for when an entry or a remote names none. <c>tcp</c> has none: it needs a port.</summary>
    public static readonly IReadOnlyDictionary<string, int> DefaultPorts = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["http"] = 80, ["https"] = 443, ["ssh"] = 22, ["git"] = 9418,
    };

    public override string ToString() => Host.Contains(':') ? $"[{Host}]:{Port}" : $"{Host}:{Port}";

    internal static string NormalizeHost(string host)
    {
        host = host.Trim().TrimEnd('.');
        if (host.Length > 1 && host[0] == '[' && host[^1] == ']') host = host[1..^1];
        return host.ToLowerInvariant();
    }

    /// <summary>The destination of an absolute address; a scheme without a default port and no port in the address gives null.</summary>
    public static OutboundDestination? FromUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || string.IsNullOrEmpty(uri.Host)) return null;
        var port = uri.IsDefaultPort || uri.Port < 0
            ? DefaultPorts.TryGetValue(uri.Scheme, out var d) ? d : uri.Port
            : uri.Port;
        return port is > 0 and <= 65535 ? new OutboundDestination(NormalizeHost(uri.Host), port) : null;
    }

    /// <summary>
    /// The destination of a git remote: an address (<c>https://host/org/repo.git</c>, <c>ssh://git@host/org/repo.git</c>), or the short form <c>git@host:org/repo.git</c> (ssh, port 22).
    /// A folder on this machine (<c>/srv/repo.git</c>, <c>C:\repo</c>, <c>file://</c>) opens no connection and gives null.
    /// </summary>
    public static OutboundDestination? FromGitRemote(string remote)
    {
        if (string.IsNullOrWhiteSpace(remote)) return null;
        remote = remote.Trim();
        if (remote.Contains("://", StringComparison.Ordinal))
            return Uri.TryCreate(remote, UriKind.Absolute, out var uri) && uri.Scheme != Uri.UriSchemeFile ? FromUri(uri) : null;
        if (remote.StartsWith('/') || remote.StartsWith('.') || remote.StartsWith('\\') || (remote.Length > 1 && remote[1] == ':' && char.IsLetter(remote[0]))) return null;
        var colon = remote.IndexOf(':');
        if (colon <= 0) return null;                                  // a relative folder
        var host = remote[..colon];
        var at = host.LastIndexOf('@');
        if (at >= 0) host = host[(at + 1)..];
        return host.Length == 0 || host.Contains('/') ? null : new OutboundDestination(NormalizeHost(host), 22);
    }
}

/// <summary>
/// The destinations a service may connect to. An entry is an address without a path: <c>scheme://host[:port]</c> with the scheme <c>http</c> (80), <c>https</c> (443), <c>ssh</c> (22),
/// <c>git</c> (9418) or <c>tcp</c> (a port is required). The host is a name, an IP address, or <c>*.suffix</c>, which stands for every name under the suffix and not the suffix itself.
/// A destination is allowed when its host and port match an entry; the scheme only gives the port its default.
/// <see cref="Unrestricted"/> (no <c>outbound:</c> setting) allows everything; a list with no entry allows nothing.
/// </summary>
public sealed class OutboundList
{
    private readonly List<(string Text, string Host, bool Wildcard, int Port)> entries;

    private OutboundList(List<(string, string, bool, int)> entries, bool unrestricted) { this.entries = entries; Restricted = !unrestricted; }

    /// <summary>No setting: the service connects anywhere. Nothing is refused and nothing is audited.</summary>
    public static OutboundList Unrestricted { get; } = new([], true);

    /// <summary>A list with no entry: the service connects nowhere.</summary>
    public static OutboundList Nothing { get; } = new([], false);

    /// <summary>False for <see cref="Unrestricted"/>.</summary>
    public bool Restricted { get; }

    /// <summary>The entries as written.</summary>
    public IReadOnlyList<string> Entries => [.. entries.Select(e => e.Text)];

    /// <summary>Reads the entries; one that is not valid fails with <see cref="OutboundSettingsException"/> naming <paramref name="source"/> and the entry.</summary>
    public static OutboundList Parse(IEnumerable<string> items, string source)
    {
        var list = new List<(string, string, bool, int)>();
        foreach (var item in items) list.Add(ParseEntry(item, source));
        return new OutboundList(list, false);
    }

    private static (string, string, bool, int) ParseEntry(string text, string source)
    {
        string Bad(string why) => $"{source}: outbound entry `{text}` {why} Write scheme://host[:port], like https://api.nuget.org, ssh://github.com or tcp://db.internal:5432; a host may be *.example.com.";
        var trimmed = text.Trim();
        var sep = trimmed.IndexOf("://", StringComparison.Ordinal);
        if (sep <= 0) throw new OutboundSettingsException(Bad("has no scheme."));
        var scheme = trimmed[..sep].ToLowerInvariant();
        if (scheme != "tcp" && !OutboundDestination.DefaultPorts.ContainsKey(scheme)) throw new OutboundSettingsException(Bad($"has the scheme `{scheme}`, which is none of http, https, ssh, git, tcp."));
        var rest = trimmed[(sep + 3)..];
        if (rest.Length == 0 || rest.IndexOfAny(['/', '?', '#', '@', ' ']) >= 0) throw new OutboundSettingsException(Bad("holds more than a host and a port (no path, query, login or space)."));
        string host;
        string? portText = null;
        if (rest[0] == '[')
        {
            var close = rest.IndexOf(']');
            if (close < 0) throw new OutboundSettingsException(Bad("has an IPv6 address with no closing bracket."));
            host = rest[1..close];
            if (close + 1 < rest.Length)
                portText = rest[close + 1] == ':' ? rest[(close + 2)..] : throw new OutboundSettingsException(Bad("has text after the address."));
            if (!IPAddress.TryParse(host, out _)) throw new OutboundSettingsException(Bad("is not an IPv6 address."));
        }
        else
        {
            var colon = rest.LastIndexOf(':');
            host = colon < 0 ? rest : rest[..colon];
            if (colon >= 0) portText = rest[(colon + 1)..];
        }

        host = OutboundDestination.NormalizeHost(host);
        var wildcard = host.StartsWith("*.", StringComparison.Ordinal);
        var name = wildcard ? host[2..] : host;
        if (name.Length == 0 || (wildcard && !name.Contains('.')) || name.Contains('*') || name.Contains("..", StringComparison.Ordinal))
            throw new OutboundSettingsException(Bad(wildcard ? "has a wildcard that is not *.name.suffix." : "has no host."));
        if (wildcard && IPAddress.TryParse(name, out _)) throw new OutboundSettingsException(Bad("has a wildcard on an address."));
        int port;
        if (portText is null)
        {
            if (!OutboundDestination.DefaultPorts.TryGetValue(scheme, out port)) throw new OutboundSettingsException(Bad("needs a port: tcp has no default."));
        }
        else if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535)
            throw new OutboundSettingsException(Bad("has a port that is not a number from 1 to 65535."));
        return (text.Trim(), host, wildcard, port);
    }

    /// <summary>Whether the destination matches an entry. Always true for <see cref="Unrestricted"/>.</summary>
    public bool Allows(OutboundDestination destination)
    {
        if (!Restricted) return true;
        var host = OutboundDestination.NormalizeHost(destination.Host);
        foreach (var (_, entryHost, wildcard, port) in entries)
        {
            if (port != destination.Port) continue;
            if (!wildcard ? host == entryHost : host.Length > entryHost.Length - 1 && host.EndsWith(entryHost[1..], StringComparison.Ordinal)) return true;
        }

        return false;
    }
}
