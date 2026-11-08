using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace DbDataKit.Runner.Limits;

/// <summary>What <see cref="LimitCapabilities.Detect"/> should look at.</summary>
public sealed record LimitCapabilityOptions
{
    /// <summary>Hosts (<c>name</c> or <c>name:port</c>, port 443 when absent) the runner tries to connect to, to say whether it can reach the outside. None configured: <c>network</c> is <c>unknown</c>.</summary>
    public IReadOnlyList<string> NetworkProbeHosts { get; init; } = [];

    /// <summary>How long each connect may take.</summary>
    public TimeSpan NetworkProbeTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Options the limits themselves are applied with (what is detected is what <see cref="ProcessLimits.Apply"/> would do with them).</summary>
    public LimitOptions Limits { get; init; } = new();
}

/// <summary>
/// The capabilities a runner reports about its limits and environment (plan 28 section 5, DR6): the name and value pairs feed <c>RunnerInstances.ReplaceCapabilities</c>. The runner reports,
/// the suite does not enforce a network policy.
/// </summary>
public static class LimitCapabilities
{
    public const string MemoryLimit = "memory_limit";
    public const string CpuLimit = "cpu_limit";
    public const string Os = "os";
    public const string Network = "network";

    /// <summary>Detects the capabilities now. The network test is bounded by its timeout; use <see cref="DetectAsync"/> to cancel it.</summary>
    public static IReadOnlyDictionary<string, string> Detect(LimitCapabilityOptions? options = null) =>
        DetectAsync(options).GetAwaiter().GetResult();

    /// <summary>
    /// <c>memory_limit</c>: <c>enforced</c> (job object, cgroup) or <c>watched</c> (watchdog); <c>cpu_limit</c>: <c>enforced</c> or <c>none</c>; <c>os</c>: <c>windows</c>, <c>linux</c>, <c>macos</c> or <c>other</c>;
    /// <c>mechanism</c>: what holds the limits; <c>network</c>: <c>outside</c> when any probe host answers, <c>none</c> when hosts are configured and none does, <c>unknown</c> when none is configured; <c>odbc:&lt;id&gt;</c> per installed ODBC driver (<see cref="OdbcCapabilities"/>).
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, string>> DetectAsync(LimitCapabilityOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new LimitCapabilityOptions();
        var (memory, cpu, mechanism) = ProcessLimits.Probe(options.Limits);
        var detected = new Dictionary<string, string>
        {
            [MemoryLimit] = Word(memory),
            [CpuLimit] = Word(cpu),
            ["limit_mechanism"] = mechanism,
            [Os] = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "macos" : "other",
            [Network] = await ProbeNetworkAsync(options, cancellationToken).ConfigureAwait(false),
        };
        // the ODBC drivers installed here (plan 28c QE12): odbc:<id>, read-only like the rest
        foreach (var (name, driver) in OdbcCapabilities.Detect()) detected[name] = driver;
        return detected;
    }

    private static string Word(LimitEnforcement e) => e switch
    {
        LimitEnforcement.Enforced => "enforced",
        LimitEnforcement.Watched => "watched",
        _ => "none",
    };

    private static async Task<string> ProbeNetworkAsync(LimitCapabilityOptions options, CancellationToken cancellationToken)
    {
        if (options.NetworkProbeHosts.Count == 0) return "unknown";
        using var any = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var attempts = options.NetworkProbeHosts.Select(h => CanConnectAsync(h, options.NetworkProbeTimeout, any.Token)).ToList();
        while (attempts.Count > 0)
        {
            var done = await Task.WhenAny(attempts).ConfigureAwait(false);
            attempts.Remove(done);
            if (await done.ConfigureAwait(false))
            {
                await any.CancelAsync().ConfigureAwait(false); // the others need not finish
                return "outside";
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return "none";
    }

    private static async Task<bool> CanConnectAsync(string hostAndPort, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var (host, port) = Split(hostAndPort);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or IOException or ArgumentException)
        {
            return false;
        }
    }

    private static (string Host, int Port) Split(string hostAndPort)
    {
        var colon = hostAndPort.LastIndexOf(':');
        return colon > 0 && !hostAndPort.AsSpan(colon + 1).Contains(']') && int.TryParse(hostAndPort.AsSpan(colon + 1), out var port)
            ? (hostAndPort[..colon], port)
            : (hostAndPort, 443);
    }
}
