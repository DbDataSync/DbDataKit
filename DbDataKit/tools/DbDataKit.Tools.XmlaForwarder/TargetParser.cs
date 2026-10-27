namespace DbDataKit.Tools.XmlaForwarder;

/// <summary>Reads a <c>host:port</c> target (a bracketed IPv6 address too).</summary>
public static class TargetParser
{
    public static bool TryParse(string text, out string host, out int port)
    {
        (host, port) = ("", 0);
        var colon = text.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(text.AsSpan(colon + 1), out port) || port is < 1 or > 65535) return false;
        host = text[..colon].Trim('[', ']');
        return host.Length > 0;
    }
}
