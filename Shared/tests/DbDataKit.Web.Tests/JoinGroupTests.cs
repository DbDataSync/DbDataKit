using System.Text.Json.Nodes;
using DbDataKit.Web.Trust;
using Xunit;

namespace DbDataKit.Web.Tests;

/// <summary>
/// What a join hands the server about its group, over real HTTPS in one process: the console's groups (<see cref="IJoinGroups"/>) see what the server reported (its
/// listen settings, the key its group holds) and answer with the group's key, the first instance's listen settings and a link, which the server gets back; a group the
/// token's server may not join is refused, told to the server and counted as a refused try.
/// </summary>
public sealed class JoinGroupTests : IAsyncLifetime
{
    private readonly TestClock _clock = new() { Now = DateTimeOffset.UtcNow };
    private readonly MemoryJoinTokenStore _tokens = new();
    private readonly MemoryAudit _audit = new();
    private readonly PendingJoins _pending = new();
    private readonly ConsoleIdentity _console = new("console-test", ConsoleSigningKey.Generate());
    private readonly Groups _groups = new();
    private TestServer _consoleHost = null!, _serverHost = null!;

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The console's groups as the test sets them: what they answer, and the requests they were asked about.</summary>
    private sealed class Groups : IJoinGroups
    {
        public JoinGroupDecision Answer { get; set; } = new(null);

        public List<JoinRequest> Asked { get; } = [];

        public Task<JoinGroupDecision> PrepareAsync(JoinRequest request, JoinTokenRecord token, CancellationToken ct)
        {
            Asked.Add(request);
            return Task.FromResult(Answer);
        }
    }

    public async ValueTask InitializeAsync()
    {
        _consoleHost = await TestServer.StartAsync((app, holder) =>
            app.MapJoin(new JoinService(_console, "Test console", () => holder.Fingerprint, _tokens, new HttpJoinCallback(TimeSpan.FromSeconds(10)), _audit, _clock, groups: _groups)));
        _serverHost = await TestServer.StartAsync((app, _) => app.MapJoinCallback(_pending));
    }

    public async ValueTask DisposeAsync()
    {
        await _consoleHost.DisposeAsync();
        await _serverHost.DisposeAsync();
    }

    private JoinRequestFile Minted(string group = "sales")
    {
        var minted = JoinTokens.Mint(new JoinScope("gateway", ServerRole.Admin, group), "user-7", "Ada", clock: _clock);
        _tokens.Add(minted.Record);
        return JoinRequestFile.From(_consoleHost.Url.ToString(), Thumbprints.Format(_consoleHost.Fingerprint), minted.Token);
    }

    private JoiningServer Self(IReadOnlyDictionary<string, string[]>? listeners = null, string? groupKey = null) =>
        new("gateway", "gateway-1", "Gateway two", _serverHost.Url, _serverHost.Fingerprint, ConsoleSigningKey.Generate(), Listeners: listeners, GroupKey: groupKey);

    [Fact]
    public async Task The_answer_hands_over_the_group_key_the_first_instance_ports_and_the_link()
    {
        var key = GroupCipher.NewKey();
        var held = GroupCipher.NewKey();
        var first = new Dictionary<string, string[]> { ["listen"] = ["https://0.0.0.0:7821"], ["public.listen"] = ["https://0.0.0.0:7820"], ["management.listen"] = ["https://127.0.0.1:7822"] };
        _groups.Answer = new JoinGroupDecision(new JoinGroupHandover(key, first, new JsonObject { ["partner"] = "runners" }));
        var own = new Dictionary<string, string[]> { ["listen"] = ["https://0.0.0.0:7831"] };

        var joined = await JoinClient.JoinAsync(Minted(), Self(own, held), _pending, Ct, clock: _clock, timeout: TimeSpan.FromSeconds(20));

        Assert.Equal(key, joined.GroupKey);
        Assert.Equal(["https://0.0.0.0:7821"], joined.Listeners!["listen"]);
        Assert.Equal(["https://127.0.0.1:7822"], joined.Listeners["management.listen"]);
        Assert.Equal("runners", (string?)joined.Link!["partner"]);
        Assert.Equal("sales", joined.Group);
        // what the server reported reached the console's groups: its own ports and the key its group already holds
        var asked = Assert.Single(_groups.Asked);
        Assert.Equal(["https://0.0.0.0:7831"], asked.Listeners!["listen"]);
        Assert.Equal(held, asked.GroupKey);
        // the key is in no audit entry
        Assert.DoesNotContain(key, System.Text.Json.JsonSerializer.Serialize(_audit.Entries), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_console_with_nothing_to_hand_over_answers_as_before()
    {
        var joined = await JoinClient.JoinAsync(Minted(), Self(), _pending, Ct, clock: _clock, timeout: TimeSpan.FromSeconds(20));

        Assert.Null(joined.GroupKey);
        Assert.Null(joined.Listeners);
        Assert.Null(joined.Link);
    }

    [Fact]
    public async Task A_group_the_server_may_not_join_is_refused_told_and_counted()
    {
        _groups.Answer = JoinGroupDecision.Refuse("the server shares another state database", "The join token was made for group sales, and this server is not an instance of it.");
        var file = Minted();

        var refused = await Assert.ThrowsAsync<JoinFailedException>(() => JoinClient.JoinAsync(file, Self(), _pending, Ct, clock: _clock, timeout: TimeSpan.FromSeconds(20)));

        Assert.True(refused.Refused);
        Assert.Contains("not an instance of it", refused.Message, StringComparison.Ordinal);
        var record = Assert.Single(_tokens.All());
        Assert.Equal(1, record.Tries);
        Assert.Null(record.SpentAt);
        Assert.Contains("another state database", _audit.Entries[^1].Outcome, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_request_whose_group_key_is_not_a_key_is_not_a_join()
    {
        var refused = await Assert.ThrowsAsync<JoinFailedException>(() => JoinClient.JoinAsync(Minted(), Self(groupKey: "not-a-key"), _pending, Ct, clock: _clock, timeout: TimeSpan.FromSeconds(20)));

        Assert.Contains("group key", refused.Message, StringComparison.Ordinal);
        Assert.Empty(_groups.Asked);
    }
}
