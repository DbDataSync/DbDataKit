using DbDataKit.Secrets;
using DbDataKit.Secrets.Store;
using DbDataKit.Web.Trust;
using ServerJoinRequest = DbDataKit.Web.Trust.JoinRequest;

namespace DbDataKit.Server.Tests;

/// <summary>
/// The console's groups during a join: one key per group, kept by the group's instance id and the same at every join; the key a group already holds kept when
/// the console has none; the first Gateway's listen settings handed to a further instance only; a token for a named group refused for an instance of another
/// state database, and for an instance known under another group.
/// </summary>
public sealed class ConsoleJoinGroupsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-console-groups-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ConsoleJoinGroups groups;
    private readonly EncryptedFileSecretStore store;

    public ConsoleJoinGroupsTests()
    {
        Directory.CreateDirectory(directory);
        store = new EncryptedFileSecretStore(SecretKeys.StoreDirectory(directory), SecretKeys.Open(directory, new SecretKeyOptions(), null, null));
        groups = new ConsoleJoinGroups(ConsoleState.Open(directory), store);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

    private static readonly Dictionary<string, string[]> First = new() { ["listen"] = ["https://0.0.0.0:7821"], ["public.listen"] = ["https://0.0.0.0:7820"] };

    private static ServerJoinRequest Request(string product, string instance, string address, string? groupKey = null, Dictionary<string, string[]>? listeners = null) =>
        new("join", "verifier", "proof", product, null, instance, null, address, new string('a', 64), "key", Listeners: listeners, GroupKey: groupKey);

    private static JoinTokenRecord Token(string product, string? group) =>
        JoinTokens.Mint(new JoinScope(product, ServerRole.Admin, group), "user-7").Record;

    private static JoinedServer Joined(ServerJoinRequest request, string? group) =>
        new(request.InstanceId!, request.InstanceId!, request.Product!, new Uri(request.Address!), request.CertificateFingerprint!, [], ServerRole.Admin, group, "jt-1", "user-7", DateTimeOffset.UtcNow)
        { Listeners = request.Listeners };

    private async Task<JoinGroupHandover> JoinAsync(ServerJoinRequest request, string? group)
    {
        var decision = await groups.PrepareAsync(request, Token(request.Product!, group), default);
        Assert.False(decision.Refused, decision.Told);
        groups.Record(Joined(request, group));
        return decision.Handover!;
    }

    [Fact]
    public async Task Every_instance_of_a_group_gets_the_one_key_and_a_further_gateway_the_first_ones_ports()
    {
        var first = await JoinAsync(Request("gateway", "gateway-1", "https://gw-a:7822", listeners: First), "sales");
        Assert.NotNull(first.GroupKey);
        Assert.Null(first.Listeners);
        using (var kept = await store.TryResolveAsync(ConsoleGroupKeys.NameFor("gateway-1").ToRef())) Assert.Equal(first.GroupKey, kept!.RevealString());

        var further = await JoinAsync(Request("gateway", "gateway-1", "https://gw-b:7822", listeners: new() { ["listen"] = ["https://0.0.0.0:7831"] }), "sales");
        Assert.Equal(first.GroupKey, further.GroupKey);
        Assert.Equal(["https://0.0.0.0:7821"], further.Listeners!["listen"]);

        // the first instance joining again from its address keeps its own ports
        Assert.Null((await JoinAsync(Request("gateway", "gateway-1", "https://gw-a:7822", listeners: First), "sales")).Listeners);
        // a Runner group has its own key and no ports
        var runner = await JoinAsync(Request("runner", "runner-1", "https://rn-a:7840"), "night");
        Assert.NotEqual(first.GroupKey, runner.GroupKey);
        Assert.Null((await JoinAsync(Request("runner", "runner-1", "https://rn-b:7840"), "night")).Listeners);
        // a Workspace keeps no group key
        Assert.Null((await JoinAsync(Request("workspace", "workspace-1", "https://ws:7860"), null)).GroupKey);
    }

    [Fact]
    public async Task The_key_a_group_already_holds_is_kept_when_the_console_has_none()
    {
        var held = GroupCipher.NewKey();
        Assert.Equal(held, (await JoinAsync(Request("gateway", "gateway-2", "https://gw:7822", groupKey: held), null)).GroupKey);
        // once the console keeps a key, another one offered changes nothing
        Assert.Equal(held, (await JoinAsync(Request("gateway", "gateway-2", "https://gw-b:7822", groupKey: GroupCipher.NewKey()), null)).GroupKey);
    }

    [Fact]
    public async Task A_token_for_a_group_is_refused_for_another_state_database_and_for_an_instance_of_another_group()
    {
        await JoinAsync(Request("runner", "runner-1", "https://rn-a:7840"), "night");

        var other = await groups.PrepareAsync(Request("runner", "runner-9", "https://rn-z:7840"), Token("runner", "night"), default);
        Assert.True(other.Refused);
        Assert.Contains("not an instance of it", other.Told);
        Assert.Contains("runner-1", other.Reason);

        var moved = await groups.PrepareAsync(Request("runner", "runner-1", "https://rn-b:7840"), Token("runner", "day"), default);
        Assert.True(moved.Refused);
        Assert.Contains("an instance of group night", moved.Told);

        // a token that names no group: the instance is joined under the group the console knows it by
        var unnamed = await groups.PrepareAsync(Request("runner", "runner-1", "https://rn-b:7840"), Token("runner", null), default);
        Assert.Equal("night", unnamed.Handover!.Group);
        // the same group name is another group for another product
        Assert.False((await groups.PrepareAsync(Request("gateway", "gateway-5", "https://gw:7822"), Token("gateway", "night"), default)).Refused);
    }
}
