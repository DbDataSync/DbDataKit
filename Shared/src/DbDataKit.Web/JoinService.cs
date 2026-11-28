using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DbDataKit.Web.Trust;

/// <summary>A server the console has joined: what the console records and pins.</summary>
/// <param name="Address">Where the console calls the server.</param>
/// <param name="CertificateFingerprint">The certificate the console pins for every call to <see cref="Address"/>.</param>
/// <param name="PublicKey">The server's own Ed25519 public key.</param>
/// <param name="Ceiling">The console's role ceiling on the server, from the token.</param>
/// <param name="Group">The group the server is in; null for none.</param>
/// <param name="TokenId">The id of the token spent (never its value).</param>
/// <param name="MadeBy">Who made the token.</param>
/// <param name="InstanceId">The server's instance id.</param>
/// <param name="Name">The name the console lists it under.</param>
/// <param name="Product">The product the token was made for.</param>
/// <param name="JoinedAt">When the console joined it.</param>
/// <param name="Generation">The server's compatibility generation; null when it said none.</param>
/// <param name="ProgramVersion">The server's program version.</param>
public sealed record JoinedServer(
    string InstanceId, string Name, string Product, Uri Address, string CertificateFingerprint, byte[] PublicKey, ServerRole Ceiling, string? Group,
    string TokenId, string MadeBy, DateTimeOffset JoinedAt, int? Generation = null, string? ProgramVersion = null)
{
    /// <summary>The listen settings the server reported (<see cref="JoinRequest.Listeners"/>); null when it reported none.</summary>
    public IReadOnlyDictionary<string, string[]>? Listeners { get; init; }
}

/// <summary>What the console hands a joining server about its group, in the join's answer.</summary>
/// <param name="GroupKey">The group's key; null for a product that keeps none.</param>
/// <param name="Listeners">The listen settings the server takes from its group's first instance; null when it keeps its own.</param>
/// <param name="Link">The link of the group with its partner group; null when there is none.</param>
/// <param name="Group">The group the console knows the server's instance under, when neither the server nor the token named one.</param>
public sealed record JoinGroupHandover(string? GroupKey, IReadOnlyDictionary<string, string[]>? Listeners = null, JsonObject? Link = null, string? Group = null);

/// <summary>The console's answer about the joining server's group: what to hand it, or why it is refused (<see cref="Reason"/> for the audit, <see cref="Told"/> for the server).</summary>
public sealed record JoinGroupDecision(JoinGroupHandover? Handover, string? Reason = null, string? Told = null)
{
    public bool Refused => Reason is not null;

    public static JoinGroupDecision Refuse(string reason, string told) => new(null, reason, told);
}

/// <summary>
/// The console's groups, asked during a join once the token's checks passed and before the console calls the server back: whether the server may join the group
/// the token names, and what it is handed (the group's key, its first instance's ports, a link). A refusal counts as a refused try of the token.
/// </summary>
public interface IJoinGroups
{
    Task<JoinGroupDecision> PrepareAsync(JoinRequest request, JoinTokenRecord token, CancellationToken ct);
}

/// <summary>How a join ended. <see cref="Told"/> is what the joining server is told; <see cref="Reason"/> is the full reason, for the console's audit and log. Neither holds the token.</summary>
/// <param name="Status">200 joined, 400 a request that is not one, 403 refused, 409 too far apart in version.</param>
/// <param name="Server">The server joined; null when refused.</param>
/// <param name="Answer">The answer sent to the server; null when refused.</param>
/// <param name="Reason">Why it was refused, for the audit and the log.</param>
/// <param name="Told">What the joining server is told.</param>
public sealed record JoinOutcome(int Status, JoinedServer? Server, JoinAnswer? Answer, string? Reason, string? Told)
{
    public bool Ok => Server is not null;
}

/// <summary>What the console asks of the joining server before it spends the token: that the process at the address the join named confirms it.</summary>
public sealed record JoinCallbackCall(Uri Address, string AddressText, string CertificateFingerprint, byte[] ServerPublicKey, string JoinId, string ConsoleId, string ConsoleFingerprint);

/// <summary>The console's call back to a joining server; null when the server confirmed the join, otherwise why not.</summary>
public interface IJoinCallback
{
    Task<string?> ConfirmAsync(JoinCallbackCall call, CancellationToken ct);
}

/// <summary>
/// The console's half of the join. A server sends a <see cref="JoinRequest"/>: the console finds the token by its verifier, checks the proof, the token's
/// state, product and group, calls the server back at the address it gave with that certificate pinned, and only then spends the token and answers with
/// its own identity. Every refusal is audited with who made the token and the token's id, never its value; a refused try counts against the token, and
/// <see cref="JoinTokens.MaxTries"/> of them cancel it. The joining server is told why only once it has proved it holds the token.
/// Design: docs/install.md#pairing
/// </summary>
/// <param name="console">The console's identity: its id and the key its tokens are signed with.</param>
/// <param name="consoleName">The console's name, as servers list it.</param>
/// <param name="certificate">The fingerprint of the certificate the console presents, read at each join (the joining server pinned it).</param>
/// <param name="tokens">The join tokens.</param>
/// <param name="callback">How the console calls a joining server back.</param>
/// <param name="audit">Where every join and refusal is recorded.</param>
/// <param name="clock">The time source.</param>
/// <param name="generation">The console's compatibility generation; tests replace it.</param>
/// <param name="programVersion">The console's program version; the running program's by default.</param>
/// <param name="groups">The console's groups: what a joining server is handed about its group, and whether it may join it; null hands nothing.</param>
public sealed partial class JoinService(
    ConsoleIdentity console, string consoleName, Func<string> certificate, IJoinTokenStore tokens, IJoinCallback callback, IAuditLog audit,
    TimeProvider? clock = null, int generation = SuiteCompatibility.Generation, string? programVersion = null, IJoinGroups? groups = null)
{
    public const string AuditCommand = "server.join";
    private const string Unknown = "unknown";
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<JoinOutcome> JoinAsync(JoinRequest request, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var key = request.TokenKey is { } k ? Base64Url.Decode(k) : null;
        var record = key is { Length: ConsoleSigningKey.PublicKeyLength } ? tokens.FindByVerifier(request.TokenKey!) : null;
        var target = Target(request);

        JoinOutcome Refuse(int status, string reason, string told, bool counts)
        {
            if (counts && record is not null) record = CountTry(record.Id, now) ?? record;
            var outcome = reason + (counts && record?.CancelledAt == now ? $"; that was the token's {JoinTokens.MaxTries}th refused try, so it is cancelled" : "");
            Audit(now, record, target, "refused: " + outcome);
            return new JoinOutcome(status, null, null, outcome, told);
        }

        if (Shape(request) is { } problem) return Refuse(400, "the request is not a join: " + problem, "The request is not a join: " + problem + ".", counts: false);

        // a server more than one generation from this console is refused before the token is looked at, naming both versions and the side to upgrade
        if (SuiteCompatibility.Incompatibility(request.Generation, request.ProgramVersion, generation, programVersion) is { } apart)
            return Refuse(409, apart, apart, counts: false);

        const string NotAccepted = "The console did not accept the join. Make a new command on the console (dbdatakit setup invite) and run it on this machine.";
        if (record is null) return Refuse(403, "no join token has this verifier", NotAccepted, counts: false);

        var ownFingerprint = certificate();
        if (!ConsoleSigningKey.Verify(key, JoinProof.Request(ownFingerprint, request), Base64Url.Decode(request.Proof!)))
            return Refuse(403, $"the proof does not match token {record.Id} (made for another console, or not made with the token)", NotAccepted, counts: true);

        switch (record.StateAt(now))
        {
            case JoinTokenState.Spent: return Refuse(403, $"token {record.Id} was used at {record.SpentAt:O}", "The join token was already used. Make a new command on the console (dbdatakit setup invite).", counts: false);
            case JoinTokenState.Expired: return Refuse(403, $"token {record.Id} expired at {record.ExpiresAt:O}", "The join token expired. Make a new command on the console (dbdatakit setup invite).", counts: false);
            case JoinTokenState.Cancelled: return Refuse(403, $"token {record.Id} was cancelled at {record.CancelledAt:O}", "The join token was cancelled. Make a new command on the console (dbdatakit setup invite).", counts: false);
        }

        if (request.Product != record.Product)
            return Refuse(403, $"token {record.Id} is for a {record.Product}, the server is a {request.Product}", $"The join token was made for a {record.Product}, and this server is a {request.Product}. Make a command for a {request.Product} on the console.", counts: true);
        if (request.Group is { } group && group != record.Group)
            return Refuse(403, $"token {record.Id} is for {GroupText(record.Group)}, the server is in group {group}", $"The join token was made for {GroupText(record.Group)}, and this server is in group {group}. Make a command for group {group} on the console.", counts: true);

        JoinGroupHandover? handover = null;
        if (groups is not null)
        {
            var decision = await groups.PrepareAsync(request, record, ct);
            if (decision.Refused) return Refuse(403, decision.Reason!, decision.Told ?? NotAccepted, counts: true);
            handover = decision.Handover;
        }

        var address = new Uri(request.Address!);
        var serverKey = Base64Url.Decode(request.PublicKey!)!;
        var call = new JoinCallbackCall(address, request.Address!, request.CertificateFingerprint!, serverKey, request.JoinId!, console.Id, ownFingerprint);
        string? callbackProblem;
        try { callbackProblem = await callback.ConfirmAsync(call, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException) { callbackProblem = $"it did not answer ({ex.GetType().Name})"; }
        if (callbackProblem is not null)
            return Refuse(403, $"the callback to {request.Address} failed: {callbackProblem}", $"The console could not confirm this server at {request.Address}: {callbackProblem}. Check that the console can reach that address, then start the service again.", counts: true);

        var spentNow = false;
        record = tokens.Update(record.Id, r =>
        {
            if (r.StateAt(now) != JoinTokenState.Waiting) return r;
            spentNow = true;
            return r with { SpentAt = now };
        }) ?? record;
        if (!spentNow) return Refuse(403, $"token {record.Id} was used by another join at the same time", "The join token was already used. Make a new command on the console (dbdatakit setup invite).", counts: false);

        var joinedGroup = request.Group ?? record.Group ?? handover?.Group;
        var server = new JoinedServer(request.InstanceId!, string.IsNullOrWhiteSpace(request.Name) ? request.InstanceId! : request.Name!, record.Product, address,
            request.CertificateFingerprint!, serverKey, record.Ceiling, joinedGroup, record.Id, record.MadeBy, now, request.Generation, request.ProgramVersion)
        { Listeners = request.Listeners };
        var answer = new JoinAnswer(console.Id, consoleName, Base64Url.Encode(console.Key.PublicKey), [.. ServerRoles.Names(record.Ceiling)], joinedGroup, server.InstanceId,
            handover?.GroupKey, handover?.Listeners is { } listeners ? new Dictionary<string, string[]>(listeners) : null, handover?.Link);
        Audit(now, record, target, "joined");
        return new JoinOutcome(200, server, answer, null, null);
    }

    private JoinTokenRecord? CountTry(string id, DateTimeOffset now) => tokens.Update(id, r =>
    {
        var tries = r.Tries + 1;
        return r with { Tries = tries, CancelledAt = r.CancelledAt ?? (tries >= JoinTokens.MaxTries && r.SpentAt is null ? now : null) };
    });

    private void Audit(DateTimeOffset at, JoinTokenRecord? record, string target, string outcome) =>
        audit.Append(new AuditEntry(at, console.Id, consoleName, record?.MadeBy ?? Unknown, record?.MadeByName, ByConsole: false, AuditCommand, target, outcome, record?.Id ?? ""));

    private static string GroupText(string? group) => group is null ? "no group" : "group " + group;

    // only the fields that passed their checks are written to the audit: a request that is not one leaves nothing of its own in the log
    private static string Target(JoinRequest r)
    {
        var parts = new List<string>();
        if (r.Product is { } p && ProductPattern().IsMatch(p)) parts.Add(p);
        if (r.InstanceId is { } i && IdPattern().IsMatch(i)) parts.Add(i);
        if (AddressProblem(r.Address) is null) parts.Add("at " + r.Address);
        return parts.Count == 0 ? Unknown : string.Join(' ', parts);
    }

    private static string? Shape(JoinRequest r) =>
        r.JoinId is not { } j || !JoinIdPattern().IsMatch(j) ? "the join id is missing or not valid"
        : r.TokenKey is null || Base64Url.Decode(r.TokenKey) is not { Length: ConsoleSigningKey.PublicKeyLength } ? "the token key is missing or not a 32-byte key"
        : r.Proof is null || Base64Url.Decode(r.Proof) is not { Length: 64 } ? "the proof is missing or not a signature"
        : r.Product is not { } p || !ProductPattern().IsMatch(p) ? "the product is missing or not a lower-case name"
        : r.Group is { } g && !GroupPattern().IsMatch(g) ? "the group is not letters, digits and . _ -, up to 64"
        : r.InstanceId is not { } i || !IdPattern().IsMatch(i) ? "the instance id is missing or not valid (letters, digits and . _ : -, up to 64)"
        : r.Name is { } n && (n.Length > 100 || n.Any(char.IsControl)) ? "the name is too long or has control characters"
        : AddressProblem(r.Address) is { } a ? a
        : r.CertificateFingerprint is not { } f || !FingerprintPattern().IsMatch(f) ? "the certificate fingerprint is not 64 lower-case hex digits"
        : r.PublicKey is null || Base64Url.Decode(r.PublicKey) is not { Length: ConsoleSigningKey.PublicKeyLength } ? "the public key is missing or not a 32-byte Ed25519 key"
        : ListenersProblem(r.Listeners) is { } l ? l
        : r.GroupKey is { } k && !IsKey(k) ? "the group key is not base64 of 32 bytes"
        : null;

    private static bool IsKey(string text)
    {
        try { _ = GroupCipher.Parse(text); return true; }
        catch (FormatException) { return false; }
    }

    // a handful of settings, each a short list of addresses: the console keeps them and hands them on, so nothing larger is taken
    private static string? ListenersProblem(Dictionary<string, string[]>? listeners) =>
        listeners is null ? null
        : listeners.Count > 16 ? "the listeners are more than 16 settings"
        : listeners.Any(l => !SettingPattern().IsMatch(l.Key)) ? "a listener's setting is not a lower-case name such as public.listen"
        : listeners.Values.Any(v => v is null || v.Length is 0 or > 8 || v.Any(a => a is null || a.Length > 300 || a.Any(char.IsControl))) ? "a listener has no address, more than 8, or one that is not an address"
        : null;

    /// <summary>Null when <paramref name="address"/> is an https address with a host and no user, path, query or fragment; otherwise why not.</summary>
    internal static string? AddressProblem(string? address) =>
        address is null || address.Length > 300 || !Uri.TryCreate(address, UriKind.Absolute, out var uri) ? "the address is missing or not an absolute address"
        : uri.Scheme != Uri.UriSchemeHttps ? "the address must start with https://"
        : uri.UserInfo.Length > 0 || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0 ? "the address is a scheme, a host and a port only"
        : null;

    [GeneratedRegex("^[A-Za-z0-9_-]{16,64}$")]
    private static partial Regex JoinIdPattern();

    [GeneratedRegex("^[a-z][a-z0-9-]{0,31}$")]
    private static partial Regex ProductPattern();

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex GroupPattern();

    [GeneratedRegex("^[A-Za-z0-9._:-]{1,64}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex FingerprintPattern();

    [GeneratedRegex("^[a-z][a-z0-9._-]{0,63}$")]
    private static partial Regex SettingPattern();
}
