using System.Globalization;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Web.Trust;

/// <summary>
/// The paired clients in the <c>pairedClients</c> list of <c>server.yaml</c> (plan 13, W12): one flow mapping per client, so the list stays one line each and a person can read it. The file is read again on
/// every <see cref="Find"/>, so a pairing or an <c>unpair</c> made by the command line in another process takes effect on the server's next call. A damaged entry is never trusted: it is skipped and named
/// in <see cref="Problems"/>. Writes go through the surgical YAML editor, so the rest of the file, comments and all, is untouched.
/// </summary>
public sealed class PairedClientsFile(string path, TimeProvider? clock = null) : IPairedClients
{
    public const string Key = "pairedClients";

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly YamlListFile _file = new(path, Key);

    public string Path => _file.Path;

    /// <summary>What was wrong with the entries skipped the last time the file was read.</summary>
    public IReadOnlyList<string> Problems => [.. _file.Problems, .. Parse().Problems];

    public PairedClient? Find(string clientId) => All().FirstOrDefault(c => string.Equals(c.Id, clientId, StringComparison.Ordinal));

    public IReadOnlyList<PairedClient> All() => Parse().Clients;

    /// <summary>Adds the client, or replaces the entry with the same id (pairing again with a new key).</summary>
    public void Upsert(PairedClient client) => _file.Upsert(client.Id, Render(client with { PairedAt = client.PairedAt ?? _clock.GetUtcNow() }));

    /// <summary>Removes the client with this id; false when there was none.</summary>
    public bool Remove(string clientId) => _file.Remove(clientId);

    private static string Render(PairedClient c) =>
        $"{{id: {YamlText.FlowScalar(c.Id)}, name: {YamlText.FlowScalar(c.Name)}, kind: {c.Kind.ToString().ToLowerInvariant()}, publicKey: {YamlText.FlowScalar(Base64Url.Encode(c.PublicKey))}, " +
        $"ceiling: {YamlText.FlowList(ServerRoles.Names(c.Ceiling))}, paired: {YamlText.FlowScalar(c.PairedAt!.Value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))}}}";

    private (IReadOnlyList<PairedClient> Clients, IReadOnlyList<string> Problems) Parse()
    {
        var clients = new List<PairedClient>();
        var problems = new List<string>();
        foreach (var entry in _file.Entries())
        {
            var (client, problem) = ParseOne(entry);
            if (client is null) problems.Add($"{Path}:{entry.Line}: paired client skipped: {problem}");
            else if (clients.Any(c => c.Id == client.Id)) problems.Add($"{Path}:{entry.Line}: paired client '{client.Id}' is listed twice; the first entry stands");
            else clients.Add(client);
        }
        return (clients, problems);
    }

    private static (PairedClient?, string) ParseOne(YamlMapping entry)
    {
        string? Text(string name) => entry.Get(name) is YamlScalar { Value.Length: > 0 } s ? s.Value : null;
        var id = Text("id");
        if (id is null) return (null, "no id");
        var key = Text("publicKey") is { } encoded ? Base64Url.Decode(encoded) : null;
        if (key is not { Length: ConsoleSigningKey.PublicKeyLength }) return (null, $"'{id}' has no valid publicKey");
        if (!Enum.TryParse<ClientKind>(Text("kind") ?? "console", ignoreCase: true, out var kind) || !Enum.IsDefined(kind)) return (null, $"'{id}' has an unknown kind");
        if (entry.Get("ceiling") is not YamlSequence roles || !ServerRoles.TryParse(roles.Items.OfType<YamlScalar>().Select(s => s.Value), out var ceiling) || ceiling == ServerRole.None)
            return (null, $"'{id}' has no valid ceiling");
        DateTimeOffset? paired = DateTimeOffset.TryParse(Text("paired"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when) ? when : null;
        return (new PairedClient(id, Text("name") ?? id, key, ceiling, kind, paired), "");
    }
}
