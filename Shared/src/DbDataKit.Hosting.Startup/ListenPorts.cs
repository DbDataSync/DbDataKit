using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace DbDataKit.Hosting.Startup;

/// <summary>Where the address a server listens on came from.</summary>
public enum ListenSource
{
    /// <summary><c>--listen</c> on the command line: used as given, never written to the settings file.</summary>
    Flag,

    /// <summary>The <c>listen:</c> line of the server's settings file, pinned by an earlier start or written by a person: used as given.</summary>
    Settings,

    /// <summary>A free port of the service's sub-block, chosen at this start: written to the settings file once the server listens on it.</summary>
    Chosen,
}

/// <summary>
/// The address a server listens on, as <see cref="ListenPorts.Resolve"/> found it. When no port of the sub-block could be bound, <see cref="Address"/>
/// holds the sub-block's first port, so the host can still be built, and the ports step fails with the reason
/// (<see cref="StartupPorts.BindAsync(StartupContext, ListenChoice, Func{CancellationToken, Task}, IEnumerable{int}?, CancellationToken)"/>).
/// </summary>
public sealed class ListenChoice
{
    internal ListenChoice(Uri address, ListenSource source, string settingsFile, StartupStepException? failure = null)
    {
        Address = address;
        Source = source;
        SettingsFile = settingsFile;
        Failure = failure;
    }

    /// <summary>The address the server listens on: <c>https://127.0.0.1:7841/</c>.</summary>
    public Uri Address { get; }

    public ListenSource Source { get; }

    /// <summary>The server's settings file, which holds the <c>listen:</c> line.</summary>
    public string SettingsFile { get; }

    /// <summary>Why no port of the sub-block could be chosen; null when the address is usable.</summary>
    public StartupStepException? Failure { get; }

    /// <summary>The port of <see cref="Address"/>.</summary>
    public int Port => Address.Port;
}

/// <summary>
/// The port a server listens on when nobody configured one: the first port of the service's sub-block that can be bound, written at the first start
/// to the <c>listen:</c> line of the server's settings file and used as it is from then on. A configured address, <c>--listen</c> or that line, is
/// never moved: a port another program holds then fails the ports step, naming it.
/// Design: docs/install.md#ports
/// </summary>
public static partial class ListenPorts
{
    /// <summary>The top-level key of a server's settings file that holds the address it listens on.</summary>
    public const string SettingsKey = "listen";

    /// <summary>
    /// The address the server listens on:
    /// <list type="bullet">
    /// <item><paramref name="flag"/> (<c>--listen</c>) when given;</item>
    /// <item>otherwise the <c>listen:</c> line of <paramref name="settingsFile"/> when it has one;</item>
    /// <item>otherwise <paramref name="defaultListen"/>'s host with the first port of <paramref name="service"/>'s sub-block that is outside
    /// <paramref name="excluded"/> and can be bound.</item>
    /// </list>
    /// <paramref name="canBind"/> says whether a port is free on an address; by default the port is bound and released (<see cref="CanBind"/>).
    /// </summary>
    /// <exception cref="InvalidOperationException">The settings file's <c>listen:</c> line is not an https address; the message names the file.</exception>
    public static ListenChoice Resolve(SuiteService service, Uri? flag, string settingsFile, Uri defaultListen, IReadOnlyList<PortRange> excluded,
        Func<IPAddress, int, bool>? canBind = null)
    {
        if (flag is not null) return new ListenChoice(flag, ListenSource.Flag, settingsFile);
        if (ReadPinned(settingsFile) is { } pinned) return new ListenChoice(pinned, ListenSource.Settings, settingsFile);
        var block = service.Ports;
        var port = Choose(block, AddressOf(defaultListen), excluded, canBind);
        return port is { } free
            ? new ListenChoice(WithPort(defaultListen, free), ListenSource.Chosen, settingsFile)
            : new ListenChoice(WithPort(defaultListen, block.Start), ListenSource.Chosen, settingsFile, StartupStepException.NoFreePort(block, excluded));
    }

    /// <summary>
    /// The first port of <paramref name="block"/> outside <paramref name="excluded"/> that can be bound on <paramref name="address"/>, or null when there
    /// is none. A port is tried by binding it: one another program holds and one Windows refuses (an excluded range that moved since it was read, which
    /// refuses with WSAEACCES) are both skipped.
    /// </summary>
    public static int? Choose(PortRange block, IPAddress address, IReadOnlyList<PortRange> excluded, Func<IPAddress, int, bool>? canBind = null)
    {
        canBind ??= CanBind;
        for (var port = block.Start; port <= block.End; port++)
        {
            if (excluded.Any(r => r.Contains(port))) continue;
            if (canBind(address, port)) return port;
        }
        return null;
    }

    /// <summary>True when a listener can be bound to <paramref name="port"/> on <paramref name="address"/>; the socket is closed again at once.</summary>
    public static bool CanBind(IPAddress address, int port)
    {
        try
        {
            using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            // on Windows a port another socket holds with SO_REUSEADDR could otherwise be bound again; on Linux this option would ask for SO_REUSEPORT
            if (OperatingSystem.IsWindows()) socket.ExclusiveAddressUse = true;
            socket.Bind(new IPEndPoint(address, port));
            socket.Listen();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>The address a host binds for <paramref name="listen"/>: the loopback for <c>localhost</c>, every address for another name, the address itself otherwise.</summary>
    public static IPAddress AddressOf(Uri listen) => listen.HostNameType switch
    {
        UriHostNameType.IPv4 or UriHostNameType.IPv6 => IPAddress.Parse(listen.Host.Trim('[', ']')),
        _ when listen.Host == "localhost" => IPAddress.Loopback,
        _ => IPAddress.Any,
    };

    /// <summary>The <c>listen:</c> line of <paramref name="settingsFile"/>, or null when the file or the line is not there.</summary>
    /// <exception cref="InvalidOperationException">The line is not an https address; the message names the file.</exception>
    public static Uri? ReadPinned(string settingsFile)
    {
        string text;
        try
        {
            using var stream = new FileStream(settingsFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            text = reader.ReadToEnd();
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        var line = ListenLine().Match(text);
        if (!line.Success) return null;
        var value = line.Groups["value"].Value.Trim().Trim('"', '\'');
        if (!Uri.TryCreate(value, UriKind.Absolute, out var listen) || listen.Scheme != Uri.UriSchemeHttps || listen.IsDefaultPort && !value.Contains(":443", StringComparison.Ordinal))
            throw new InvalidOperationException($"{settingsFile}: {SettingsKey} is '{value}'; write the https address the server listens on, with its port, such as https://127.0.0.1:7840.");
        return listen;
    }

    /// <summary>
    /// Writes <paramref name="listen"/> as the <c>listen:</c> line of <paramref name="settingsFile"/>: the line is replaced when there is one, and added
    /// at the end, under a comment, when there is none. The rest of the file, comments and all, is kept. The file is made when it is not there.
    /// </summary>
    public static void Pin(string settingsFile, Uri listen)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(settingsFile))!;
        Directory.CreateDirectory(directory);
        using var gate = TakeLock(settingsFile + ".lock");
        var text = File.Exists(settingsFile) ? File.ReadAllText(settingsFile) : "";
        var value = listen.GetLeftPart(UriPartial.Authority);
        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var match = ListenLine().Match(text);
        var changed = match.Success
            ? text[..match.Index] + $"{SettingsKey}: {value}" + text[(match.Index + match.Length)..]
            : text.TrimEnd('\r', '\n') + (text.Length == 0 ? "" : newLine)
              + "# The address this server listens on, chosen from its ports at its first start. Change it to move the server; --listen overrides it." + newLine
              + $"{SettingsKey}: {value}" + newLine;
        if (changed == text) return;
        var temp = settingsFile + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllText(temp, changed, new UTF8Encoding(false));
        File.Move(temp, settingsFile, overwrite: true);
    }

    private static Uri WithPort(Uri listen, int port) => new UriBuilder(listen) { Port = port }.Uri;

    /// <summary>
    /// A lock between processes on one file, taken the way the other writers of a server's settings file take theirs (the paired clients' list): the
    /// lock file opened with no sharing, deleted when it is released, waited for while another process holds it.
    /// </summary>
    private static FileStream TakeLock(string path)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
            catch (IOException) when (DateTime.UtcNow < until) { Thread.Sleep(20); }
            catch (UnauthorizedAccessException) when (DateTime.UtcNow < until) { Thread.Sleep(20); }
        }
    }

    // a top-level line only: an indented "listen:" belongs to another section
    [GeneratedRegex(@"^listen:[ \t]*(?<value>[^#\r\n]*?)[ \t]*(?:#[^\r\n]*)?(?=\r?$)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ListenLine();
}
