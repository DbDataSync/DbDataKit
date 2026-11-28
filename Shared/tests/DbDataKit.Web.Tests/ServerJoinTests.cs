using DbDataKit.Cli;
using DbDataKit.State;
using DbDataKit.State.Tests;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace DbDataKit.Web.Tests;

/// <summary>
/// The console's join tokens as rows of the <c>trust</c> module, on every engine: a record reads back as it was written, a change to one token is made
/// once even when two are made at the same moment, and ended tokens go.
/// </summary>
public sealed class JoinTokenStoreTests : IDisposable
{
    private readonly StateEngines _engines = new();
    private readonly TestClock _clock = new();

    public void Dispose() => _engines.Dispose();

    private StateJoinTokenStore Open(StateEngines.Location location) => new(location.Open([TrustModule.Module], new StateScope(Group: "console")));

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_token_reads_back_by_id_and_by_verifier_and_keeps_no_token(string engine)
    {
        var location = _engines.Provision(engine);
        var store = Open(location);
        var minted = JoinTokens.Mint(new JoinScope("runner", ServerRole.Operator, "night"), "user-7", "Ada", clock: _clock);
        store.Add(minted.Record);

        var found = store.Find(minted.Record.Id)!;
        Assert.Equal((minted.Record.Verifier, "runner", "night", "user-7", "Ada", 0), (found.Verifier, found.Product, found.Group, found.MadeBy, found.MadeByName, found.Tries));
        Assert.True(ServerRoles.Allows(found.Ceiling, ServerRole.Operator));
        Assert.Equal(minted.Record.ExpiresAt.ToUnixTimeSeconds(), found.ExpiresAt.ToUnixTimeSeconds());
        Assert.Equal(minted.Record.Id, store.FindByVerifier(minted.Record.Verifier)!.Id);
        Assert.Null(store.FindByVerifier("nothing-like-it"));
        Assert.Single(store.All());
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void Two_consoles_on_one_database_spend_a_token_once(string engine)
    {
        var location = _engines.Provision(engine);
        var (a, b) = (Open(location), Open(location));
        var minted = JoinTokens.Mint(new JoinScope("gateway", ServerRole.Admin), "user-7", clock: _clock);
        a.Add(minted.Record);

        var spent = 0;
        Parallel.For(0, 8, i =>
        {
            (i % 2 == 0 ? a : b).Update(minted.Record.Id, r =>
            {
                if (r.SpentAt is not null) return r;
                Interlocked.Increment(ref spent);
                return r with { SpentAt = _clock.GetUtcNow() };
            });
        });

        // a change whose row moved under it is made again on what is there, so a later attempt sees the token spent
        Assert.True(spent >= 1);
        Assert.NotNull(b.Find(minted.Record.Id)!.SpentAt);
        var tries = a.Update(minted.Record.Id, r => r with { Tries = r.Tries + 1 })!;
        Assert.Equal(1, tries.Tries);
        Assert.Equal(1, b.Find(minted.Record.Id)!.Tries);
        Assert.Null(a.Update("jt-none", r => r));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void Tokens_that_ended_before_a_time_are_removed_and_waiting_ones_are_kept(string engine)
    {
        var store = Open(_engines.Provision(engine));
        var spent = JoinTokens.Mint(new JoinScope("runner", ServerRole.Admin), "u", clock: _clock);
        var waiting = JoinTokens.Mint(new JoinScope("runner", ServerRole.Admin), "u", lifetime: TimeSpan.FromHours(24), clock: _clock);
        store.Add(spent.Record with { SpentAt = _clock.GetUtcNow() });
        store.Add(waiting.Record);

        Assert.Equal(1, store.RemoveEndedBefore(_clock.GetUtcNow().AddHours(1)));
        Assert.Equal(waiting.Record.Id, Assert.Single(store.All()).Id);
    }

    [Fact]
    public void The_module_keeps_its_conventions() => Assert.Empty(StateConventions.Violations(TrustModule.Module));
}

/// <summary>
/// The server's join step and the <c>pair</c> command: the command writes the request, the step joins the console it names over real HTTPS, keeps the
/// console as a paired client and its certificate's thumbprint, and deletes the request; a refused join keeps the request and says what the console said.
/// </summary>
public sealed class ServerJoinStepTests : IAsyncLifetime
{
    private readonly TestClock _clock = new() { Now = DateTimeOffset.UtcNow };
    private readonly MemoryJoinTokenStore _tokens = new();
    private readonly MemoryAudit _audit = new();
    private readonly PendingJoins _pending = new();
    private readonly ConsoleIdentity _console = new("console-test", ConsoleSigningKey.Generate());
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("server-join-");
    private readonly MemorySettings _settings = new();
    private TestServer _consoleHost = null!, _serverHost = null!;

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _consoleHost = await TestServer.StartAsync((app, holder) =>
            app.MapJoin(new JoinService(_console, "Test console", () => holder.Fingerprint, _tokens, new HttpJoinCallback(TimeSpan.FromSeconds(10)), _audit, _clock)));
        _serverHost = await TestServer.StartAsync((app, _) => app.MapJoinCallback(_pending));
    }

    public async ValueTask DisposeAsync()
    {
        await _consoleHost.DisposeAsync();
        await _serverHost.DisposeAsync();
        _folder.Delete(recursive: true);
    }

    private sealed class MemorySettings : IGroupSettings
    {
        private readonly Dictionary<string, string> _values = [];
        public string? Get(string name) => _values.GetValueOrDefault(name);
        public string Put(string name, string value, string? expected = null)
        {
            if (!_values.TryGetValue(name, out var was) || was == expected) _values[name] = value;
            return _values[name];
        }
    }

    private string Mint()
    {
        var minted = JoinTokens.Mint(new JoinScope("runner", ServerRole.Operator), "user-7", "Ada", clock: _clock);
        _tokens.Add(minted.Record);
        return minted.Token;
    }

    private async Task<(int Exit, string Out, string Error)> Pair(params string[] args)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        var options = new PairCommandOptions("dbdatarunner", _ => _folder.FullName, _ => Task.FromResult(new ServiceRestartResult(false)));
        var router = new NounRouter("dbdatarunner", [JoinPairCommand.Create(options)]);
        var exit = await router.RunAsync(["pair", .. args], new CliContext(output, error, new StringReader(""), Ct));
        return (exit, output.ToString(), error.ToString());
    }

    private Task<JoinedConsole?> Step(PairedClientsFile clients) => ServerJoin.RunAsync(_folder.FullName,
        () => new JoiningServer("runner", "runner-1", "Runner one", _serverHost.Url, _serverHost.Fingerprint, ConsoleSigningKey.Generate()),
        _pending, clients, new JoinedConsoles(_settings), Ct, TimeSpan.FromSeconds(20));

    [Fact]
    public async Task The_printed_command_writes_the_request_and_the_step_joins_keeps_the_console_and_deletes_the_request()
    {
        var token = Mint();
        var (exit, output, error) = await Pair("--host", _consoleHost.Url.ToString(), "--token", token, "--console-thumbprint", Thumbprints.Format(_consoleHost.Fingerprint));
        Assert.True(exit == 0, error);
        Assert.Contains("Start the server to join: dbdatarunner server run", output);
        Assert.True(File.Exists(ServerJoin.RequestPath(_folder.FullName)));

        var clients = new PairedClientsFile(Path.Combine(_folder.FullName, "server.yaml"));
        var joined = await Step(clients);

        Assert.Equal(_console.Id, joined!.ConsoleId);
        var paired = clients.Find(_console.Id)!;
        Assert.Equal(ClientKind.Console, paired.Kind);
        Assert.Equal(_console.Key.PublicKey, paired.PublicKey);
        Assert.True(ServerRoles.Allows(paired.Ceiling, ServerRole.Operator));
        var pin = new JoinedConsoles(_settings).Find(_console.Id)!;
        Assert.Equal((_consoleHost.Fingerprint, _consoleHost.Url.GetLeftPart(UriPartial.Authority)), (pin.CertificateFingerprint, pin.Url.GetLeftPart(UriPartial.Authority)));
        Assert.False(File.Exists(ServerJoin.RequestPath(_folder.FullName)));
        Assert.Null(await Step(clients));   // no request: nothing to join
    }

    [Fact]
    public async Task A_spent_token_is_refused_the_request_stays_and_the_failure_names_the_console_and_its_answer()
    {
        var token = Mint();
        var clients = new PairedClientsFile(Path.Combine(_folder.FullName, "server.yaml"));
        await Pair("--host", _consoleHost.Url.ToString(), "--token", token, "--console-thumbprint", Thumbprints.Format(_consoleHost.Fingerprint));
        await Step(clients);
        await Pair("--host", _consoleHost.Url.ToString(), "--token", token, "--console-thumbprint", Thumbprints.Format(_consoleHost.Fingerprint));

        var refused = await Assert.ThrowsAsync<ServerJoinException>(() => Step(clients));

        Assert.Equal(_consoleHost.Url.GetLeftPart(UriPartial.Authority), refused.Console);
        Assert.Contains("already used", refused.Answer);
        Assert.True(File.Exists(ServerJoin.RequestPath(_folder.FullName)));
        var failure = DbDataKit.Hosting.Startup.StartupStepException.JoinFailed(refused.Console, refused.Answer, refused.Message, refused);
        Assert.Equal(DbDataKit.Hosting.Startup.StartupStep.Join, failure.Step);
        Assert.Contains("answered: The join token was already used", failure.Message);
    }

    [Fact]
    public async Task What_the_server_cannot_keep_of_its_group_fails_the_step_and_the_spent_request_goes()
    {
        var token = Mint();
        var clients = new PairedClientsFile(Path.Combine(_folder.FullName, "server.yaml"));
        await Pair("--host", _consoleHost.Url.ToString(), "--token", token, "--console-thumbprint", Thumbprints.Format(_consoleHost.Fingerprint));

        var failed = await Assert.ThrowsAsync<ServerJoinException>(() => ServerJoin.RunAsync(_folder.FullName,
            () => new JoiningServer("runner", "runner-1", "Runner one", _serverHost.Url, _serverHost.Fingerprint, ConsoleSigningKey.Generate()),
            _pending, clients, new JoinedConsoles(_settings), Ct, TimeSpan.FromSeconds(20), keep: _ => throw new InvalidOperationException("The group's values are protected with another key.")));

        Assert.Contains("accepted the join, but this server could not keep", failed.Message);
        Assert.Contains("another key", failed.Message);
        Assert.IsType<InvalidOperationException>(failed.InnerException);
        Assert.False(File.Exists(ServerJoin.RequestPath(_folder.FullName)));
        Assert.Null(clients.Find(_console.Id));
    }

    [Fact]
    public async Task A_console_that_cannot_be_reached_fails_the_step_without_an_answer()
    {
        var token = Mint();
        await Pair("--host", "https://127.0.0.1:1", "--token", token, "--console-thumbprint", Thumbprints.Format(_consoleHost.Fingerprint));
        var refused = await Assert.ThrowsAsync<ServerJoinException>(() => Step(new PairedClientsFile(Path.Combine(_folder.FullName, "server.yaml"))));
        Assert.Null(refused.Answer);
        var failure = DbDataKit.Hosting.Startup.StartupStepException.JoinFailed(refused.Console, refused.Answer, refused.Message, refused);
        Assert.Contains("could not be reached", failure.Message);
    }

    [Fact]
    public async Task The_command_reads_the_token_from_standard_input_and_refuses_what_is_not_a_command_the_console_printed()
    {
        var token = Mint();
        var (output, error) = (new StringWriter(), new StringWriter());
        var router = new NounRouter("dbdatarunner", [JoinPairCommand.Create(new PairCommandOptions("dbdatarunner", _ => _folder.FullName, _ => Task.FromResult(new ServiceRestartResult(false))))]);
        var exit = await router.RunAsync(["pair", "--host", _consoleHost.Url.ToString(), "--token", "-", "--console-thumbprint", Thumbprints.Format(_consoleHost.Fingerprint)],
            new CliContext(output, error, new StringReader(token + "\n"), Ct));
        Assert.True(exit == 0, error.ToString());
        Assert.Equal(token, JoinRequestFile.Read(ServerJoin.RequestPath(_folder.FullName))!.Token);

        Assert.Equal(CliExit.Usage, (await Pair("--host", _consoleHost.Url.ToString(), "--token", "not-a-token", "--console-thumbprint", Thumbprints.Format(_consoleHost.Fingerprint))).Exit);
        Assert.Equal(CliExit.Usage, (await Pair("--host", "http://console:7800", "--token", token, "--console-thumbprint", Thumbprints.Format(_consoleHost.Fingerprint))).Exit);
        var (usage, _, said) = await Pair("--token", token);
        Assert.Equal(CliExit.Usage, usage);
        Assert.Contains("Usage: dbdatarunner pair --host", said);
    }

    [Fact]
    public async Task A_restarted_service_that_does_not_join_in_time_is_reported_and_a_failed_restart_says_why()
    {
        var token = Mint();
        async Task<(int, string)> With(ServiceRestartResult restart)
        {
            var error = new StringWriter();
            var router = new NounRouter("dbdatarunner", [JoinPairCommand.Create(new PairCommandOptions("dbdatarunner", _ => _folder.FullName, _ => Task.FromResult(restart)) { JoinWait = TimeSpan.FromSeconds(1) })]);
            var exit = await router.RunAsync(["pair", "--host", _consoleHost.Url.ToString(), "--token", token, "--console-thumbprint", Thumbprints.Format(_consoleHost.Fingerprint)],
                new CliContext(new StringWriter(), error, new StringReader(""), Ct));
            return (exit, error.ToString());
        }

        var (late, lateSaid) = await With(new ServiceRestartResult(true));
        Assert.Equal(CliExit.Failure, late);
        Assert.Contains("dbdatarunner service check", lateSaid);
        var (failed, failedSaid) = await With(new ServiceRestartResult(true, "access is denied."));
        Assert.Equal(CliExit.Failure, failed);
        Assert.Contains("access is denied.", failedSaid);
    }

    [Fact]
    public void A_server_listening_on_every_address_is_called_at_this_machine_s_name()
    {
        Assert.Equal(System.Net.Dns.GetHostName().ToLowerInvariant(), ServerJoin.AddressFor(new Uri("https://0.0.0.0:7841")).Host.ToLowerInvariant());
        Assert.Equal("https://runner1.example.com:7841/", ServerJoin.AddressFor(new Uri("https://runner1.example.com:7841/")).AbsoluteUri);
    }
}
