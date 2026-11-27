using System.Text.Json;
using DbDataKit.Secrets;
using DbDataKit.Secrets.Store;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataKit.Server;

/// <summary>
/// The keys of the groups this console manages, in its own encrypted store under <c>dbdatakit:server:group-key:&lt;instance id&gt;</c>. Every instance of a group
/// shares its state database and so its instance id, which makes the id the group's: a key kept for it opens the values of every instance of the group. One gate
/// for the whole console, so a join and a person joining an instance from the Servers page never make two keys for one group.
/// </summary>
public static class ConsoleGroupKeyRing
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static SecretName NameFor(string groupId) => new("server", "group-key", groupId);

    /// <summary>
    /// The group's key: the one the console keeps; when it keeps none, <paramref name="offered"/> (the key the group already holds, sent by its instance), else a new one.
    /// The key is kept before it is returned.
    /// </summary>
    public static async Task<string> KeyOfAsync(ISecretStore store, ISecretProvider reader, string groupId, string? offered, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            using (var held = await reader.TryResolveAsync(NameFor(groupId).ToRef(), ct))
                if (held is not null) return held.RevealString();
            var key = offered ?? GroupCipher.NewKey();
            using var value = SecretValue.From(key);
            await store.SetAsync(NameFor(groupId), value, ct);
            return key;
        }
        finally { Gate.Release(); }
    }
}

/// <summary>
/// What a joining server is handed about its group, and whether it may join the group its token names (<see cref="IJoinGroups"/>). The console keeps, in its
/// state's settings, each group's first instance: the product, the group's name, the address it joined from and the listen settings it reported, and for a named
/// group the instance id it belongs to. A further instance of the group (the same instance id, joining from another address) is handed the group's key and, for a
/// Gateway, the first instance's listen settings, so it listens on the same ports. A token for a group whose instances share another state database is refused,
/// as is a token for another group than the one the instance is known under.
/// Design: docs/install.md#pairing
/// </summary>
public sealed class ConsoleJoinGroups(StateDatabase state, EncryptedFileSecretStore keys) : IJoinGroups
{
    private const string InstancePrefix = "join-group:";
    private const string NamePrefix = "join-group-name:";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The products whose groups keep a key the console makes: the Gateway and the Runner.</summary>
    public static bool TakesKey(string product) => product is "gateway" or "runner";

    /// <summary>The products whose further instances take their group's listen settings: the Gateway.</summary>
    public static bool TakesListeners(string product) => product == "gateway";

    /// <summary>A group's first instance as the console keeps it.</summary>
    public sealed record GroupRecord(string Product, string? Group, string Address, Dictionary<string, string[]>? Listeners);

    private readonly GroupSettings _settings = new(state);

    public GroupRecord? Find(string instanceId)
    {
        if (_settings.Get(InstancePrefix + instanceId) is not { } text) return null;
        try { return JsonSerializer.Deserialize<GroupRecord>(text, Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>The instance id a named group of <paramref name="product"/> belongs to; null when no instance joined with that group yet.</summary>
    public string? InstanceOf(string product, string group) => _settings.Get(NamePrefix + product + ":" + group);

    public async Task<JoinGroupDecision> PrepareAsync(DbDataKit.Web.Trust.JoinRequest request, JoinTokenRecord token, CancellationToken ct)
    {
        var product = request.Product!;
        var instance = request.InstanceId!;
        var group = request.Group ?? token.Group;
        var known = Find(instance);
        if (group is not null)
        {
            if (InstanceOf(product, group) is { } owner && owner != instance)
                return JoinGroupDecision.Refuse(
                    $"token {token.Id} is for group {group}, whose instances are {owner}; the server is {instance}, which shares another state database",
                    $"The join token was made for group {group}, and this server is not an instance of it: the instances of a group share one state database, and this one has its own. " +
                    $"Point this server's state at the group's database, or make a command for a group of its own on the console.");
            if (known?.Group is { } was && was != group)
                return JoinGroupDecision.Refuse(
                    $"token {token.Id} is for group {group}, the server is an instance of group {was}",
                    $"The join token was made for group {group}, and this server is an instance of group {was}. Make a command for group {was} on the console.");
        }

        var key = TakesKey(product) ? await ConsoleGroupKeyRing.KeyOfAsync(keys, keys, instance, request.GroupKey, ct) : null;
        // the first instance's ports, for another instance of its group; the instance that joined first, joining again from its address, keeps its own
        var listeners = TakesListeners(product) && known is { Listeners: { } first } && !SameAddress(known.Address, request.Address) ? first : null;
        return new JoinGroupDecision(new JoinGroupHandover(key, listeners, Link: null, Group: request.Group is null && token.Group is null ? known?.Group : null));
    }

    /// <summary>
    /// Records a server that joined: the group's first instance (or the first instance joining again from its address, with the listen settings it reports now) and,
    /// for a named group, which instance id it is. A further instance of the group changes nothing.
    /// </summary>
    public void Record(JoinedServer server)
    {
        var name = InstancePrefix + server.InstanceId;
        var address = server.Address.GetLeftPart(UriPartial.Authority);
        var stored = _settings.Get(name);
        var known = Find(server.InstanceId);
        var record = new GroupRecord(server.Product, server.Group ?? known?.Group, address, server.Listeners is { } l ? new Dictionary<string, string[]>(l) : null);
        if (stored is null) _settings.Put(name, JsonSerializer.Serialize(record, Json));
        else if (known is not null && SameAddress(known.Address, address)) _settings.Put(name, JsonSerializer.Serialize(record, Json), stored);
        if (record.Group is { } group) _settings.Put(NamePrefix + server.Product + ":" + group, server.InstanceId);
    }

    private static bool SameAddress(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
}
