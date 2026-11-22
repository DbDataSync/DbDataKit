using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace DbDataKit.Web.Tests;

internal sealed class MemoryAudit : IAuditLog
{
    private readonly ConcurrentQueue<AuditEntry> _entries = new();
    public IReadOnlyList<AuditEntry> Entries => [.. _entries];
    public void Append(AuditEntry entry) => _entries.Enqueue(entry);
    public IReadOnlyList<AuditEntry> Read(int limit = 100) => [.. _entries.TakeLast(limit)];
}

/// <summary>
/// The join over real HTTPS in one process: a console (its join route) and a joining server (its callback route), each on loopback with its own
/// self-signed certificate. Every request either of them receives is recorded whole (path, headers, body), so a test can say none carries the token.
/// </summary>
public sealed class JoinEndToEndTests : IAsyncLifetime
{
    private const string MadeBy = "user-7", MadeByName = "Ada";
    private readonly TestClock _clock = new() { Now = DateTimeOffset.UtcNow };
    private readonly MemoryJoinTokenStore _tokens = new();
    private readonly MemoryAudit _audit = new();
    private readonly PendingJoins _pending = new();
    private readonly ConcurrentQueue<string> _requests = new();
    private readonly ConsoleIdentity _console = new("console-test", ConsoleSigningKey.Generate());
    private TestServer _consoleHost = null!, _serverHost = null!;

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _consoleHost = await TestServer.StartAsync((app, holder) =>
        {
            app.Use(Record);
            app.MapJoin(new JoinService(_console, "Test console", () => holder.Fingerprint, _tokens, new HttpJoinCallback(TimeSpan.FromSeconds(10)), _audit, _clock));
        });
        _serverHost = await TestServer.StartAsync((app, _) =>
        {
            app.Use(Record);
            app.MapJoinCallback(_pending);
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _consoleHost.DisposeAsync();
        await _serverHost.DisposeAsync();
    }

    private async Task Record(HttpContext http, Func<Task> next)
    {
        http.Request.EnableBuffering();
        using var reader = new StreamReader(http.Request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        http.Request.Body.Position = 0;
        var headers = string.Join("\n", http.Request.Headers.Select(h => $"{h.Key}: {h.Value}"));
        _requests.Enqueue($"{http.Request.Method} {http.Request.Path}{http.Request.QueryString}\n{headers}\n\n{body}");
        await next();
    }

    private MintedJoinToken Mint(string product = "runner", string? group = null, ServerRole ceiling = ServerRole.Admin)
    {
        var minted = JoinTokens.Mint(new JoinScope(product, ceiling, group), MadeBy, MadeByName, clock: _clock);
        _tokens.Add(minted.Record);
        return minted;
    }

    private JoinRequestFile File(string token, string? thumbprint = null) =>
        JoinRequestFile.From(_consoleHost.Url.ToString(), Thumbprints.Format(thumbprint ?? _consoleHost.Fingerprint), token);

    private JoiningServer Self(string product = "runner", string? group = null, string? fingerprint = null, Uri? address = null) =>
        new(product, "runner-1", "Runner one", address ?? _serverHost.Url, fingerprint ?? _serverHost.Fingerprint, ConsoleSigningKey.Generate(), group);

    private Task<JoinedConsole> Join(JoinRequestFile file, JoiningServer self) => JoinClient.JoinAsync(file, self, _pending, Ct, clock: _clock, timeout: TimeSpan.FromSeconds(20));

    /// <summary>The token in each spelling a careless request could carry it: as written and in groups of five (the checks ignore case).</summary>
    private static string[] Spellings(string token) => [token, string.Join('-', token.Chunk(5).Select(c => new string(c)))];

    private void AssertNoRequestCarries(string token)
    {
        Assert.NotEmpty(_requests);
        foreach (var request in _requests)
        foreach (var spelling in Spellings(token))
            Assert.DoesNotContain(spelling, request, StringComparison.OrdinalIgnoreCase);
    }

    private void AssertAuditHasNo(string token)
    {
        var text = JsonSerializer.Serialize(_audit.Entries);
        foreach (var spelling in Spellings(token)) Assert.DoesNotContain(spelling, text, StringComparison.OrdinalIgnoreCase);
    }

    private AuditEntry LastAudit() => _audit.Entries[^1];

    private async Task<JoinFailedException> Refused(JoinRequestFile file, JoiningServer self) =>
        await Assert.ThrowsAsync<JoinFailedException>(() => Join(file, self));

    [Fact]
    public async Task A_join_succeeds_once_and_no_request_carries_the_token()
    {
        var minted = Mint(ceiling: ServerRole.Operator, group: "night");
        var self = Self();

        var joined = await Join(File(minted.Token), self);

        Assert.Equal((_console.Id, "Test console", _consoleHost.Fingerprint, "night"), (joined.ConsoleId, joined.Name, joined.CertificateFingerprint, joined.Group));
        Assert.Equal(_console.Key.PublicKey, joined.PublicKey);
        Assert.True(ServerRoles.Allows(joined.Ceiling, ServerRole.Operator));
        Assert.False(ServerRoles.Allows(joined.Ceiling, ServerRole.Admin));
        var paired = joined.AsPairedClient();
        Assert.Equal((ClientKind.Console, _console.Id), (paired.Kind, paired.Id));

        var record = _tokens.Find(minted.Record.Id)!;
        Assert.Equal(JoinTokenState.Spent, record.StateAt(_clock.GetUtcNow()));
        var entry = LastAudit();
        Assert.Equal((JoinService.AuditCommand, "joined", MadeBy, MadeByName, minted.Record.Id), (entry.Command, entry.Outcome, entry.UserId, entry.UserName, entry.TokenId));
        Assert.Contains(_requests, r => r.StartsWith("POST " + ManagementApi.JoinCallbackPath, StringComparison.Ordinal));
        Assert.Contains(_requests, r => r.StartsWith("POST " + JoinEndpoint.Path, StringComparison.Ordinal));

        var again = await Refused(File(minted.Token), Self());
        Assert.True(again.Refused);
        Assert.Contains("already used", again.Message, StringComparison.Ordinal);
        Assert.Equal((MadeBy, minted.Record.Id), (LastAudit().UserId, LastAudit().TokenId));
        Assert.StartsWith("refused: ", LastAudit().Outcome, StringComparison.Ordinal);

        AssertNoRequestCarries(minted.Token);
        AssertAuditHasNo(minted.Token);
        Assert.DoesNotContain(minted.Token, JsonSerializer.Serialize(_tokens.All()), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_expired_token_is_refused_and_audited_with_who_made_it()
    {
        var minted = Mint();
        _clock.Advance(JoinTokens.DefaultLifetime + TimeSpan.FromSeconds(1));

        var refused = await Refused(File(minted.Token), Self());

        Assert.True(refused.Refused);
        Assert.Contains("expired", refused.Message, StringComparison.Ordinal);
        Assert.Equal((MadeBy, minted.Record.Id), (LastAudit().UserId, LastAudit().TokenId));
        Assert.Contains("expired", LastAudit().Outcome, StringComparison.Ordinal);
        AssertNoRequestCarries(minted.Token);
        AssertAuditHasNo(minted.Token);
    }

    [Fact]
    public async Task A_token_for_another_product_is_refused_and_counts_a_try()
    {
        var minted = Mint(product: "gateway");

        var refused = await Refused(File(minted.Token), Self(product: "runner"));

        Assert.True(refused.Refused);
        Assert.Contains("made for a gateway", refused.Message, StringComparison.Ordinal);
        Assert.Equal(1, _tokens.Find(minted.Record.Id)!.Tries);
        Assert.Equal((MadeBy, minted.Record.Id), (LastAudit().UserId, LastAudit().TokenId));
        Assert.Contains("is for a gateway", LastAudit().Outcome, StringComparison.Ordinal);
        AssertNoRequestCarries(minted.Token);
        AssertAuditHasNo(minted.Token);
    }

    [Fact]
    public async Task A_token_for_another_group_is_refused()
    {
        var minted = Mint(group: "sales");

        var refused = await Refused(File(minted.Token), Self(group: "ops"));

        Assert.True(refused.Refused);
        Assert.Contains("group sales", refused.Message, StringComparison.Ordinal);
        Assert.Equal((MadeBy, minted.Record.Id), (LastAudit().UserId, LastAudit().TokenId));
        Assert.Contains("in group ops", LastAudit().Outcome, StringComparison.Ordinal);
        Assert.Equal(JoinTokenState.Waiting, _tokens.Find(minted.Record.Id)!.StateAt(_clock.GetUtcNow()));
        AssertNoRequestCarries(minted.Token);
        AssertAuditHasNo(minted.Token);
    }

    [Fact]
    public async Task A_wrong_console_thumbprint_sends_nothing_and_leaves_the_token_waiting()
    {
        var minted = Mint();
        var other = PairingProof.CertificateFingerprint(TestServer.SelfSigned());

        var refused = await Refused(File(minted.Token, thumbprint: other), Self());

        Assert.False(refused.Refused);
        Assert.Contains(Thumbprints.Format(_consoleHost.Fingerprint), refused.Message, StringComparison.Ordinal);
        Assert.Contains(Thumbprints.Format(other), refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(minted.Token, refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_requests);   // the handshake failed, so the console received no request at all
        Assert.Empty(_audit.Entries);
        Assert.Equal(JoinTokenState.Waiting, _tokens.Find(minted.Record.Id)!.StateAt(_clock.GetUtcNow()));
    }

    [Fact]
    public async Task A_callback_to_an_address_with_another_certificate_is_refused_and_audited()
    {
        var minted = Mint();
        var other = PairingProof.CertificateFingerprint(TestServer.SelfSigned());

        var refused = await Refused(File(minted.Token), Self(fingerprint: other));

        Assert.True(refused.Refused);
        Assert.Contains("could not confirm", refused.Message, StringComparison.Ordinal);
        Assert.Equal((MadeBy, minted.Record.Id), (LastAudit().UserId, LastAudit().TokenId));
        Assert.Contains($"presented the certificate {Thumbprints.Format(_serverHost.Fingerprint)}", LastAudit().Outcome, StringComparison.Ordinal);
        Assert.Equal(1, _tokens.Find(minted.Record.Id)!.Tries);
        Assert.Equal(JoinTokenState.Waiting, _tokens.Find(minted.Record.Id)!.StateAt(_clock.GetUtcNow()));
        AssertNoRequestCarries(minted.Token);
        AssertAuditHasNo(minted.Token);
    }

    [Fact]
    public async Task A_callback_to_an_address_that_does_not_answer_is_refused_and_audited()
    {
        var minted = Mint();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();   // nothing listens there now

        var refused = await Refused(File(minted.Token), Self(address: new Uri($"https://127.0.0.1:{port}")));

        Assert.True(refused.Refused);
        Assert.Equal((MadeBy, minted.Record.Id), (LastAudit().UserId, LastAudit().TokenId));
        Assert.Contains("did not answer", LastAudit().Outcome, StringComparison.Ordinal);
        AssertNoRequestCarries(minted.Token);
        AssertAuditHasNo(minted.Token);
    }

    [Fact]
    public async Task Five_refused_tries_cancel_the_token()
    {
        var minted = Mint(product: "gateway");
        for (var i = 0; i < JoinTokens.MaxTries; i++) await Refused(File(minted.Token), Self(product: "runner"));

        Assert.Equal(JoinTokenState.Cancelled, _tokens.Find(minted.Record.Id)!.StateAt(_clock.GetUtcNow()));
        Assert.Contains("so it is cancelled", LastAudit().Outcome, StringComparison.Ordinal);

        var refused = await Refused(File(minted.Token), Self(product: "gateway"));
        Assert.Contains("cancelled", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_token_the_console_never_made_is_refused_without_saying_why()
    {
        var stranger = JoinTokens.Mint(new JoinScope("runner", ServerRole.Admin), "someone").Token;

        var refused = await Refused(File(stranger), Self());

        Assert.True(refused.Refused);
        Assert.Contains("did not accept the join", refused.Message, StringComparison.Ordinal);
        Assert.Equal(("unknown", ""), (LastAudit().UserId, LastAudit().TokenId));
        AssertNoRequestCarries(stranger);
        AssertAuditHasNo(stranger);
    }
}

/// <summary>The console's half without HTTP: what each request is answered, with the callback replaced.</summary>
public sealed class JoinServiceTests
{
    private readonly TestClock _clock = new();
    private readonly MemoryJoinTokenStore _tokens = new();
    private readonly MemoryAudit _audit = new();
    private readonly ConsoleIdentity _console = new("console-a", ConsoleSigningKey.Generate());
    private readonly ConsoleSigningKey _server = ConsoleSigningKey.Generate();
    private const string ConsoleFingerprint = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ServerFingerprint = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private sealed class Callback(string? answer) : IJoinCallback
    {
        public JoinCallbackCall? Called { get; private set; }

        public Task<string?> ConfirmAsync(JoinCallbackCall call, CancellationToken ct)
        {
            Called = call;
            return Task.FromResult(answer);
        }
    }

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private JoinService Service(IJoinCallback? callback = null, int generation = SuiteCompatibility.Generation) =>
        new(_console, "Console A", () => ConsoleFingerprint, _tokens, callback ?? new Callback(null), _audit, _clock, generation);

    private MintedJoinToken Mint(string product = "runner")
    {
        var minted = JoinTokens.Mint(new JoinScope(product, ServerRole.Admin), "user-1", clock: _clock);
        _tokens.Add(minted.Record);
        return minted;
    }

    private JoinRequest Request(string token, string signedFor = ConsoleFingerprint, int? generation = null)
    {
        var key = JoinTokens.KeyFor(token)!;
        var unsigned = new JoinRequest("join-id-0123456789abcdef", B64(key.PublicKey), null, "runner", null, "runner-1", null,
            "https://runner.example:7840", ServerFingerprint, B64(_server.PublicKey), generation);
        return unsigned with { Proof = B64(key.Sign(JoinProof.Request(signedFor, unsigned))) };
    }

    [Fact]
    public async Task A_proof_made_for_another_console_is_refused_and_counts_a_try()
    {
        var minted = Mint();
        var callback = new Callback(null);

        var outcome = await Service(callback).JoinAsync(Request(minted.Token, signedFor: ServerFingerprint), TestContext.Current.CancellationToken);

        Assert.Equal(403, outcome.Status);
        Assert.Null(callback.Called);
        Assert.Contains("proof does not match", outcome.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("proof", outcome.Told, StringComparison.Ordinal);   // nothing is said to whoever does not hold the token
        Assert.Equal(1, _tokens.Find(minted.Record.Id)!.Tries);
    }

    [Fact]
    public async Task The_callback_is_made_to_the_address_and_certificate_the_join_named_before_the_token_is_spent()
    {
        var minted = Mint();
        var callback = new Callback(null);

        var outcome = await Service(callback).JoinAsync(Request(minted.Token), TestContext.Current.CancellationToken);

        Assert.True(outcome.Ok);
        Assert.Equal(("https://runner.example:7840", ServerFingerprint, ConsoleFingerprint, _console.Id), (callback.Called!.AddressText, callback.Called.CertificateFingerprint, callback.Called.ConsoleFingerprint, callback.Called.ConsoleId));
        Assert.Equal(_server.PublicKey, callback.Called.ServerPublicKey);
        Assert.Equal(("runner-1", "runner-1", "runner", minted.Record.Id, "user-1"), (outcome.Server!.InstanceId, outcome.Server.Name, outcome.Server.Product, outcome.Server.TokenId, outcome.Server.MadeBy));
        Assert.Equal(B64(_console.Key.PublicKey), outcome.Answer!.ConsolePublicKey);
    }

    [Fact]
    public async Task A_server_too_far_apart_in_version_is_refused_before_the_token_is_touched()
    {
        var minted = Mint();

        var outcome = await Service(generation: 5).JoinAsync(Request(minted.Token, generation: 2), TestContext.Current.CancellationToken);

        Assert.Equal(409, outcome.Status);
        Assert.Contains("generations apart", outcome.Told, StringComparison.Ordinal);
        Assert.Equal((0, JoinTokenState.Waiting), (_tokens.Find(minted.Record.Id)!.Tries, _tokens.Find(minted.Record.Id)!.StateAt(_clock.GetUtcNow())));
    }

    [Theory]
    [InlineData("http://runner.example:7840")]
    [InlineData("https://runner.example:7840/path")]
    [InlineData("https://user@runner.example:7840")]
    [InlineData("runner.example:7840")]
    public async Task A_request_whose_address_is_not_an_https_host_and_port_is_not_a_join(string address)
    {
        var minted = Mint();
        var request = Request(minted.Token) with { Address = address };

        var outcome = await Service().JoinAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(400, outcome.Status);
        Assert.Equal(0, _tokens.Find(minted.Record.Id)!.Tries);
        Assert.StartsWith("refused: the request is not a join", _audit.Entries[^1].Outcome, StringComparison.Ordinal);
    }
}

public sealed class JoinTokenTests
{
    [Fact]
    public void A_token_is_128_random_bits_and_the_record_keeps_only_its_verifier()
    {
        var minted = JoinTokens.Mint(new JoinScope("gateway", ServerRole.Admin, "sales"), "user-1");

        Assert.Equal(26, minted.Token.Length);
        Assert.Equal(minted.Token, JoinTokens.Normalize(minted.Token.ToUpperInvariant()));
        Assert.Equal(minted.Token, JoinTokens.Normalize(string.Join('-', minted.Token.Chunk(5).Select(c => new string(c)))));
        Assert.Equal(JoinTokens.VerifierFor(minted.Token), minted.Record.Verifier);
        Assert.DoesNotContain(minted.Token, JsonSerializer.Serialize(minted.Record), StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(minted.Token, JoinTokens.Mint(new JoinScope("gateway", ServerRole.Admin), "user-1").Token);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaa1")]   // 1 is not in the alphabet
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaab")]   // the last two bits must be zero
    public void Text_that_is_not_a_token_has_no_key(string text)
    {
        Assert.Null(JoinTokens.Normalize(text));
        Assert.Null(JoinTokens.KeyFor(text));
    }

    [Fact]
    public void A_token_lives_fifteen_minutes_by_default_and_at_most_a_day()
    {
        var clock = new TestClock();
        var minted = JoinTokens.Mint(new JoinScope("runner", ServerRole.Viewer), "user-1", clock: clock);
        Assert.Equal(clock.Now + TimeSpan.FromMinutes(15), minted.Record.ExpiresAt);
        Assert.Equal(clock.Now + TimeSpan.FromHours(24), JoinTokens.Mint(new JoinScope("runner", ServerRole.Viewer), "user-1", lifetime: TimeSpan.FromHours(24), clock: clock).Record.ExpiresAt);
        Assert.Throws<ArgumentException>(() => JoinTokens.Mint(new JoinScope("runner", ServerRole.Viewer), "user-1", lifetime: TimeSpan.FromHours(25)));
        Assert.Throws<ArgumentException>(() => JoinTokens.Mint(new JoinScope("Runner!", ServerRole.Viewer), "user-1"));
        Assert.Throws<ArgumentException>(() => JoinTokens.Mint(new JoinScope("runner", ServerRole.None), "user-1"));
    }

    [Fact]
    public void Ended_tokens_are_removed_and_waiting_ones_kept()
    {
        var clock = new TestClock();
        var store = new MemoryJoinTokenStore();
        var waiting = JoinTokens.Mint(new JoinScope("runner", ServerRole.Admin), "u", clock: clock).Record;
        var spent = JoinTokens.Mint(new JoinScope("runner", ServerRole.Admin), "u", clock: clock).Record with { SpentAt = clock.Now };
        store.Add(waiting);
        store.Add(spent);

        Assert.Equal(1, store.RemoveEndedBefore(clock.Now + TimeSpan.FromMinutes(1)));
        Assert.Equal([waiting.Id], store.All().Select(r => r.Id));
    }

    [Theory]
    [InlineData("sha256:" + "AB12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12", "ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12")]
    [InlineData("ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12", null)]
    [InlineData("sha256:ab12", null)]
    [InlineData("sha1:ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12", null)]
    public void A_thumbprint_is_sha256_and_64_hex_digits(string text, string? expected) => Assert.Equal(expected, Thumbprints.Parse(text));
}

public sealed class JoinRequestFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("join-request").FullName;
    private const string Thumbprint = "sha256:ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12";

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string PathOf() => Path.Combine(_dir, JoinRequestFile.FileName);

    [Fact]
    public void A_request_is_written_for_its_owner_only_and_read_back()
    {
        var token = JoinTokens.Mint(new JoinScope("runner", ServerRole.Admin), "u").Token;
        var request = JoinRequestFile.From("https://console:7800", Thumbprint, token.ToUpperInvariant(), "https://runner:7841");

        request.Write(PathOf());

        Assert.Equal(request, JoinRequestFile.Read(PathOf()));
        Assert.Contains("schema: join-request/1", File.ReadAllText(PathOf()), StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(PathOf()));
        JoinRequestFile.Delete(PathOf());
        Assert.Null(JoinRequestFile.Read(PathOf()));
    }

    [Theory]
    [InlineData("schema: join-request/2\n", "is join-request/2; this program reads join-request/1")]
    [InlineData("console: https://console:7800\n", "has no 'schema'")]
    [InlineData("schema: join-request/1\nconsole: https://console:7800\nconsole_thumbprint: " + Thumbprint + "\ntoken: x\ngroup: sales\n", "has the key 'group'")]
    [InlineData("schema: join-request/1\nconsole: https://console:7800\nconsole_thumbprint: " + Thumbprint + "\n", "has no 'token'")]
    [InlineData("schema: join-request/1\nconsole: http://console:7800\nconsole_thumbprint: " + Thumbprint + "\ntoken: x\n", "must start with https://")]
    public void A_file_that_cannot_be_used_is_refused_naming_it(string text, string says)
    {
        File.WriteAllText(PathOf(), text);

        var refused = Assert.Throws<JoinRequestFileException>(() => JoinRequestFile.Read(PathOf()));

        Assert.Contains(PathOf(), refused.Message, StringComparison.Ordinal);
        Assert.Contains(says, refused.Message, StringComparison.Ordinal);
    }
}
