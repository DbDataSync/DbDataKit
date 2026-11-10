using DbDataKit.Identity;
using DbDataKit.State;
using DbDataGateway.Client;
using DbDataGateway.Data;
using DbDataGateway.Server.Hosting;
using DbDataGateway.Tests.Support;
using Grpc.Core;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Logging;

namespace DbDataGateway.Tests.Flight;

/// <summary>
/// Flight SQL with the identity home's people (plan 13a P13): a home user name with a personal access token, a home bearer token, a person outside the named group, and the audit that names the home user. The host is the real one, in-process,
/// with the home's state next to it; Windows is covered with a fake logon in <c>HomeIdentityTests</c> (the operating system's own check runs on the Windows CI job).
/// </summary>
public sealed class HomeSignInTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbgateway-homesign-" + Guid.NewGuid().ToString("N"));
    private readonly CaptureLoggerProvider _logs = new();
    private WebApplication? _app;
    private string _address = "";
    private StateDatabase _state = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        var settings = new Dictionary<string, string?>
        {
            ["Gateway:Listen:Address"] = "127.0.0.1",
            ["Gateway:Listen:Port"] = "0",
            ["Gateway:Tls:Mode"] = "None",
            ["Gateway:Management:Enabled"] = "false",   // the management API is https only and on by default (plan 33 D15); not the subject here
            ["Gateway:DataDirectory"] = _dir,
            ["Gateway:Compression:Mode"] = "None",
            ["Gateway:DataSources:erp:Provider"] = "duckdb",
            ["Gateway:DataSources:erp:ConnectionString"] = "Data Source=:memory:",
            ["Gateway:DataSources:erp:Classifier"] = "off",
            ["Gateway:DataSources:erp:Groups:0"] = "finance",
            ["Gateway:DataSources:lake:Provider"] = "duckdb",
            ["Gateway:DataSources:lake:ConnectionString"] = "Data Source=:memory:",
            ["Gateway:DataSources:lake:Classifier"] = "off",
            ["Gateway:DataSources:lake:Groups:0"] = "analysts",
            ["Gateway:DataSources:closed:Provider"] = "duckdb",
            ["Gateway:DataSources:closed:ConnectionString"] = "Data Source=:memory:",
        };
        _app = GatewayHost.Build([], b =>
        {
            b.Configuration.AddInMemoryCollection(settings);
            b.Logging.SetMinimumLevel(LogLevel.Trace);
            b.Logging.AddProvider(_logs);
        });
        await _app.StartAsync();
        _address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().Replace("0.0.0.0", "127.0.0.1");
        _state = _app.Services.GetRequiredService<StateDatabase>();
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static async Task<string> FirstValueAsync(GatewayClient client, string sql, string source)
    {
        var result = await client.Raw.QueryAsync(sql, new QueryOptions { DataSource = source });
        await foreach (var batch in result)
            if (batch.Length > 0) return ArrowText.Get(batch.Column(0), 0) ?? "";
        return "";
    }

    [Fact]
    public async Task A_home_user_name_with_a_personal_access_token_signs_in_and_the_audit_names_the_home_user()
    {
        var (ada, token) = HomeSeed.Person(_state, "Ada Lovelace", "ada@example.invalid", "finance");

        using var client = new GatewayClient(_address);
        await client.AuthenticateAsync("ada@example.invalid", token);
        Assert.StartsWith("Bearer ", client.Bearer);
        Assert.Equal("42", await FirstValueAsync(client, "SELECT 42", "erp"));

        // the audit: the log of the handshake and of the query, and the session the server lists, name the home user, not the address she typed or the token
        Assert.Contains(_logs.Lines, l => l.Text.Contains("Handshake accepted for Ada Lovelace"));
        Assert.Contains(_logs.Lines, l => l.Text.Contains("Query by Ada Lovelace on erp"));
        var sessions = _app!.Services.GetRequiredService<DbDataGateway.Server.Auth.SessionStore>();
        var session = Assert.Single(sessions.List(), s => s.User == "Ada Lovelace");
        Assert.Equal("home-user", session.Method);
        Assert.Equal(["erp"], session.DataSources);
        Assert.DoesNotContain(_logs.Lines, l => l.Text.Contains(token));
        Assert.Equal(ada.Id, sessions.TryGet(client.Bearer!["Bearer ".Length..])!.HomeUserId);
    }

    [Fact]
    public async Task A_service_account_signs_in_with_its_name_and_a_home_bearer_token_works_without_a_handshake()
    {
        var token = HomeSeed.ServiceAccount(_state, "nightly-load", "analysts");

        using (var byName = new GatewayClient(_address))
        {
            await byName.AuthenticateAsync("nightly-load", token);
            Assert.Equal("7", await FirstValueAsync(byName, "SELECT 7", "lake"));
        }

        // the token alone, as an API client sends it: no handshake, the call is signed in as that account
        using var bearer = new GatewayClient(_address);
        bearer.UseHeader("Bearer " + token);
        Assert.Equal("8", await FirstValueAsync(bearer, "SELECT 8", "lake"));
        Assert.Contains(_logs.Lines, l => l.Text.Contains("Query by nightly-load on lake"));
    }

    [Fact]
    public async Task A_revoked_home_bearer_token_stops_working_at_once()
    {
        var (ada, token) = HomeSeed.Person(_state, "Ada Lovelace", "ada@example.invalid", "analysts");
        using var client = new GatewayClient(_address);
        client.UseHeader("Bearer " + token);
        Assert.Equal("1", await FirstValueAsync(client, "SELECT 1", "lake"));

        var tokens = new AccessTokens(_state, new IdentityUserStore(_state));
        Assert.True(tokens.Revoke(tokens.List(ada.Id).Single().Id));

        var refused = await Rpc.ExpectAsync(async () => await FirstValueAsync(client, "SELECT 1", "lake"));
        Assert.Equal(StatusCode.Unauthenticated, refused.StatusCode);
    }

    [Fact]
    public async Task A_person_not_in_the_named_group_is_refused_naming_the_data_source_and_a_closed_source_is_used_by_nobody()
    {
        var (_, token) = HomeSeed.Person(_state, "Bob Babbage", "bob@example.invalid", "analysts");
        using var client = new GatewayClient(_address);
        await client.AuthenticateAsync("bob@example.invalid", token);

        var notYours = await Rpc.ExpectAsync(async () => await FirstValueAsync(client, "SELECT 1", "erp"));
        Assert.Equal(StatusCode.PermissionDenied, notYours.StatusCode);
        Assert.Contains("'erp'", notYours.Status.Detail);
        Assert.Contains("finance", notYours.Status.Detail);

        var closed = await Rpc.ExpectAsync(async () => await FirstValueAsync(client, "SELECT 1", "closed"));
        Assert.Equal(StatusCode.PermissionDenied, closed.StatusCode);
        Assert.Contains("'closed'", closed.Status.Detail);

        Assert.Equal("1", await FirstValueAsync(client, "SELECT 1", "lake"));   // his own group's source is fine
    }

    [Fact]
    public async Task A_wrong_token_and_a_password_that_is_nothing_are_refused_at_the_handshake()
    {
        HomeSeed.Person(_state, "Ada Lovelace", "ada@example.invalid", "finance");
        using var client = new GatewayClient(_address);

        Assert.Equal(StatusCode.Unauthenticated, (await Rpc.ExpectAsync(() => client.AuthenticateAsync("ada@example.invalid", "ddk_wrong_token"))).StatusCode);
        Assert.Equal(StatusCode.Unauthenticated, (await Rpc.ExpectAsync(() => client.AuthenticateAsync("ada@example.invalid", "a-password"))).StatusCode);   // Gateway has no passwords of its own
        Assert.Equal(StatusCode.Unauthenticated, (await Rpc.ExpectAsync(() => client.AuthenticateAsync("alice", "alice-pw"))).StatusCode);
    }
}
