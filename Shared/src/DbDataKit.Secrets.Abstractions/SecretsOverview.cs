using System.Text.Json.Nodes;

namespace DbDataKit.Secrets;

/// <summary>A secret a store holds: its name and when it was last written (null when the store does not say). Never a value.</summary>
public sealed record SecretHeld(SecretName Name, DateTimeOffset? Updated);

/// <summary>One place a definition names a secret reference: what names it (<c>connection erp</c>, <c>package/task slot</c>) and the reference as written.</summary>
public sealed record ReferenceUse(string UsedBy, SecretRef Reference);

/// <summary>
/// The overview of the secrets of one server (UI06): the secrets its store holds against the references its definitions name, in one JSON form every server answers with (<c>GET /api/v1/secrets/overview</c>), so the console joins the
/// answers of a Gateway and a runner without knowing which is which. A reference of the store (<c>secret:</c>) is <b>present</b> when the store holds a secret of that name and <b>missing</b> when it does not; a reference to the environment, a
/// file or a DPAPI blob is <b>outside</b>: this server cannot say whether it answers, so it is neither missing nor present. A secret that no reference names is <b>unused</b>. Names, references and dates only: no value is read here.
/// </summary>
public static class SecretsOverview
{
    public const string Present = "present";
    public const string Missing = "missing";
    public const string Outside = "outside";

    /// <summary><c>{product, provider, secrets: [{name, reference, updated, used_by}], references: [{reference, kind, status, used_by}], missing: [reference], unused: [name]}</c>, each list in name order.</summary>
    public static JsonObject Build(string product, string provider, IEnumerable<SecretHeld> held, IEnumerable<ReferenceUse> uses)
    {
        var names = held.GroupBy(h => h.Name.ToString(), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var byTarget = uses.GroupBy(u => u.Reference.ToString(), StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        static JsonArray Names(IEnumerable<string> users) => new([.. users.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(u => (JsonNode)JsonValue.Create(u)!)]);

        var secrets = new JsonArray();
        var unused = new JsonArray();
        foreach (var (name, secret) in names.OrderBy(n => n.Key, StringComparer.Ordinal))
        {
            var users = byTarget.Where(g => g.First().Reference.Kind == SecretRefKind.Secret && g.First().Reference.Target == name).SelectMany(g => g.Select(u => u.UsedBy)).ToList();
            secrets.Add(new JsonObject { ["name"] = name, ["reference"] = secret.Name.ToRef().ToString(), ["updated"] = secret.Updated?.ToString("O"), ["used_by"] = Names(users) });
            if (users.Count == 0) unused.Add(name);
        }

        var references = new JsonArray();
        var missing = new JsonArray();
        foreach (var group in byTarget)
        {
            var reference = group.First().Reference;
            var status = reference.Kind != SecretRefKind.Secret ? Outside : names.ContainsKey(reference.Target) ? Present : Missing;
            references.Add(new JsonObject { ["reference"] = reference.ToAuditString(), ["kind"] = reference.Kind.ToString().ToLowerInvariant(), ["status"] = status, ["used_by"] = Names(group.Select(u => u.UsedBy)) });
            if (status == Missing) missing.Add(reference.ToString());
        }
        return new JsonObject { ["product"] = product, ["provider"] = provider, ["secrets"] = secrets, ["references"] = references, ["missing"] = missing, ["unused"] = unused };
    }
}
