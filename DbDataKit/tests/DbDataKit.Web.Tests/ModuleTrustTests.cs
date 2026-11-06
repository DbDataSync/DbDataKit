using DbDataKit.State;
using DbDataKit.State.Tests;
using DbDataKit.Web.Trust;
using Xunit;

namespace DbDataKit.Web.Tests;

/// <summary>
/// Plan 29a Q10: a group of instances is one server to its clients, on the <c>trust</c> module and on every engine. The older store's tests, carried over: two stores on one database stand for
/// two instances, a pairing made through one holds at another, a code is redeemed once whichever instance it reaches, and wrong attempts add up across instances.
/// </summary>
public sealed class ModuleTrustTests : IDisposable
{
    private readonly StateEngines _engines = new();
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("module-trust-");
    private readonly StateTestClock _clock = new(DateTimeOffset.Parse("2026-11-06T08:00:00Z"));

    public void Dispose()
    {
        _engines.Dispose();
        _directory.Delete(recursive: true);
    }

    private StateDatabase Open(string engine) => _engines.Provision(engine).Open([TrustModule.Module], new StateScope(Group: "main"), time: _clock);

    private string ServerYaml(string instance) => Path.Combine(_directory.FullName, instance, "server.yaml");

    private static PairedClient Console(string id) => new(id, "Operations " + id, ConsoleSigningKey.Generate().PublicKey, ServerRole.Viewer | ServerRole.Admin);

    [Fact]
    public void The_module_follows_the_conventions() => Assert.Empty(StateConventions.Violations(TrustModule.Module));

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void The_group_has_one_instance_id_the_first_instance_s_own(string engine)
    {
        var settings = new ModuleGroupSettings(Open(engine));
        var first = ServerIdentity.InstanceId(ServerYaml("a"), "proxy", "dbdataproxy server");

        Assert.Equal(first, GroupIdentity.InstanceId(settings, ServerYaml("a"), "proxy", "dbdataproxy server"));   // a server that becomes a group keeps its id, so its pairings hold
        ServerIdentity.InstanceId(ServerYaml("b"), "proxy", "dbdataproxy server");                                 // b had an id of its own before it joined
        Assert.Equal(first, GroupIdentity.InstanceId(settings, ServerYaml("b"), "proxy", "dbdataproxy server"));
        Assert.NotNull(GroupIdentity.Differs(first, ServerYaml("b")));
        Assert.Null(GroupIdentity.Differs(first, ServerYaml("a")));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_setting_is_written_once_and_replaced_only_while_it_is_still_the_value_read(string engine)
    {
        var location = _engines.Provision(engine);   // two instances: one database
        var (a, b) = (new ModuleGroupSettings(location.Open([TrustModule.Module], new StateScope(Group: "main"))), new ModuleGroupSettings(location.Open([TrustModule.Module], new StateScope(Group: "main"))));

        Assert.Null(a.Get("certificate"));
        Assert.Equal("one", a.Put("certificate", "one"));
        Assert.Equal("one", b.Put("certificate", "two"));                       // another instance wrote first: its value is the group's
        Assert.Equal("one", b.Put("certificate", "three", expected: "stale"));  // not the value read: nothing changes
        Assert.Equal("three", b.Put("certificate", "three", expected: "one"));
        Assert.Equal("three", a.Get("certificate"));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_pairing_made_through_one_instance_holds_at_another_and_an_unpairing_everywhere(string engine)
    {
        var location = _engines.Provision(engine);
        var modules = new[] { TrustModule.Module };
        var (a, b) = (new ModulePairedClients(location.Open(modules, new StateScope(Group: "main"))), new ModulePairedClients(location.Open(modules, new StateScope(Group: "main"))));
        var console = Console("console-1");
        a.Upsert(console);
        Assert.Equal(console.PublicKey, b.Find("console-1")!.PublicKey);
        Assert.Equal(ServerRole.Viewer | ServerRole.Admin, b.Find("console-1")!.Ceiling);
        Assert.Equal(ClientKind.Console, b.Find("console-1")!.Kind);

        var again = Console("console-1");           // paired again (a new key) through b: a sees the new key
        b.Upsert(again);
        Assert.Equal(again.PublicKey, a.Find("console-1")!.PublicKey);
        Assert.Single(a.All());

        Assert.True(a.Remove("console-1"));
        Assert.Null(b.Find("console-1"));
        Assert.False(b.Remove("console-1"));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_client_its_server_yaml_listed_before_the_group_keeps_working_and_unpairing_removes_it_from_the_file_too(string engine)
    {
        var state = Open(engine);
        new PairedClientsFile(ServerYaml("a")).Upsert(Console("console-old"));
        var group = new ModulePairedClients(state, new PairedClientsFile(ServerYaml("a")));

        Assert.NotNull(group.Find("console-old"));
        Assert.Null(new ModulePairedClients(state, new PairedClientsFile(ServerYaml("b"))).Find("console-old"));   // the file is that instance's
        Assert.True(group.Remove("console-old"));
        Assert.Empty(new PairedClientsFile(ServerYaml("a")).All());
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_code_made_through_one_instance_is_redeemed_once_through_any(string engine)
    {
        var state = Open(engine);
        var (a, b) = (new ModulePairingCodes(state), new ModulePairingCodes(state));
        var code = a.Create(ServerRole.Viewer | ServerRole.Operator, "for the console");
        Assert.Equal(1, b.Pending());

        var grant = b.Redeem(code.Code);
        Assert.Equal((ServerRole.Viewer | ServerRole.Operator, "for the console"), (grant!.Ceiling, grant.Label));
        Assert.Null(a.Redeem(code.Code));
        Assert.Equal(0, a.Pending());

        var proved = a.Create(ServerRole.Viewer);       // redeemed by proof, as a client does over the wire
        var key = PairingCodes.KeyFor(proved.Code)!;
        Assert.NotNull(b.RedeemProof(candidate => candidate.AsSpan().SequenceEqual(key)));
        Assert.Null(a.RedeemProof(candidate => candidate.AsSpan().SequenceEqual(key)));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void Of_instances_redeeming_one_code_at_once_one_wins(string engine)
    {
        var state = Open(engine);
        var code = new ModulePairingCodes(state).Create(ServerRole.Viewer);
        var instances = Enumerable.Range(0, 8).Select(_ => new ModulePairingCodes(state)).ToArray();

        var wins = instances.AsParallel().WithDegreeOfParallelism(8).Count(i => i.Redeem(code.Code) is not null);

        Assert.Equal(1, wins);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_code_expires_and_wrong_attempts_at_any_instance_add_up_until_every_code_is_thrown_away(string engine)
    {
        var state = Open(engine);
        var (a, b) = (new ModulePairingCodes(state), new ModulePairingCodes(state));
        var late = a.Create(ServerRole.Viewer, lifetime: TimeSpan.FromMinutes(1));
        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(b.Redeem(late.Code));

        var waiting = a.Create(ServerRole.Viewer);
        for (var i = 0; i < PairingCodes.MaxFailures; i++) Assert.Null((i % 2 == 0 ? a : b).Redeem("AAAAA-AAAAA-AAAAA-AAAAA"));
        Assert.Equal(0, a.Pending());
        Assert.Null(b.Redeem(waiting.Code));

        Assert.NotNull(b.Redeem(a.Create(ServerRole.Viewer).Code));   // a good redemption clears the count (one wrong attempt is left from the round above)
        var next = a.Create(ServerRole.Viewer);
        for (var i = 0; i < PairingCodes.MaxFailures - 1; i++) a.Redeem("AAAAA-AAAAA-AAAAA-AAAAA");
        Assert.NotNull(b.Redeem(next.Code));
        var after = a.Create(ServerRole.Viewer);
        for (var i = 0; i < PairingCodes.MaxFailures - 1; i++) b.Redeem("AAAAA-AAAAA-AAAAA-AAAAA");
        Assert.NotNull(a.Redeem(after.Code));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void Two_groups_in_one_database_have_their_own_trust(string engine)
    {
        var location = _engines.Provision(engine);
        var main = new ModulePairedClients(location.Open([TrustModule.Module], new StateScope(Group: "main")));
        var eu = new ModulePairedClients(location.Open([TrustModule.Module], new StateScope(Group: "eu")));

        main.Upsert(Console("console-1"));

        Assert.NotNull(main.Find("console-1"));
        Assert.Null(eu.Find("console-1"));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void The_retention_job_sweeps_long_expired_codes_and_never_touches_pairings(string engine)
    {
        var state = Open(engine);
        var codes = new ModulePairingCodes(state);
        var clients = new ModulePairedClients(state);
        clients.Upsert(Console("console-1"));
        codes.Create(ServerRole.Viewer, lifetime: TimeSpan.FromMinutes(5));
        _clock.Advance(TimeSpan.FromDays(40));

        var result = new RetentionJob(state).Run();

        Assert.Equal(1, result.Removed["trust.pairing_code"]);
        Assert.NotNull(clients.Find("console-1"));
    }
}
