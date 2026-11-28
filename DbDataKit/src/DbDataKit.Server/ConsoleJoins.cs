using System.Text.Json;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataKit.Server;

/// <summary>
/// Servers joining the console by a single-use token: the tokens are rows of the console's state (<see cref="StateJoinTokenStore"/>), the command a
/// person runs on the server's machine is made here (<see cref="Command"/>), and a server that joined is recorded in <c>instances.yaml</c> with the
/// certificate it presented pinned, as a server paired by code is. Every token made and every join is in the console's audit; the token's value never is.
/// Design: docs/install.md#pairing
/// </summary>
/// <param name="keys">The key the console signs with; needed to answer a join, not to make a token.</param>
/// <param name="registry">Where a joined server is recorded; needed to answer a join, not to make a token.</param>
/// <param name="certificateFingerprint">The fingerprint of the certificate the console presents; needed to make the command of a token (<see cref="Invite"/>).</param>
/// <param name="groupKeys">The console's encrypted store, where the keys of the groups it manages are kept; null: a join hands no group anything.</param>
public sealed class ConsoleJoins(StateDatabase state, ConsoleKeyStore? keys = null, InstanceRegistry? registry = null, TimeProvider? clock = null, string? certificateFingerprint = null,
    DbDataKit.Secrets.Store.EncryptedFileSecretStore? groupKeys = null)
{
    /// <summary>The products a token can be made for, as <c>setup invite</c> names them, and the program each runs <c>pair</c> with.</summary>
    public static readonly IReadOnlyDictionary<string, string> Products = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["gateway"] = "dbdatagateway",
        ["runner"] = "dbdatarunner",
        ["workspace"] = "dbdataworkspace",
    };

    public const string InviteVerb = "server.invite";

    /// <summary>The audit verb of a token a person cancelled before it was used.</summary>
    public const string CancelVerb = "server.invite.cancel";

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public IJoinTokenStore Tokens { get; } = new StateJoinTokenStore(state);

    /// <summary>The groups of the servers that joined: what a further instance is handed, and which instance a named group is; null when the console was opened without its store.</summary>
    public ConsoleJoinGroups? Groups { get; } = groupKeys is null ? null : new ConsoleJoinGroups(state, groupKeys);

    private ConsoleKeyStore Keys => keys ?? throw new InvalidOperationException("This console's joins were opened to make tokens only.");

    /// <summary>Makes a token and audits it (who made it, for what, until when; never its value).</summary>
    public MintedJoinToken Mint(JoinScope scope, string madeBy, string? madeByName, TimeSpan? lifetime = null, string actorKind = "person")
    {
        var minted = JoinTokens.Mint(scope, madeBy, madeByName, lifetime, _clock);
        Tokens.Add(minted.Record);
        new AuditLog(state, ConsoleState.ServiceId).Record(new AuditEvent(actorKind, madeBy, InviteVerb, AuditEvent.Ok, TargetKind: "join-token", TargetId: minted.Record.Id,
            Detail: JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["product"] = scope.Product, ["ceiling"] = ServerRoles.Names(scope.Ceiling), ["group"] = scope.Group, ["expires"] = minted.Record.ExpiresAt.UtcDateTime.ToString("O"),
            })));
        return minted;
    }

    /// <summary>
    /// Makes a token and the command that spends it, for a server that reaches the console at <paramref name="address"/>: what the Add a server screen
    /// shows once. The token is audited as <see cref="Mint"/> does; the command is not kept anywhere.
    /// </summary>
    public (MintedJoinToken Minted, string Command) Invite(JoinScope scope, Uri address, string madeBy, string? madeByName, TimeSpan? lifetime = null)
    {
        var fingerprint = certificateFingerprint ?? throw new InvalidOperationException("This console's joins were opened without its certificate.");
        var minted = Mint(scope, madeBy, madeByName, lifetime);
        return (minted, Command(scope.Product, address, minted.Token, fingerprint));
    }

    /// <summary>The tokens still waiting to be used, oldest first: what the Add a server screen lists (never a token's value or verifier).</summary>
    public IReadOnlyList<JoinTokenRecord> Waiting() =>
        [.. Tokens.All().Where(t => t.StateAt(_clock.GetUtcNow()) == JoinTokenState.Waiting)];

    /// <summary>
    /// Ends a waiting token, so the command made with it no longer joins anything, and audits who cancelled it. Returns the token as it is now, or null
    /// when there is no token with this id; a token that is no longer waiting (spent, expired, cancelled) is returned unchanged and nothing is audited.
    /// </summary>
    public JoinTokenRecord? Cancel(string id, string cancelledBy, string actorKind = "person")
    {
        var now = _clock.GetUtcNow();
        var cancelled = false;
        var kept = Tokens.Update(id, current =>
        {
            cancelled = current.StateAt(now) == JoinTokenState.Waiting;
            return cancelled ? current with { CancelledAt = now } : current;
        });
        if (kept is not null && cancelled)
            new AuditLog(state, ConsoleState.ServiceId).Record(new AuditEvent(actorKind, cancelledBy, CancelVerb, AuditEvent.Ok, TargetKind: "join-token", TargetId: id,
                Detail: JsonSerializer.Serialize(new Dictionary<string, object?> { ["product"] = kept.Product, ["made_by"] = kept.MadeByName ?? kept.MadeBy })));
        return kept;
    }

    /// <summary>The command a person runs on the server's machine: <c>dbdatarunner pair --host https://console:7800 --token ... --console-thumbprint sha256:...</c>.</summary>
    public static string Command(string product, Uri console, string token, string certificateFingerprint) =>
        $"{Products[product]} {JoinPairCommand.Name} --host {console.GetLeftPart(UriPartial.Authority)} --token {token} --console-thumbprint {Thumbprints.Format(certificateFingerprint)}";

    /// <summary>The console's half of the join for one request: made with the key the console signs with now.</summary>
    public async Task<JoinService> ServiceAsync(Func<string> certificateFingerprint, CancellationToken ct)
    {
        var identity = await Keys.GetOrCreateAsync(ct);
        return new JoinService(identity, ConsoleInstances.ClientName, certificateFingerprint, Tokens, new HttpJoinCallback(), new JoinAudit(state), _clock, groups: Groups);
    }

    /// <summary>
    /// Records a server that joined, with its certificate pinned and the console key it now holds. A further instance of a registered group (the same instance id,
    /// joining from another address) leaves the group's entry as it is: the console keeps calling the group at the address and certificate it registered, and the
    /// instances share the console as a client through their state. The server that joined first, joining again from its address, replaces its entry.
    /// </summary>
    public async Task RecordAsync(JoinedServer server, CancellationToken ct)
    {
        var identity = await Keys.GetOrCreateAsync(ct);
        var instances = registry ?? throw new InvalidOperationException("This console's joins were opened to make tokens only.");
        var registered = instances.All().FirstOrDefault(i => i.InstanceId == server.InstanceId);
        if (registered is null || string.Equals(registered.Url.GetLeftPart(UriPartial.Authority), server.Address.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
            instances.Add(server, identity.Key.Fingerprint);
        Groups?.Record(server);
    }

    /// <summary>The join's audit entries in the console's audit: <c>server.join</c>, the person who made the token as actor, the token's id and the outcome in the detail.</summary>
    private sealed class JoinAudit(StateDatabase state) : IAuditLog
    {
        private readonly AuditLog _audit = new(state, ConsoleState.ServiceId);

        public void Append(AuditEntry entry) => state.Retry(() => _audit.Record(new AuditEvent(
            "person", entry.UserId, JoinService.AuditCommand, entry.Outcome == "joined" ? AuditEvent.Ok : AuditEvent.Refused,
            ViaKind: "join", TargetKind: "server", TargetId: entry.Target,
            Detail: JsonSerializer.Serialize(new Dictionary<string, object?> { ["token"] = entry.TokenId, ["made_by"] = entry.UserName, ["outcome"] = entry.Outcome }))));

        public IReadOnlyList<AuditEntry> Read(int limit = 100) => [];
    }
}
