using System.Globalization;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace DbDataKit.Hosting.Startup;

/// <summary>
/// The ports step of a server whose listeners are bound when its host starts (ASP.NET Core's Kestrel): a listener that cannot be bound becomes the
/// step's own failure, naming the port and Windows' excluded port ranges, whatever exception the host threw for it.
/// Design: docs/install.md#one-startup-sequence
/// </summary>
public static partial class StartupPorts
{
    /// <summary>
    /// Runs <paramref name="start"/>, which binds the listeners. A port another program holds, or one Windows refuses (an excluded range), is thrown again
    /// as <see cref="StartupStepException.Ports"/>: the port is read from the host's message ("Failed to bind to address https://127.0.0.1:7841"), or
    /// is the first of <paramref name="ports"/> when the message names none. Any other exception goes through as it is.
    /// </summary>
    public static async Task BindAsync(this StartupContext context, Func<CancellationToken, Task> start, IEnumerable<int> ports, CancellationToken cancellationToken = default)
    {
        try
        {
            await start(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (RefusedPort(error, ports) is { } port)
        {
            throw StartupStepException.Ports(port, null, context.Options.ExcludedPorts(), error);
        }
    }

    /// <summary>The port a listener could not bind, or null when <paramref name="error"/> is not a refused bind.</summary>
    internal static int? RefusedPort(Exception error, IEnumerable<int> ports)
    {
        var chain = StartupSequence.Chain(error).ToList();
        if (!chain.OfType<SocketException>().Any(s => s.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)) return null;
        foreach (var e in chain)
        {
            var match = BindAddress().Match(e.Message);
            if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535)
                return port;
        }
        return ports.Where(p => p > 0).Select(p => (int?)p).FirstOrDefault();
    }

    [GeneratedRegex(@"address\s+\S*?:(\d{1,5})(?=[:/\s.,;]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BindAddress();
}
