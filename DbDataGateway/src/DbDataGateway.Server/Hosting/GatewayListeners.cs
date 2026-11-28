using System.Net;
using System.Text;
using DbDataGateway.Server.Configuration;
using DbDataKit.Config.Yaml;
using DbDataKit.Hosting.Startup;

namespace DbDataGateway.Server.Hosting;

/// <summary>One listener the Gateway chose a port for at this start: the setting that will hold it, and the address.</summary>
/// <param name="ConfigKey">The setting as a configuration key: <c>Gateway:Public:Listen</c>.</param>
/// <param name="FilePath">The setting as a path in <c>server.yaml</c>: <c>public.listen</c>.</param>
public sealed record ChosenListener(string ConfigKey, string FilePath, ListenUrl Url);

/// <summary>What <see cref="GatewayListeners.Choose"/> decided: the listeners that were given no address, the free port of the Gateway's block each takes, or why one could not.</summary>
public sealed class ListenerChoice
{
    public IReadOnlyList<ChosenListener> Chosen { get; init; } = [];

    /// <summary>Why no port of the block could be chosen for a listener; the ports step of the start fails with it.</summary>
    public StartupStepException? Failure { get; init; }
}

/// <summary>
/// The Gateway has three listeners by default (the public one, Flight SQL and the management API) and none has a fixed port: a listener whose <c>listen</c> setting is empty takes the first free port of the Gateway's block
/// (7820 to 7839) at the first start and keeps it, the setting written to <c>server.yaml</c> once the server listens on it. A configured address is never moved.
/// Design: docs/install.md#ports
/// </summary>
public static class GatewayListeners
{
    /// <summary>Picks a port for each listener without an address. A port is found by binding it, so one another program holds is skipped, as are Windows' excluded ranges and the ports of the other listeners.</summary>
    public static ListenerChoice Choose(GatewayOptions options, IReadOnlyList<PortRange> excluded, Func<IPAddress, int, bool>? canBind = null)
    {
        var block = SuiteServices.Gateway.Ports;
        var taken = new List<PortRange>(excluded);
        foreach (var value in AllConfigured(options))
            if (ListenUrl.TryParse(value, out var url, out _) && url!.Port != 0) taken.Add(new PortRange(url.Port, url.Port));
        var chosen = new List<ChosenListener>();
        StartupStepException? failure = null;

        void Take(string key, string path, string address, string scheme)
        {
            var port = ListenPorts.Choose(block, IPAddress.Parse(address), taken, canBind);
            if (port is null) failure ??= StartupStepException.NoFreePort(block, excluded);
            var free = port ?? block.Start;
            taken.Add(new PortRange(free, free));
            chosen.Add(new ChosenListener(key, path, ListenUrl.Parse($"{scheme}://{address}:{free}")));
        }

        // in this order, so a Gateway with no settings has its public listener on 7820, Flight SQL on 7821 and the management API on 7822
        if (options.Public.Listen.Count == 0) Take("Gateway:Public:Listen", "public.listen", "0.0.0.0", "https");
        if (GatewayModules.Has(options, GatewayModules.FlightSql) && options.Listen.Count == 0)
            Take("Gateway:Listen", "listen", "0.0.0.0", options.Tls.Mode == TlsMode.None ? "http" : "https");
        if (options.Management.Enabled && options.Management.Listen.Count == 0) Take("Gateway:Management:Listen", "management.listen", "127.0.0.1", "https");
        return new ListenerChoice { Chosen = chosen, Failure = failure };
    }

    /// <summary>The configuration the choice stands for: the chosen addresses as the settings they will be written to, so the rest of the start reads them like any other.</summary>
    public static IReadOnlyDictionary<string, string?> AsConfiguration(ListenerChoice choice) =>
        choice.Chosen.ToDictionary(c => c.ConfigKey + ":0", c => (string?)c.Url.ToString());

    /// <summary>
    /// Writes each chosen address to its setting in <paramref name="settingsFile"/> (made when it is not there), leaving the rest of the file, comments and all, as it is. The next start reads them as configured.
    /// </summary>
    /// <exception cref="InvalidOperationException">The file cannot be written or edited; the message names it.</exception>
    public static void Pin(string settingsFile, IReadOnlyList<ChosenListener> chosen) =>
        Write(settingsFile, [.. chosen.Select(c => (c.FilePath, new YamlValue(c.Url.ToString())))], "which it chose at this start");

    /// <summary>The listen settings of the three listeners a group's instances share, by their name in <c>server.yaml</c>: what a Gateway reports when it joins a console.</summary>
    public static Dictionary<string, string[]> Reported(GatewayOptions options)
    {
        var reported = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var (setting, listen, _) in Shared(options))
            if (Parsed(listen).Select(u => u.ToString()).ToArray() is { Length: > 0 } addresses) reported[setting] = addresses;
        return reported;
    }

    /// <summary>
    /// The addresses this instance takes from its group's first instance (<paramref name="handed"/>, as the console handed them over at the join): for each of the three shared listeners, the group's schemes and
    /// ports, on the address this instance's own setting names (the listener's default address when it has none), so the instance listens on the group's ports on its own machine. A setting the group did not hand
    /// over is left out.
    /// </summary>
    public static IReadOnlyList<(string Setting, IReadOnlyList<ListenUrl> Urls)> FromGroup(GatewayOptions own, IReadOnlyDictionary<string, string[]> handed)
    {
        var taken = new List<(string, IReadOnlyList<ListenUrl>)>();
        foreach (var (setting, listen, defaultHost) in Shared(own))
        {
            if (!handed.TryGetValue(setting, out var addresses)) continue;
            var host = Parsed(listen).FirstOrDefault()?.Host ?? defaultHost;
            var urls = Parsed(addresses).Select(u => ListenUrl.Parse($"{u.Scheme}://{(host.Contains(':') ? $"[{host}]" : host)}:{u.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)}")).ToList();
            if (urls.Count > 0) taken.Add((setting, urls));
        }
        return taken;
    }

    /// <summary>
    /// Writes the group's ports (<see cref="FromGroup"/>) to <paramref name="settingsFile"/> as this instance's <c>listen</c>, <c>public.listen</c> and <c>management.listen</c>. Returns true when that changes an
    /// address the instance listens on now, so the instance starts again to listen on the group's.
    /// </summary>
    /// <exception cref="InvalidOperationException">The file cannot be written or edited; the message names it.</exception>
    public static bool TakeGroup(string settingsFile, GatewayOptions own, IReadOnlyDictionary<string, string[]> handed)
    {
        var taken = FromGroup(own, handed);
        if (taken.Count == 0) return false;
        Write(settingsFile, [.. taken.Select(t => (t.Setting, t.Urls.Count == 1
            ? new YamlValue(t.Urls[0].ToString())
            : YamlValue.Raw("[" + string.Join(", ", t.Urls.Select(u => YamlText.FlowScalar(u.ToString()))) + "]")))], "which its group listens on");
        var now = Shared(own).ToDictionary(l => l.Setting, l => Parsed(l.Listen).Select(u => u.ToString()).ToList());
        return taken.Any(t => !now[t.Setting].SequenceEqual(t.Urls.Select(u => u.ToString())));
    }

    // the listeners every instance of a group has on the same ports, with the address one takes when its setting names none
    private static IEnumerable<(string Setting, IReadOnlyList<string> Listen, string DefaultHost)> Shared(GatewayOptions o) =>
    [
        ("public.listen", o.Public.Listen, "0.0.0.0"),
        ("listen", o.Listen, "0.0.0.0"),
        ("management.listen", o.Management.Listen, "127.0.0.1"),
    ];

    private static void Write(string settingsFile, IReadOnlyList<(string Path, YamlValue Value)> settings, string why)
    {
        if (settings.Count == 0) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(settingsFile))!);
            var text = File.Exists(settingsFile) ? File.ReadAllText(settingsFile) : "";
            foreach (var (path, value) in settings)
            {
                var edit = YamlEditor.Set(text, path, value, settingsFile);
                if (edit.Text is null)
                    throw new InvalidOperationException($"The Gateway listens on {value.Text}, {why}, and cannot write it to {settingsFile} as `{path}` to keep it: {edit.Problem?.Message ?? "the edit was refused"}");
                text = edit.Text;
            }
            var temp = settingsFile + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            File.Move(temp, settingsFile, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"The Gateway listens on ports {why}, and cannot write them to {settingsFile} to keep them: {e.Message}", e);
        }
    }

    /// <summary>Every address of every listener the settings give, parsed.</summary>
    public static IEnumerable<ListenUrl> Parsed(IEnumerable<string> listen) =>
        listen.Select(v => ListenUrl.TryParse(v, out var url, out _) ? url : null).OfType<ListenUrl>();

    /// <summary>The port people reach the public listener on: its first address, 0 when it has none.</summary>
    public static int PublicPort(GatewayOptions o) => Parsed(o.Public.Listen).FirstOrDefault()?.Port ?? 0;

    /// <summary>The port of a module's listener of its own, or the public listener's when it has none.</summary>
    public static int PortOf(IReadOnlyList<string> own, GatewayOptions o) => Parsed(own).FirstOrDefault()?.Port ?? PublicPort(o);

    private static IEnumerable<string> AllConfigured(GatewayOptions o) =>
        [.. o.Public.Listen, .. o.Listen, .. o.Management.Listen, .. o.Identity.Listen, .. o.Apps.Listen, .. o.Store.Listen, .. o.Views.Listen, .. o.Mcp.Listen, .. o.Rest.Listen];
}
