using System.Net;
using System.Text;
using DbDataKit.Hosting;
using DbDataKit.Update.Detect;
using DbDataKit.Update.Service;

namespace DbDataKit.Upgrade.Tests;

/// <summary>A "service" for the helper to stop and start: while it runs it answers <c>/version</c> on a loopback port with what its program folder's <c>version.txt</c> says, and not ready when the folder holds a file <c>broken</c>.</summary>
public sealed class FakeUnit(string programDirectory, int port) : IServiceControl, IDisposable
{
    private HttpListener? _listener;
    public List<string> Calls { get; } = [];

    public Task<bool> IsRunningAsync(CancellationToken ct) => Task.FromResult(_listener is not null);

    public Task StopAsync(TimeSpan timeout, CancellationToken ct)
    {
        Calls.Add("stop");
        Close();
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken ct)
    {
        Calls.Add("start");
        Open();
        return Task.CompletedTask;
    }

    public void Open()
    {
        var version = File.ReadAllText(Path.Combine(programDirectory, "version.txt")).Trim();
        var ready = !File.Exists(Path.Combine(programDirectory, "broken"));
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        _listener = listener;
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                try
                {
                    var context = await listener.GetContextAsync();
                    var body = Encoding.UTF8.GetBytes($"{{\"product\":\"dbdatatest\",\"version\":\"{version}\",\"ready\":{(ready ? "true" : "false")}}}");
                    context.Response.ContentType = "application/json";
                    await context.Response.OutputStream.WriteAsync(body);
                    context.Response.Close();
                }
                catch (Exception) { break; }
            }
        });
    }

    private void Close()
    {
        try { _listener?.Stop(); _listener?.Close(); } catch (ObjectDisposedException) { }
        _listener = null;
    }

    public void Dispose() => Close();
}

/// <summary>Runs the helper in this process, as the real launcher starts it as another one.</summary>
public sealed class InProcessLauncher(IServiceControl unit, TimeSpan interval) : IUpgradeLauncher
{
    public Task? Running { get; private set; }

    public Task LaunchAsync(HelperInvocation invocation, CancellationToken ct)
    {
        Running = Task.Run(() => UpgradeHelperHost.RunAsync(invocation, unit, new VersionHealthProbe(interval)));
        return Task.CompletedTask;
    }
}

/// <summary>A machine: a program folder at the old version, running; a mirror folder with the releases.</summary>
public sealed class Machine : MirrorFolder
{
    public const string Old = "2026.11.1.1000";

    public string Program => Path.Combine(Root, "app");
    public int Port { get; } = FreePort();
    public FakeUnit Unit { get; }
    public DrainCoordinator Drain { get; } = new();
    public InProcessLauncher Launcher { get; }
    public UpgradeService Service { get; private set; } = null!;

    public Machine()
    {
        Directory.CreateDirectory(Program);
        File.WriteAllText(Path.Combine(Program, "version.txt"), Old);
        Unit = new FakeUnit(Program, Port);
        Unit.Open();
        Launcher = new InProcessLauncher(Unit, TimeSpan.FromMilliseconds(100));
    }

    public UpgradeService Create(Func<IReadOnlyList<PeerGeneration>>? peers = null, InstallEnvironment? environment = null, TimeSpan? drain = null, string? mirror = null)
    {
        var settings = new UpgradeFeedSettings { Mirror = mirror ?? Mirror };
        Service = new UpgradeService(new UpgradeOptions
        {
            Product = Product, Version = Old, ProgramDirectory = Program, ServiceName = "dbdatatest", HealthUrl = new Uri($"http://127.0.0.1:{Port}/version"),
            HealthTimeout = TimeSpan.FromSeconds(4), DrainTimeout = drain ?? TimeSpan.FromSeconds(5), Peers = peers ?? (() => []),
            Environment = environment ?? new InstallEnvironment("/opt/dbdatatest/dbdatatest", "/opt/dbdatatest/", new Dictionary<string, string?>()),
        }, ReleaseFeed.Create(settings), Drain, Launcher);
        return Service;
    }

    public async Task<string> ServedVersionAsync()
    {
        using var http = new HttpClient();
        var body = await http.GetStringAsync($"http://127.0.0.1:{Port}/version");
        return System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("version").GetString()!;
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public override void Dispose()
    {
        Unit.Dispose();
        base.Dispose();
    }
}
