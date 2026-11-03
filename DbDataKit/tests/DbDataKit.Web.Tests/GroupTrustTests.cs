using DbDataKit.State;
using DbDataKit.Web.Trust;
using Xunit;

namespace DbDataKit.Web.Tests;

/// <summary>
/// Plan 20 O04: a group of instances is one server to its clients. Its instance id, paired clients and pairing codes are in the state they share, so a pairing made through one instance holds at every one, a code is
/// redeemed once whichever instance it reaches, and the clients a server's <c>server.yaml</c> listed before it joined a group keep working. Two stores on one state stand for two instances.
/// </summary>
public sealed class GroupTrustTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("group-trust-");
    private readonly StateDatabase _state;
    private readonly TestClock _clock = new();

    public GroupTrustTests() => _state = new StateDatabase(Path.Combine(_directory.FullName, "state.db"), new StateSchema([GroupTrustSchema.Tables]));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _directory.Delete(recursive: true);
    }

    private string ServerYaml(string instance) => Path.Combine(_directory.FullName, instance, "server.yaml");

    private static PairedClient Console(string id) => new(id, "Operations " + id, ConsoleSigningKey.Generate().PublicKey, ServerRole.Viewer | ServerRole.Admin);

    [Fact]
    public void The_group_has_one_instance_id_the_first_instance_s_own()
    {
        var first = ServerIdentity.InstanceId(ServerYaml("a"), "proxy", "dbdataproxy server");
        Assert.Equal(first, new PairingFiles(ServerYaml("a"), Group: _state).InstanceId("proxy", "dbdataproxy server"));   // a server that becomes a group keeps its id, so its pairings hold
        var second = new PairingFiles(ServerYaml("b"), Group: _state);
        ServerIdentity.InstanceId(ServerYaml("b"), "proxy", "dbdataproxy server");   // b had an id of its own before it joined
        Assert.Equal(first, second.InstanceId("proxy", "dbdataproxy server"));
        Assert.NotNull(GroupIdentity.Differs(first, ServerYaml("b")));
        Assert.Null(GroupIdentity.Differs(first, ServerYaml("a")));
    }

    [Fact]
    public void A_pairing_made_through_one_instance_holds_at_another_and_an_unpairing_everywhere()
    {
        var (a, b) = (new PairingFiles(ServerYaml("a"), Group: _state).Clients(), new PairingFiles(ServerYaml("b"), Group: _state).Clients());
        var console = Console("console-1");
        a.Upsert(console);
        Assert.Equal(console.PublicKey, b.Find("console-1")!.PublicKey);
        Assert.Equal(ServerRole.Viewer | ServerRole.Admin, b.Find("console-1")!.Ceiling);

        // paired again (a new key) through b: a sees the new key
        var again = Console("console-1");
        b.Upsert(again);
        Assert.Equal(again.PublicKey, a.Find("console-1")!.PublicKey);
        Assert.Single(a.All());

        Assert.True(a.Remove("console-1"));
        Assert.Null(b.Find("console-1"));
        Assert.False(b.Remove("console-1"));
    }

    [Fact]
    public void A_client_its_server_yaml_listed_before_the_group_keeps_working_and_unpairing_removes_it_from_the_file_too()
    {
        var before = Console("console-old");
        new PairedClientsFile(ServerYaml("a")).Upsert(before);
        var group = new PairingFiles(ServerYaml("a"), Group: _state).Clients();
        Assert.NotNull(group.Find("console-old"));
        Assert.Null(new PairingFiles(ServerYaml("b"), Group: _state).Clients().Find("console-old"));   // the file is that instance's; pairing again through the group makes it the group's

        Assert.True(group.Remove("console-old"));
        Assert.Empty(new PairedClientsFile(ServerYaml("a")).All());
    }

    [Fact]
    public void A_code_made_through_one_instance_is_redeemed_once_through_any()
    {
        var (a, b) = (new StatePairingCodes(_state, _clock), new StatePairingCodes(_state, _clock));
        var code = a.Create(ServerRole.Viewer | ServerRole.Operator, "for the console");
        Assert.Equal(1, b.Pending());

        var grant = b.Redeem(code.Code);
        Assert.Equal((ServerRole.Viewer | ServerRole.Operator, "for the console"), (grant!.Ceiling, grant.Label));
        Assert.Null(a.Redeem(code.Code));
        Assert.Equal(0, a.Pending());

        // redeemed by proof, as a client does over the wire: the proof is checked against the code's key
        var proved = a.Create(ServerRole.Viewer);
        var key = PairingCodes.KeyFor(proved.Code)!;
        Assert.NotNull(b.RedeemProof(candidate => candidate.AsSpan().SequenceEqual(key)));
        Assert.Null(a.RedeemProof(candidate => candidate.AsSpan().SequenceEqual(key)));
    }

    [Fact]
    public void A_code_expires_and_wrong_attempts_at_any_instance_add_up_until_every_code_is_thrown_away()
    {
        var (a, b) = (new StatePairingCodes(_state, _clock), new StatePairingCodes(_state, _clock));
        var late = a.Create(ServerRole.Viewer, lifetime: TimeSpan.FromMinutes(1));
        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(b.Redeem(late.Code));

        var waiting = a.Create(ServerRole.Viewer);
        for (var i = 0; i < PairingCodes.MaxFailures; i++) Assert.Null((i % 2 == 0 ? a : b).Redeem("AAAAA-AAAAA-AAAAA-AAAAA"));
        Assert.Equal(0, a.Pending());
        Assert.Null(b.Redeem(waiting.Code));

        // a good redemption clears the count (one wrong attempt is left from the round above)
        Assert.NotNull(b.Redeem(a.Create(ServerRole.Viewer).Code));
        var next = a.Create(ServerRole.Viewer);
        for (var i = 0; i < PairingCodes.MaxFailures - 1; i++) a.Redeem("AAAAA-AAAAA-AAAAA-AAAAA");
        Assert.NotNull(b.Redeem(next.Code));
        var after = a.Create(ServerRole.Viewer);
        for (var i = 0; i < PairingCodes.MaxFailures - 1; i++) b.Redeem("AAAAA-AAAAA-AAAAA-AAAAA");
        Assert.NotNull(a.Redeem(after.Code));
    }
}
