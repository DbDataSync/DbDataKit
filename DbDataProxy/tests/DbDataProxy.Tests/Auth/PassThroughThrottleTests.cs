using System.Data;
using System.Data.Common;
using DbDataKit.Connections;
using DbDataKit.Dialect;
using DbDataProxy.Client;
using DbDataProxy.Server.Hosting;
using DbDataProxy.Tests.Support;
using Grpc.Core;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace DbDataProxy.Tests.Auth;

/// <summary>
/// S-2: a pass-through login is only checked by the database, at query time. The shared failure model classifies a rejected login (kind Login), the
/// server answers Unauthenticated and counts it in the login throttle; once a user and client are locked out they are refused before any connection is made.
/// </summary>
public sealed class PassThroughThrottleTests : IAsyncLifetime
{
    private WebApplication? _app;
    private string _address = "";
    private int _opens;

    public async ValueTask InitializeAsync()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Proxy:Listen:Address"] = "127.0.0.1",
            ["Proxy:Listen:Port"] = "0",
            ["Proxy:Tls:Mode"] = "None",
            ["Proxy:DataDirectory"] = Path.Combine(Path.GetTempPath(), "dbproxy-throttle-" + Guid.NewGuid().ToString("N")),
            ["Proxy:Compression:Mode"] = "None",
            ["Proxy:Auth:MaxFailedLogins"] = "3",
            ["Proxy:Auth:LockoutMinutes"] = "5",
            ["Proxy:DataSources:pt:Provider"] = "odbc",
            ["Proxy:DataSources:pt:PassThrough"] = "true",
            ["Proxy:DataSources:pt:ConnectionString"] = "Driver={Fake};Server=s",
        };
        _app = ProxyHost.Build([], b => b.Configuration.AddInMemoryCollection(settings),
            services => services.AddSingleton(new ProviderRegistry([new RejectingProvider(() => Interlocked.Increment(ref _opens))])));
        await _app.StartAsync();
        _address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().Replace("0.0.0.0", "127.0.0.1");
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
    }

    private async Task<RpcException> QueryAsDbUserAsync(string user)
    {
        using var client = new ProxyFlightClient(_address);
        await client.AuthenticateAsync(user, "wrong-db-password"); // pass-through accepts anything at the handshake
        return await Rpc.ExpectAsync(async () => await client.QueryAsync("SELECT 1", new QueryOptions { DataSource = "pt" }));
    }

    [Fact]
    public async Task A_rejected_database_login_is_unauthenticated_with_a_code_and_no_driver_text()
    {
        var ex = await QueryAsDbUserAsync("mallory");
        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
        Assert.Equal("The login was rejected. [odbc 28000]", ex.Status.Detail);
        Assert.DoesNotContain("wrong-db-password", ex.Status.Detail);
    }

    [Fact]
    public async Task Repeated_rejections_lock_the_user_out_before_the_database_is_contacted_again()
    {
        // One session (token) keeps asking: each rejected database login counts, the fourth call is refused without a connection.
        using (var client = new ProxyFlightClient(_address))
        {
            await client.AuthenticateAsync("mallory", "wrong-db-password");
            for (int i = 0; i < 3; i++)
            {
                var rejected = await Rpc.ExpectAsync(async () => await client.QueryAsync("SELECT 1", new QueryOptions { DataSource = "pt" }));
                Assert.Equal("The login was rejected. [odbc 28000]", rejected.Status.Detail);
            }
            Assert.Equal(3, _opens);

            var locked = await Rpc.ExpectAsync(async () => await client.QueryAsync("SELECT 1", new QueryOptions { DataSource = "pt" }));
            Assert.Equal(StatusCode.Unauthenticated, locked.StatusCode);
            Assert.Equal("Too many failed logins; try again later.", locked.Status.Detail);
            Assert.Equal(3, _opens);
        }

        // A new handshake is refused as well (the same throttle), and another user is unaffected: it still reaches the database and is rejected there.
        using (var again = new ProxyFlightClient(_address))
            Assert.Equal(StatusCode.Unauthenticated, (await Rpc.ExpectAsync(() => again.AuthenticateAsync("mallory", "wrong-db-password"))).StatusCode);
        Assert.Equal("The login was rejected. [odbc 28000]", (await QueryAsDbUserAsync("carol")).Status.Detail);
        Assert.Equal(4, _opens);
    }

    [Fact]
    public async Task Accepting_a_pass_through_handshake_does_not_clear_recorded_failures()
    {
        // Alternating "login" (accepted without a check) with failing queries must not reset the count: the lockout still arrives on the third failure.
        for (int i = 0; i < 3; i++)
            Assert.Equal("The login was rejected. [odbc 28000]", (await QueryAsDbUserAsync("mallory")).Status.Detail);
        using var client = new ProxyFlightClient(_address);
        Assert.Equal(StatusCode.Unauthenticated, (await Rpc.ExpectAsync(() => client.AuthenticateAsync("mallory", "x"))).StatusCode);
    }

    /// <summary>An ODBC provider whose connections always fail with a login failure (SQLSTATE 28000), counting the attempts.</summary>
    private sealed class RejectingProvider(Action onOpen)
        : DbProviderBase(new EngineId("odbc"), new OdbcComposer(), ReadOnlySession.NoneAvailable("odbc", "test"), new LoginMapper())
    {
        protected override DbConnection NewConnection(string connectionString, ConnectionContext context) => new RejectingConnection(onOpen);
    }

    private sealed class LoginMapper : IFailureMapper
    {
        public ConnectionFailure? Map(Exception exception, EngineId engine) =>
            exception is InvalidOperationException ? new ConnectionFailure(ConnectionFailureKind.Login, "28000", engine) : null;
    }

    private sealed class RejectingConnection(Action onOpen) : DbConnection
    {
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get; set; } = "";
        public override string Database => "";
        public override string DataSource => "";
        public override string ServerVersion => "0";
        public override ConnectionState State => ConnectionState.Closed;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() { }
        public override void Open()
        {
            onOpen();
            throw new InvalidOperationException("Login failed for user; ERROR [28000] with the caller's own words in it");
        }
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
    }
}
