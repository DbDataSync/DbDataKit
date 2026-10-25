using System.Globalization;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Web.Trust;

/// <summary>A server a console manages, as <c>instances.yaml</c> records it. <c>KeyFingerprint</c> is the console key the server has on record, so a console in the middle of a key rotation knows which key to sign for it with. <c>Ceiling</c> is the most the server lets this console grant its users (a token that claims more is refused), as the pairing said; none when the entry predates it.</summary>
public sealed record RegisteredInstance(string InstanceId, string Name, Uri Url, string CertificateFingerprint, int ApiVersion, DateTimeOffset? PairedAt, string? KeyFingerprint = null, ServerRole Ceiling = ServerRole.None);

/// <summary>
/// The servers a console is paired with: the <c>instances</c> list of the console's <c>instances.yaml</c>, one flow mapping each (plan 13, W12). It holds no secret: the pin is a certificate fingerprint,
/// the key that signs for these servers is in the secret store (<see cref="ConsoleKeyStore"/>).
/// </summary>
public sealed class InstanceRegistry(string path, TimeProvider? clock = null)
{
    public const string Key = "instances";

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly YamlListFile _file = new(path, Key);

    public string Path => _file.Path;

    public IReadOnlyList<RegisteredInstance> All()
    {
        var all = new List<RegisteredInstance>();
        foreach (var entry in _file.Entries())
        {
            string? Text(string name) => entry.Get(name) is YamlScalar { Value.Length: > 0 } s ? s.Value : null;
            if (Text("id") is not { } id || Text("url") is not { } url || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || Text("fingerprint") is not { } pin) continue; // not trusted: it has no pin
            _ = int.TryParse(Text("apiVersion"), CultureInfo.InvariantCulture, out var version);
            DateTimeOffset? paired = DateTimeOffset.TryParse(Text("paired"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when) ? when : null;
            var ceiling = entry.Get("ceiling") is YamlSequence roles && ServerRoles.TryParse(roles.Items.OfType<YamlScalar>().Select(r => r.Value), out var parsed) ? parsed : ServerRole.None;
            if (all.All(i => i.InstanceId != id)) all.Add(new RegisteredInstance(id, Text("name") ?? id, uri, pin, version, paired, Text("key"), ceiling));
        }
        return all;
    }

    /// <summary>The instance with this id or, failing that, this name (ignoring case); null when none or when the name is ambiguous.</summary>
    public RegisteredInstance? Find(string idOrName)
    {
        var all = All();
        if (all.FirstOrDefault(i => i.InstanceId == idOrName) is { } byId) return byId;
        var byName = all.Where(i => string.Equals(i.Name, idOrName, StringComparison.OrdinalIgnoreCase)).ToList();
        return byName.Count == 1 ? byName[0] : null;
    }

    public IReadOnlyList<string> Problems => _file.Problems;

    /// <summary>Records a pairing; pairing the same server again replaces its entry (a renewed certificate, a new address).</summary>
    public RegisteredInstance Add(PairedServer server, string? name = null, string? keyFingerprint = null) =>
        Write(new RegisteredInstance(server.InstanceId, string.IsNullOrWhiteSpace(name) ? server.Name : name, server.Url, server.CertificateFingerprint, server.ApiVersion, _clock.GetUtcNow(), keyFingerprint, server.Ceiling));

    /// <summary>Records that the server now has the console key with this fingerprint.</summary>
    public RegisteredInstance SetKey(string instanceId, string keyFingerprint) =>
        Write((Find(instanceId) ?? throw new InvalidOperationException($"No instance '{instanceId}' is registered.")) with { KeyFingerprint = keyFingerprint });

    private RegisteredInstance Write(RegisteredInstance instance)
    {
        _file.Upsert(instance.InstanceId,
            $"{{id: {YamlText.FlowScalar(instance.InstanceId)}, name: {YamlText.FlowScalar(instance.Name)}, url: {YamlText.FlowScalar(instance.Url.AbsoluteUri)}, fingerprint: {YamlText.FlowScalar(instance.CertificateFingerprint)}, " +
            $"apiVersion: {instance.ApiVersion.ToString(CultureInfo.InvariantCulture)}, {(instance.KeyFingerprint is { } key ? $"key: {YamlText.FlowScalar(key)}, " : "")}{(instance.Ceiling != ServerRole.None ? $"ceiling: {YamlText.FlowList(ServerRoles.Names(instance.Ceiling))}, " : "")}paired: {YamlText.FlowScalar(instance.PairedAt!.Value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))}}}");
        return instance;
    }

    public bool Remove(string instanceId) => _file.Remove(instanceId);
}
