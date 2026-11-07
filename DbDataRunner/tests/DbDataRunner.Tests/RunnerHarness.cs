using DbDataKit.Runner.Api;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DbDataKit.Monitoring;
using DbDataKit.Runner;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataRunner.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataRunner.Tests;

/// <summary>
/// A real DbDataRunner server in a directory of its own (HTTPS on a free port, the real task host from the server project's build output, the task processes real), over a state on the engine the test names, with a console
/// pairing and calling its management API the way a console does.
/// </summary>
public sealed class RunnerHarness : IAsyncDisposable
{
    private readonly StateEngines _engines = new();
    private readonly ConsoleIdentity _console;
    private readonly List<string> _log = [];
    private PairedServer? _paired;
    private HttpClient? _http;
    private WebApplication? _app;
    private readonly bool _ownsDirectory;

    /// <param name="staging">Whether this runner follows staged versions and activates them (R17). Off by default: the older tests drive the manual publish API and expect a staged version to stay staged until they activate it.</param>
    /// <param name="sharedState">The state of another harness: a second runner of the same group (or, on SQLite, a second server on the same file, which needs that harness's <paramref name="directory"/> too).</param>
    public RunnerHarness(string engine, TimeSpan? tick = null, TimeSpan? grace = null, IReadOnlyList<string>? channels = null, StateSettings? sharedState = null, string? directory = null, long? maxArtifactBytes = null, TimeSpan? lease = null, bool staging = false, Func<BuiltAgainst, string?>? stagingContracts = null, TimeSpan? activationMaxWait = null)
    {
        // a short path: the socket's directory is in it, and a Unix socket's path has a small limit
        Directory = directory ?? System.IO.Directory.CreateTempSubdirectory("dbdr-").FullName;
        _ownsDirectory = directory is null;
        // runners of one group share their paired clients, so each harness is a console of its own
        _console = new(sharedState is null ? "console-1" : "console-" + Guid.NewGuid().ToString("N")[..8], ConsoleSigningKey.Generate());
        Settings = new RunnerSettings(Directory, new Uri("https://127.0.0.1:0"))
        {
            State = sharedState ?? _engines.Provision(engine),
            Tick = tick ?? TimeSpan.FromMilliseconds(100),
            CancellationGrace = grace ?? TimeSpan.FromSeconds(10),
            InstanceHeartbeat = TimeSpan.FromSeconds(1),
            Channels = channels ?? ["dev"],
            TaskHostPath = ServerBuild.TaskHost,
            MaxArtifactBytes = maxArtifactBytes ?? RunnerPublishing.DefaultMaxArtifactBytes,
            Secrets = new DbDataKit.Secrets.SecretResolverChain([new DbDataKit.Secrets.EnvSecretProvider(k => k == "DBDATA_SECRET_SHOP_HOOK" ? WebhookSecret : null)]),
            InstanceLeaseOverride = lease,
            Staging = staging,
            StagingContracts = stagingContracts,
            ActivationMaxWait = activationMaxWait ?? TimeSpan.FromMinutes(10),
        };
        State = RunnerState.Open(Directory, Settings.State, null, Settings.Group);
    }

    /// <summary>The value of the secret <c>shop_hook</c> in the runner's secret store (what a signed webhook is signed with).</summary>
    public const string WebhookSecret = "s3cret-for-tests";

    public string Directory { get; }

    /// <summary>The runner engine of the running server.</summary>
    public RunnerEngine Engine => _app!.Services.GetRequiredService<RunnerEngine>();

    public RunnerSettings Settings { get; }

    public StateDatabase State { get; }

    public IReadOnlyList<string> Log => _log;

    public Uri Url { get; private set; } = null!;

    public string InstanceId => RunnerServer.InstanceId(State);

    public RunStore Runs => new(State, RunnerState.EngineService);

    public AuditLog Audit => new(State, RunnerState.ServiceId);

    /// <summary>Puts the sample package into the runner's state, as <c>package install</c> does.</summary>
    public PackageInstalled Install(params PackageTask[] tasks)
    {
        var manifest = SamplePackage.WriteManifest(SamplePackage.Manifest(tasks));
        return RunnerPackageInstaller.Install(State, manifest, SamplePackage.Directory, "dev", "test");
    }

    /// <summary>A single-use pairing code for a client whose ceiling is <paramref name="ceiling"/> (what <c>server pair --max-role</c> makes).</summary>
    public string NewCode(ServerRole ceiling) => new PairingFiles(Settings.ServerYaml, Group: State).Codes().Create(ServerRoles.Close(ceiling)).Code;

    public async Task StartAsync()
    {
        _app = RunnerServer.Build(Settings, RunnerServer.Certificate(Directory), _log.Add, State);
        await _app.StartAsync();
        Url = new Uri(_app.Urls.First());
    }

    /// <summary>Stops the server the way a service manager's stop does: the host's own graceful stop.</summary>
    public async Task StopAsync()
    {
        if (_app is null) return;
        await _app.StopAsync();
        await _app.DisposeAsync();
        _app = null;
    }

    private async Task<HttpClient> ClientAsync(ServerRole ceiling)
    {
        if (_http is not null) return _http;
        var code = new PairingFiles(Settings.ServerYaml, Group: State).Codes().Create(ServerRoles.Close(ceiling));
        _paired = await PairingClient.PairAsync(Url, code.Code, _console, ClientKind.Console, "test console");
        return _http = new HttpClient(PairingClient.Pinned(_paired.CertificateFingerprint)) { BaseAddress = Url };
    }

    /// <summary>A call as a person holding <paramref name="role"/> (the pairing allows up to an administrator). <paramref name="token"/> false sends none.</summary>
    public async Task<(HttpStatusCode Status, JsonNode? Body)> CallAsync(HttpMethod method, string path, ServerRole role, object? body = null, string subject = "ada", bool token = true)
    {
        var http = await ClientAsync(ServerRole.Admin);
        using var request = new HttpRequestMessage(method, path);
        if (token) request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, _console.Issuer().Issue(new ConsoleTokenRequest(_paired!.InstanceId, subject, char.ToUpperInvariant(subject[0]) + subject[1..], role)));
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    /// <summary>A POST of raw bytes with the headers given, with a token of <paramref name="role"/> or, when it is null, none (a sender that signs its body).</summary>
    public async Task<(HttpStatusCode Status, JsonNode? Body)> PostRawAsync(string path, byte[] body, IReadOnlyDictionary<string, string>? headers = null, ServerRole? role = null)
    {
        var http = await ClientAsync(ServerRole.Admin);
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new ByteArrayContent(body) };
        if (role is { } r) request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, _console.Issuer().Issue(new ConsoleTokenRequest(_paired!.InstanceId, "ada", "Ada", r)));
        foreach (var (name, value) in headers ?? new Dictionary<string, string>()) request.Headers.TryAddWithoutValidation(name, value);
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    public Task<(HttpStatusCode Status, JsonNode? Body)> GetAsync(string path, ServerRole role = ServerRole.Viewer, string subject = "ada") => CallAsync(HttpMethod.Get, path, role, null, subject);

    public Task<(HttpStatusCode Status, JsonNode? Body)> PostAsync(string path, ServerRole role = ServerRole.Operator, object? body = null, string subject = "ada") => CallAsync(HttpMethod.Post, path, role, body, subject);

    /// <summary>Waits for <paramref name="condition"/> to hold, asking every 100 milliseconds; fails with <paramref name="what"/> and the server's log after <paramref name="seconds"/>.</summary>
    public async Task<T> WaitAsync<T>(Func<Task<T?>> probe, string what, int seconds = 60) where T : class
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (await probe() is { } found) return found;
            await Task.Delay(100);
        }

        throw new TimeoutException($"Waited {seconds} seconds for {what}. The server said:\n{string.Join('\n', _log)}");
    }

    /// <summary>The runs a client reads with <c>GET /api/v1/runs</c> and the query given.</summary>
    public async Task<JsonArray> RunsAsync(string query = "")
    {
        var (status, body) = await GetAsync("/api/v1/runs" + query);
        Assert.Equal(HttpStatusCode.OK, status);
        return body!["runs"]!.AsArray();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _http?.Dispose();
        _engines.Dispose();
        if (_ownsDirectory)
            try { System.IO.Directory.Delete(Directory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* temp */ }
    }
}

/// <summary>The build output of the server's own project, which the build wrote to <c>server-output.txt</c> in this project's output: where the task host is beside the program.</summary>
internal static class ServerBuild
{
    public static string Directory { get; } = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "server-output.txt")).Trim();

    public static string TaskHost { get; } = RunnerTaskHost.Require(null, Directory);
}
