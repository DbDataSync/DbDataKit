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
public sealed class ConsoleJoins(StateDatabase state, ConsoleKeyStore? keys = null, InstanceRegistry? registry = null, TimeProvider? clock = null)
{
    /// <summary>The products a token can be made for, as <c>setup invite</c> names them, and the program each runs <c>pair</c> with.</summary>
    public static readonly IReadOnlyDictionary<string, string> Products = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["gateway"] = "dbdatagateway",
        ["runner"] = "dbdatarunner",
        ["workspace"] = "dbdataworkspace",
    };

    public const string InviteVerb = "server.invite";

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public IJoinTokenStore Tokens { get; } = new StateJoinTokenStore(state);

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

    /// <summary>The command a person runs on the server's machine: <c>dbdatarunner pair --host https://console:7800 --token ... --console-thumbprint sha256:...</c>.</summary>
    public static string Command(string product, Uri console, string token, string certificateFingerprint) =>
        $"{Products[product]} {JoinPairCommand.Name} --host {console.GetLeftPart(UriPartial.Authority)} --token {token} --console-thumbprint {Thumbprints.Format(certificateFingerprint)}";

    /// <summary>The console's half of the join for one request: made with the key the console signs with now.</summary>
    public async Task<JoinService> ServiceAsync(Func<string> certificateFingerprint, CancellationToken ct)
    {
        var identity = await Keys.GetOrCreateAsync(ct);
        return new JoinService(identity, ConsoleInstances.ClientName, certificateFingerprint, Tokens, new HttpJoinCallback(), new JoinAudit(state), _clock);
    }

    /// <summary>Records a server that joined, with its certificate pinned and the console key it now holds.</summary>
    public async Task RecordAsync(JoinedServer server, CancellationToken ct)
    {
        var identity = await Keys.GetOrCreateAsync(ct);
        (registry ?? throw new InvalidOperationException("This console's joins were opened to make tokens only.")).Add(server, identity.Key.Fingerprint);
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
