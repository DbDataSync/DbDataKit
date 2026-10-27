using DbDataProxy.Client;
using DbDataProxy.Server.Hosting;
using DbDataProxy.Server.Secrets;
using DbDataProxy.Tests.Support;
using Grpc.Core;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Logging;
using DbDataProxy.Server.Auth;
using DbDataProxy.Server.Configuration;

namespace DbDataProxy.Tests.Auth;

/// <summary>
/// Drives the real host with sentinel passwords and checks that none of them appears in any log line (at Trace level,
/// exception text included) or in any status text sent to a client.
/// </summary>
public sealed class SecretLeakTests : IDisposable
{
    private const string EnvSentinel = "ENV-SENTINEL-7f3a91";
    private const string LiteralSentinel = "LITERAL-SENTINEL-c20b44";
    private const string LoginSentinel = "LOGIN-SENTINEL-91d5e2";
    private const string PassThroughSentinel = "PASSTHROUGH-SENTINEL-5be8a0";
    private static readonly string[] Sentinels = [EnvSentinel, LiteralSentinel, LoginSentinel, PassThroughSentinel];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbproxy-leak-" + Guid.NewGuid().ToString("N"));
    private readonly string _envName = "DBPROXY_TEST_" + Guid.NewGuid().ToString("N");
    private readonly CaptureLoggerProvider _logs = new();

    public SecretLeakTests()
    {
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable(_envName, EnvSentinel);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(_envName, null);
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private Dictionary<string, string?> Settings(bool require = false) => new()
    {
        ["Proxy:Listen:Address"] = "127.0.0.1",
        ["Proxy:Listen:Port"] = "0",
        ["Proxy:Tls:Mode"] = "None",
        ["Proxy:DataDirectory"] = _dir,
        ["Proxy:Compression:Mode"] = "None",
        ["Proxy:RequireReferencedSecrets"] = require ? "true" : "false",
        ["Proxy:DataSources:ref:Provider"] = "sqlserver",
        ["Proxy:DataSources:ref:ConnectionString"] = $"Server=127.0.0.1,1;User Id=svc;Password=env:{_envName};Connect Timeout=2;Encrypt=False",
        ["Proxy:DataSources:lit:Provider"] = "sqlserver",
        ["Proxy:DataSources:lit:ConnectionString"] = $"Server=127.0.0.1,1;User Id=svc;Password={LiteralSentinel};Connect Timeout=2;Encrypt=False",
        ["Proxy:DataSources:pt:Provider"] = "sqlserver",
        ["Proxy:DataSources:pt:PassThrough"] = "true",
        ["Proxy:DataSources:pt:ConnectionString"] = "Server=127.0.0.1,1;Connect Timeout=2;Encrypt=False",
        ["Proxy:Users:0:Name"] = "alice",
        ["Proxy:Users:0:PasswordHash"] = PasswordHasher.Hash("alice-real-pw", 1000),
        ["Proxy:Users:0:DataSources:0"] = "ref",
        ["Proxy:Users:0:DataSources:1"] = "lit",
        ["Proxy:Users:0:DefaultDataSource"] = "ref",
    };

    private WebApplication Build(Dictionary<string, string?> settings) => ProxyHost.Build([], b =>
    {
        b.Configuration.AddInMemoryCollection(settings);
        b.Logging.SetMinimumLevel(LogLevel.Trace);
        b.Logging.AddProvider(_logs);
    });

    private static string AddressOf(WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().Replace("0.0.0.0", "127.0.0.1");

    private void AssertNoSentinelLogged()
    {
        foreach (var line in _logs.Lines)
            foreach (var s in Sentinels)
                Assert.False(line.Text.Contains(s, StringComparison.Ordinal), $"[{line.Category}] {line.Level} line carried a secret: {line.Text}");
    }

    [Fact]
    public async Task Startup_log_names_literal_secrets_by_key_and_never_shows_a_secret()
    {
        await using var app = Build(Settings());
        await app.StartAsync();

        // A component that logs a connection string by mistake: the logger scrubs it.
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Probe");
        log.LogInformation("Connecting with {ConnectionString}", $"Server=s;Password={EnvSentinel};Pwd={LiteralSentinel}");
        log.LogError(new InvalidOperationException($"driver said: login failed for Password='{LiteralSentinel}' ({EnvSentinel})"), "probe failed");

        var secretWarnings = _logs.Lines.Where(l => l.Category == "DbDataProxy.Secrets" && l.Level == LogLevel.Warning).Select(l => l.Text).ToList();
        Assert.Equal(
            ["Config key Proxy:DataSources:lit:ConnectionString (Password) holds a literal secret; replace it with a secret reference (secret:, env:, file: or dpapi:). Set Proxy:RequireReferencedSecrets=true to refuse literal secrets."],
            secretWarnings);

        var probe = _logs.Lines.Where(l => l.Category == "Probe").ToList();
        Assert.Equal("Connecting with Server=s;Password=***;Pwd=***", probe[0].Text);
        Assert.Contains("probe failed", probe[1].Text);
        Assert.Contains("login failed for Password=***", probe[1].Text);
        AssertNoSentinelLogged();
    }

    [Fact]
    public void RequireReferencedSecrets_fails_startup_naming_the_literal_key()
    {
        var ex = Assert.Throws<PlainSecretNotAllowedException>(() => Build(Settings(require: true)));
        Assert.Contains("Proxy:DataSources:lit:ConnectionString", ex.Message);
        Assert.DoesNotContain("Proxy:DataSources:ref", ex.Message);
        Assert.DoesNotContain(LiteralSentinel, ex.Message);
    }

    [Fact]
    public async Task All_references_satisfy_RequireReferencedSecrets()
    {
        var settings = Settings(require: true);
        settings.Remove("Proxy:DataSources:lit:ConnectionString");
        settings["Proxy:DataSources:lit:ConnectionString"] = "Server=127.0.0.1,1;Trusted_Connection=yes";
        await using var app = Build(settings);
        await app.StartAsync();
        Assert.DoesNotContain(_logs.Lines, l => l.Level == LogLevel.Warning && l.Category == "DbDataProxy.Secrets");
    }

    [Fact]
    public async Task Failed_logins_and_failed_connections_never_leak_a_password()
    {
        await using var app = Build(Settings());
        await app.StartAsync();
        string address = AddressOf(app);
        var statuses = new List<string>();

        // Failed login: a local user with the wrong password, and the sentinel as the (wrong) password.
        using (var client = new ProxyFlightClient(address))
        {
            var login = await Rpc.ExpectAsync(() => client.AuthenticateAsync("alice", LoginSentinel));
            Assert.Equal(StatusCode.Unauthenticated, login.StatusCode);
            statuses.Add(login.Status.Detail);
        }

        // Failed connections: configured credentials (referenced and literal) and pass-through credentials.
        foreach (var (user, password, source) in new[] { ("alice", "alice-real-pw", "ref"), ("alice", "alice-real-pw", "lit"), ("mallory", PassThroughSentinel, "pt") })
        {
            using var client = new ProxyFlightClient(address);
            await client.AuthenticateAsync(user, password);
            var failure = await Rpc.ExpectAsync(async () =>
            {
                var result = await client.QueryAsync("SELECT 1", new QueryOptions { DataSource = source });
                await foreach (var _ in result) { }
            });
            Assert.NotEqual(StatusCode.OK, failure.StatusCode);
            statuses.Add(failure.Status.Detail);
        }

        Assert.All(statuses, s => Assert.All(Sentinels, sentinel => Assert.DoesNotContain(sentinel, s)));
        AssertNoSentinelLogged();
        // The failures really were logged or reported (the test is not vacuous).
        Assert.Contains(_logs.Lines, l => l.Text.Contains("Handshake rejected for user alice"));
        Assert.Contains(_logs.Lines, l => l.Text.Contains("Query by"));
    }

    [Fact]
    public async Task Interceptor_scrubs_status_text_including_the_callers_password()
    {
        await using var app = Build(Settings());
        await app.StartAsync();
        var interceptor = new RedactingInterceptor(app.Services.GetRequiredService<SecretsRuntime>());

        var context = new FakeContext();
        context.UserState[DbDataKit.Serving.FlightSql.FlightCallContext.ContextKey] = new DbDataKit.Serving.ServingContext(
            new DbDataProxy.Server.Serving.ProxyCaller(new ProxyPrincipal("mallory", "passthrough", ["pt"], null, new DbCredentials("mallory", PassThroughSentinel))));
        // The env secret was registered at startup; the caller's password is only known through the principal.
        var ex = await Assert.ThrowsAsync<RpcException>(() => interceptor.UnaryServerHandler<string, string>("req", context,
            (_, _) => throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"[08001] cannot open Server=s;UID=mallory;PWD={PassThroughSentinel};X={EnvSentinel}"))));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
        Assert.DoesNotContain(PassThroughSentinel, ex.Status.Detail);
        Assert.DoesNotContain(EnvSentinel, ex.Status.Detail);
        Assert.StartsWith("[08001] cannot open", ex.Status.Detail);
    }

    private sealed class FakeContext : ServerCallContext
    {
        protected override string MethodCore => "/test/Method";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "peer";
        protected override DateTime DeadlineCore => DateTime.MaxValue;
        protected override Metadata RequestHeadersCore => [];
        protected override CancellationToken CancellationTokenCore => CancellationToken.None;
        protected override Metadata ResponseTrailersCore => [];
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore => new("", new Dictionary<string, List<AuthProperty>>());
        protected override IDictionary<object, object> UserStateCore { get; } = new Dictionary<object, object>();
        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) => throw new NotSupportedException();
        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
    }
}
