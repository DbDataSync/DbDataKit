using System.Text.Json;
using DbDataKit.Secrets;
using DbDataKit.Secrets.Store;
using DbDataKit.State;
using DbDataKit.Web.Auth;
using DbDataKit.Web.Trust;

namespace DbDataKit.Server;

/// <summary>What joining an instance to its group did: the status and words to answer with, and whether the instance already held the key.</summary>
public sealed record GroupJoin(int Status, string? Error, string Instance, bool Already = false)
{
    public bool Ok => Status is >= 200 and < 300;
}

/// <summary>
/// The keys of the groups this console manages (plan 28c E20d, owner decision 2026-11-15): a group whose instances share a state database keeps what is sent to it (a run login) in that state, encrypted with a key every
/// instance holds. The console makes the key the first time it joins an instance of the group and keeps it in its own encrypted store (<c>dbdatakit:server:group-key:&lt;group id&gt;</c>); joining hands it to the instance over the
/// paired channel (<c>PUT /api/v1/group/key</c>, as the signed-in administrator), so nobody copies a key by hand. Pairing a runner joins the instance paired with; another instance of the same group is joined by its address and a
/// pairing code made on it, which proves it serves the group (the pairing answer carries the group's id) and pins its certificate before the key is sent. The key is in no log, argument, audit entry or answer; the audit
/// (<see cref="JoinVerb"/>) names who joined which instance of which group. Only groups that take their key from the console are joined: the Runner today (a Gateway group makes its own until plan 36 IS13, which has the console make every group key).
/// </summary>
public sealed class ConsoleGroupKeys(EncryptedFileSecretStore store, ConsoleInstances instances, ConsoleRelay relay, ConsoleKeyStore keys, AuditLog audit, CurrentUser current)
{
    public const string JoinVerb = "server.group.join";

    /// <summary>The products whose groups take their key from the console.</summary>
    public static bool TakesKey(string? product) => product == ServerProducts.Runner;

    public static SecretName NameFor(string groupId) => new("server", "group-key", groupId);

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>The group's key, made the first time.</summary>
    private async Task<string> KeyOfAsync(string groupId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using (var held = await store.TryResolveAsync(NameFor(groupId).ToRef(), ct))
                if (held is not null) return held.RevealString();
            var made = GroupCipher.NewKey();
            using var value = SecretValue.From(made);
            await store.SetAsync(NameFor(groupId), value, ct);
            return made;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Joins an instance of <paramref name="group"/> (a registered server): the one at its registered address when <paramref name="url"/> is null, else the instance at <paramref name="url"/>, paired first with
    /// <paramref name="code"/> (made on that instance, or on any instance of the group, since the group shares its codes) without changing the registered address.
    /// </summary>
    public async Task<GroupJoin> JoinAsync(RegisteredInstance group, Uri? url, string? code, CancellationToken ct)
    {
        var target = group;
        var where = (url ?? group.Url).AbsoluteUri.TrimEnd('/');
        if (url is not null && url != group.Url)
        {
            if (url.Scheme != Uri.UriSchemeHttps) return Record(group, where, new GroupJoin(400, "The address is an https address, such as https://runner-2.example:7840.", where));
            if (string.IsNullOrWhiteSpace(code)) return Record(group, where, new GroupJoin(400, "A pairing code made on that instance is needed (dbdatarunner server pair there): it proves the instance serves the group before the key is sent.", where));
            PairedServer paired;
            try { paired = await PairingClient.PairAsync(url, code.Trim(), await keys.GetOrCreateAsync(ct), ClientKind.Console, ConsoleInstances.ClientName, ct); }
            catch (Exception ex) when (ex is PairingFailedException or IncompatibleServerException) { return Record(group, where, new GroupJoin(400, ex.Message, where)); }
            if (paired.InstanceId != group.InstanceId)
                return Record(group, where, new GroupJoin(409, $"The server at {where} is not an instance of {group.Name}: it answers as {paired.Name} ({paired.InstanceId}), so it shares another state database. Nothing was sent.", where));
            target = group with { Url = url, CertificateFingerprint = paired.CertificateFingerprint };
        }

        var key = await KeyOfAsync(group.InstanceId, ct);
        var (status, body) = await relay.CallAsync(target, HttpMethod.Put, "/api/v1/group/key", new { key }, ct);
        var outcome = status == 200
            ? new GroupJoin(200, null, where, body?["already"]?.GetValue<bool>() ?? false)
            : new GroupJoin(status, body?["error"]?.GetValue<string>() ?? $"{group.Name} did not take the group's key ({status}).", where);
        return Record(group, where, outcome);
    }

    /// <summary>Joins the instance just paired with when its group takes its key from the console; what went wrong is the answer's words (the pairing itself stands).</summary>
    public async Task<GroupJoin?> JoinPairedAsync(RegisteredInstance paired, CancellationToken ct) =>
        TakesKey((await instances.StatusAsync(paired, ct)).Product) ? await JoinAsync(paired, null, null, ct) : null;

    private GroupJoin Record(RegisteredInstance group, string instance, GroupJoin join)
    {
        audit.Record(new AuditEvent("person", current.Id ?? "unknown", JoinVerb, join.Ok ? AuditEvent.Ok : AuditEvent.Refused, ViaKind: "console", ViaId: "console", TargetKind: "server", TargetId: group.InstanceId,
            Detail: JsonSerializer.Serialize(new { group = group.Name, instance, status = join.Status, already = join.Already, error = join.Error })));
        return join;
    }
}
