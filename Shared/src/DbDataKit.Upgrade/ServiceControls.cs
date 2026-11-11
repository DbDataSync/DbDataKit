using System.Diagnostics;
using DbDataKit.Update.Service;

namespace DbDataKit.Upgrade;

/// <summary>
/// Stops and starts the service the program runs as, for <see cref="UpdateHelper"/>: <c>systemctl</c> for a systemd unit on Linux, <c>sc.exe</c> for a Windows service. The helper runs with the rights the service has, so a service that
/// may not control itself (a hardened unit with a dynamic user) cannot be upgraded from the console, and says so at <c>prepare</c> (the install guide has the unit's needs).
/// </summary>
public sealed class CommandServiceControl(string name, Func<string, IReadOnlyList<string>, CancellationToken, Task<(int Exit, string Output)>>? run = null) : IServiceControl
{
    private readonly Func<string, IReadOnlyList<string>, CancellationToken, Task<(int Exit, string Output)>> _run = run ?? Run;

    private static string Tool => OperatingSystem.IsWindows() ? "sc.exe" : "systemctl";

    public async Task<bool> IsRunningAsync(CancellationToken ct)
    {
        var (exit, output) = OperatingSystem.IsWindows() ? await _run(Tool, ["query", name], ct) : await _run(Tool, ["is-active", name], ct);
        return OperatingSystem.IsWindows() ? exit == 0 && output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase) : exit == 0;
    }

    public async Task StopAsync(TimeSpan timeout, CancellationToken ct)
    {
        var (exit, output) = await _run(Tool, OperatingSystem.IsWindows() ? ["stop", name] : ["stop", name], ct);
        if (exit != 0 && !(OperatingSystem.IsWindows() && output.Contains("1062"))) throw new InvalidOperationException($"{Tool} stop {name} failed ({exit}): {output.Trim()}");
        if (!OperatingSystem.IsWindows()) return; // systemctl stop waits for the unit
        var until = DateTime.UtcNow + timeout;
        while (await IsRunningAsync(ct))
        {
            if (DateTime.UtcNow > until) throw new TimeoutException($"The service {name} did not stop within {timeout.TotalSeconds:0} seconds.");
            await Task.Delay(500, ct);
        }
    }

    public async Task StartAsync(CancellationToken ct)
    {
        var (exit, output) = await _run(Tool, ["start", name], ct);
        if (exit != 0) throw new InvalidOperationException($"{Tool} start {name} failed ({exit}): {output.Trim()}");
    }

    private static async Task<(int Exit, string Output)> Run(string file, IReadOnlyList<string> args, CancellationToken ct)
    {
        var info = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"{file} could not be started.");
        var output = await process.StandardOutput.ReadToEndAsync(ct) + await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, output);
    }
}

/// <summary>Waits until the program that was started answers <c>/version</c> on its own loopback address with the expected version and <c>ready</c>.</summary>
public sealed class VersionHealthProbe(TimeSpan? interval = null) : IHealthProbe
{
    public async Task<HealthResult> WaitHealthyAsync(string expectedVersion, string? healthUrl, TimeSpan timeout, CancellationToken ct)
    {
        if (healthUrl is null) return new(false, "the plan has no health address");
        // the server's certificate is its own (clients pin it), and the address is loopback (the plan is refused otherwise), so the chain is not what is checked here
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var until = DateTime.UtcNow + timeout;
        string last = "no answer";
        while (DateTime.UtcNow < until)
        {
            try
            {
                using var response = await http.GetAsync(healthUrl, ct);
                var body = await response.Content.ReadAsStringAsync(ct);
                using var json = System.Text.Json.JsonDocument.Parse(body);
                var version = json.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
                var ready = !json.RootElement.TryGetProperty("ready", out var r) || r.ValueKind != System.Text.Json.JsonValueKind.False;
                if (version is not null && version.Contains(expectedVersion, StringComparison.OrdinalIgnoreCase) && ready) return new(true);
                last = version is null ? "its answer names no version" : !version.Contains(expectedVersion, StringComparison.OrdinalIgnoreCase) ? $"it reports version {version}" : "it reports that it is not ready";
            }
            catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) && !ct.IsCancellationRequested) { last = ex.Message; }

            await Task.Delay(interval ?? TimeSpan.FromSeconds(1), ct);
        }

        return new(false, $"the new version {expectedVersion} did not become healthy within {timeout.TotalSeconds:0} seconds ({last})");
    }
}
