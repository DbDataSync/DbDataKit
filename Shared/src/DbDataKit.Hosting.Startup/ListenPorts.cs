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
/// The address a server listens on, as <c>ListenPorts.Resolve</c> found it. When no port of the sub-block could be bound, <see cref="Address"/>
/// holds the sub-block's first port, so the host can still be built, and the ports step fails with the reason
/// (<see cref="StartupPorts.BindAsync(StartupContext, ListenChoice, Func{CancellationToken, Task}, IEnumerable{int}?, CancellationToken)"/>).
/// </summary>
public sealed class ListenChoice
{
    internal ListenChoice(Uri address, ListenSource source, string settingsFile, StartupStepException? failure = null, IReadOnlyList<Uri>? addresses = null)
    {
        Address = address;
        Addresses = addresses is { Count: > 0 } ? addresses : [address];
        Source = source;
        SettingsFile = settingsFile;
        Failure = failure;
    }

    /// <summary>The address the server listens on, the first one when it listens on several: <c>https://127.0.0.1:7841/</c>. It is the one the server reports to the console.</summary>
    public Uri Address { get; }

    /// <summary>Every address the server listens on: <see cref="Address"/> alone unless <c>--listen</c> or the <c>listen:</c> line named a list.</summary>
    public IReadOnlyList<Uri> Addresses { get; }

    public ListenSource Source { get; }

    /// <summary>The server's settings file, which holds the <c>listen:</c> line.</summary>
    public string SettingsFile { get; }

    /// <summary>Why no port of the sub-block could be chosen; null when the address is usable.</summary>
    public StartupStepException? Failure { get; }

    /// <summary>The port of <see cref="Address"/>.</summary>
    public int Port => Address.Port;

    /// <summary>The ports of every address, for the ports step.</summary>
    public IEnumerable<int> Ports => Addresses.Select(a => a.Port);
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
        Func<IPAddress, int, bool>? canBind = null) =>
        Resolve(service, flag is null ? null : [flag], settingsFile, defaultListen, excluded, canBind);

    /// <summary>
    /// <see cref="Resolve(SuiteService, Uri?, string, Uri, IReadOnlyList{PortRange}, Func{IPAddress, int, bool}?)"/> for a flag or a settings line that names
    /// several addresses (the first is the one reported to the console); a port is chosen and pinned only when neither names any.
    /// </summary>
    public static ListenChoice Resolve(SuiteService service, IReadOnlyList<Uri>? flags, string settingsFile, Uri defaultListen, IReadOnlyList<PortRange> excluded,
        Func<IPAddress, int, bool>? canBind = null)
    {
        if (flags is { Count: > 0 }) return new ListenChoice(flags[0], ListenSource.Flag, settingsFile, addresses: flags);
        if (ReadPinnedAll(settingsFile) is { Count: > 0 } pinned) return new ListenChoice(pinned[0], ListenSource.Settings, settingsFile, addresses: pinned);
        var block = service.Ports;
        var port = Choose(block, AddressOf(defaultListen), excluded, canBind);
        return port is { } free
            ? new ListenChoice(WithPort(defaultListen, free), ListenSource.Chosen, settingsFile)
            : new ListenChoice(WithPort(defaultListen, block.Start), ListenSource.Chosen, settingsFile, StartupStepException.NoFreePort(block, excluded));
    }

    /// <summary>
    /// The first port of <paramref name="block"/> outside the excluded ranges that can be bound on <paramref name="address"/>, or null when there is
    /// none. A port is tried by binding it, which skips one another program holds. With the real bind (no <paramref name="canBind"/>), the ranges
    /// Windows excludes at this moment are read and skipped as well as <paramref name="excluded"/>: Windows does not always refuse a bind inside an
    /// excluded range (a loopback listener can be bound there), so a refused bind is never what finds them.
    /// </summary>
    public static int? Choose(PortRange block, IPAddress address, IReadOnlyList<PortRange> excluded, Func<IPAddress, int, bool>? canBind = null)
    {
        if (canBind is null)
        {
            canBind = CanBind;
            excluded = [.. excluded, .. ExcludedPortRanges.Read()];
        }
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

    /// <summary>The https addresses of a <c>--listen</c> flag: one, or several separated by a semicolon or a comma.</summary>
    /// <exception cref="FormatException">An address is not an https address with a port; the message says why.</exception>
    public static IReadOnlyList<Uri> ParseFlag(string text)
    {
        var urls = ListenUrl.ParseFlag(text);
        if (urls.Count == 0) throw new FormatException("--listen is empty.");
        if (urls.FirstOrDefault(u => !u.IsHttps) is { } plain) throw new FormatException($"--listen has {plain}; the server listens on https.");
        return [.. urls.Select(u => u.ToUri())];
    }

    /// <summary>The first address of the <c>listen:</c> setting of <paramref name="settingsFile"/>, or null when the file or the setting is not there.</summary>
    /// <exception cref="InvalidOperationException">The setting is not an https address; the message names the file.</exception>
    public static Uri? ReadPinned(string settingsFile) => ReadPinnedAll(settingsFile)?[0];

    /// <summary>
    /// Every address of the <c>listen:</c> setting of <paramref name="settingsFile"/>: one address (<c>listen: https://127.0.0.1:7840</c>) or a list
    /// (<c>listen: [https://127.0.0.1:7840, https://127.0.0.1:7841]</c>, or one <c>- address</c> line each); null when the file or the setting is not there.
    /// </summary>
    /// <exception cref="InvalidOperationException">An address is not an https address with a port; the message names the file.</exception>
    public static IReadOnlyList<Uri>? ReadPinnedAll(string settingsFile)
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
        var value = line.Groups["value"].Value.Trim();
        List<string> items;
        if (value.Length == 0)
        {
            items = [];
            foreach (var next in text[(line.Index + line.Length)..].Split('\n'))
            {
                var trimmed = next.Trim('\r');
                if (trimmed.Trim().Length == 0 || trimmed.TrimStart().StartsWith('#')) continue;
                var item = BlockItem().Match(trimmed);
                if (!item.Success) break;
                items.Add(item.Groups["value"].Value);
            }
            if (items.Count == 0) return null;
        }
        else if (value.StartsWith('['))
        {
            if (!value.EndsWith(']')) throw new InvalidOperationException($"{settingsFile}: {SettingsKey} is '{value}'; write one https address, or a list on one line such as [https://127.0.0.1:7840, https://127.0.0.1:7841].");
            items = [.. value[1..^1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        }
        else if (value.StartsWith('{'))
            throw new InvalidOperationException($"{settingsFile}: {SettingsKey} is '{value}'; it is one https address or a list of them, such as https://127.0.0.1:7840 (the port is part of the address).");
        else items = [value];

        var addresses = new List<Uri>();
        foreach (var item in items)
        {
            var written = item.Trim().Trim('"', '\'');
            if (!ListenUrl.TryParse(written, out var parsed, out _) || !parsed!.IsHttps)
                throw new InvalidOperationException($"{settingsFile}: {SettingsKey} is '{written}'; write the https address the server listens on, with its port, such as https://127.0.0.1:7840.");
            addresses.Add(parsed.ToUri());
        }
        return addresses;
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

    [GeneratedRegex(@"^[ \t]+-[ \t]*(?<value>[^#\r\n]*?)[ \t]*(?:#[^\r\n]*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex BlockItem();

    // a top-level line only: an indented "listen:" belongs to another section
    [GeneratedRegex(@"^listen:[ \t]*(?<value>[^#\r\n]*?)[ \t]*(?:#[^\r\n]*)?(?=\r?$)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ListenLine();
}
