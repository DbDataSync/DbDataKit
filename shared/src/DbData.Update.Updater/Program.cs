using DbData.Update;
using DbData.Update.Service;

// dbdata-updater --root <dir> --product <name> --trusted-key id=base64 [--trusted-key ...] [--key-manifest file]
//                [--channel stable|beta|snapshot] [--allow-downgrade] [--updater-version 1.0.0]
// Exit codes: 0 committed/no plan, 1 rolled back or rejected, 2 needs operator attention, 3 usage/config error.

var a = args;
string? Opt(string k) { var i = Array.IndexOf(a, "--" + k); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
IEnumerable<string> Opts(string k) { for (var i = 0; i < a.Length - 1; i++) if (a[i] == "--" + k) yield return a[i + 1]; }

try
{
    var root = Opt("root") ?? throw new ArgumentException("--root is required");
    var product = Opt("product") ?? throw new ArgumentException("--product is required");
    var keys = Opts("trusted-key").Select(s => { var i = s.IndexOf('='); return new TrustedKey(s[..i], Convert.FromBase64String(s[(i + 1)..])); }).ToList();
    if (keys.Count == 0) throw new ArgumentException("at least one --trusted-key id=base64 is required");
    var trust = new TrustStore(keys);
    if (Opt("key-manifest") is { } km) trust = trust.Rotate(File.ReadAllBytes(km));

    var layout = new InstallLayout(root);
    var helper = new UpdateHelper(layout, new LinkCurrentPointer(layout), new UnimplementedServiceControl(), new LoopbackHealthProbe(),
        new NoMigrations(), trust,
        new UpdateHelperOptions
        {
            Product = product,
            Channel = Enum.Parse<Channel>(Opt("channel") ?? "stable", true),
            AllowDowngrade = a.Contains("--allow-downgrade"),
            UpdaterVersion = Opt("updater-version"),
        }, log: Console.WriteLine);

    var outcome = await helper.RunAsync();
    Console.WriteLine($"{outcome.Kind}: {outcome.Message}");
    return outcome.Kind switch
    {
        UpdateOutcomeKind.Committed or UpdateOutcomeKind.NoPlan => 0,
        UpdateOutcomeKind.NeedsOperator => 2,
        _ => 1,
    };
}
catch (Exception ex) when (ex is ArgumentException or FormatException or IOException or UpdateVerificationException)
{
    Console.Error.WriteLine(ex.Message);
    return 3;
}

/// <summary>Placeholder: real Windows SCM / systemd control is Plan 03's ServiceHost. Fails loudly instead of pretending.</summary>
sealed class UnimplementedServiceControl : IServiceControl
{
    private static NotSupportedException Nope() => new("Service control is provided by Plan 03 (DbData.ServiceHost); not wired in this build.");
    public Task<bool> IsRunningAsync(CancellationToken ct) => throw Nope();
    public Task StopAsync(TimeSpan timeout, CancellationToken ct) => throw Nope();
    public Task StartAsync(CancellationToken ct) => throw Nope();
}

/// <summary>Polls a loopback URL (plan.HealthUrl) until the body contains the expected version.</summary>
sealed class LoopbackHealthProbe : IHealthProbe
{
    public async Task<HealthResult> WaitHealthyAsync(string expectedVersion, string? healthUrl, TimeSpan timeout, CancellationToken ct)
    {
        if (healthUrl is null) return new(false, "no health URL in plan");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var body = await http.GetStringAsync(healthUrl, ct);
                if (body.Contains(expectedVersion, StringComparison.OrdinalIgnoreCase)) return new(true);
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { }
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
        return new(false, "timed out waiting for " + expectedVersion);
    }
}
