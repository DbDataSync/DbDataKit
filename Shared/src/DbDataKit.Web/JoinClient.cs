using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DbDataKit.Web.Trust;

/// <summary>The server that joins: what it tells the console about itself.</summary>
/// <param name="Product">The program's product in lower case (<c>gateway</c>, <c>runner</c>, <c>workspace</c>).</param>
/// <param name="Address">Where the console calls this server; the join request's address, when it has one, is used instead.</param>
/// <param name="CertificateFingerprint">The SHA-256 of the certificate this server presents at that address.</param>
/// <param name="Key">The server's own Ed25519 key; it signs the callback's answer.</param>
/// <param name="Group">The group the server already belongs to; null to take the token's.</param>
/// <param name="InstanceId">The server's instance id.</param>
/// <param name="Name">The name the console lists it under; the instance id when null.</param>
/// <param name="Listeners">The server's listen settings by their name in its settings file, which the console hands on to further instances of its group; null for none.</param>
/// <param name="GroupKey">The key the server's group already holds, sent so a console that holds none for the group keeps it; null when the server holds none.</param>
public sealed record JoiningServer(string Product, string InstanceId, string? Name, Uri? Address, string CertificateFingerprint, ConsoleSigningKey Key, string? Group = null,
    IReadOnlyDictionary<string, string[]>? Listeners = null, string? GroupKey = null);

/// <summary>The console a server joined: whom it trusts from now on, and the console's certificate it pins.</summary>
/// <param name="Url">The console's address.</param>
/// <param name="CertificateFingerprint">The console's certificate, as the join command named it.</param>
/// <param name="Ceiling">The console's role ceiling on this server.</param>
/// <param name="Group">The group this server is in now; null for none.</param>
/// <param name="ConsoleId">The console's id, the issuer of its tokens.</param>
/// <param name="Name">The console's name.</param>
/// <param name="PublicKey">The Ed25519 key the console's tokens are signed with.</param>
/// <param name="JoinedAt">When the join happened.</param>
public sealed record JoinedConsole(string ConsoleId, string Name, byte[] PublicKey, Uri Url, string CertificateFingerprint, ServerRole Ceiling, string? Group, DateTimeOffset JoinedAt)
{
    /// <summary>The key of this server's group, which it keeps in its own secret store; null when the console handed none.</summary>
    public string? GroupKey { get; init; }

    /// <summary>The listen settings of the group's first instance, which this server takes as its own; null when it keeps its own.</summary>
    public IReadOnlyDictionary<string, string[]>? Listeners { get; init; }

    /// <summary>The link of this server's group with its partner group; null when the console made none.</summary>
    public JsonObject? Link { get; init; }

    /// <summary>The console as a paired client of this server, which validates the console's tokens.</summary>
    public PairedClient AsPairedClient(int? generation = null) => new(ConsoleId, Name, PublicKey, Ceiling, ClientKind.Console, JoinedAt, generation);
}

/// <summary>Why a join did not happen. <see cref="Refused"/> is true when the console answered and said no (a new command is needed), false when it could not be reached or was not the console the command named.</summary>
public sealed class JoinFailedException(string message, bool refused) : Exception(message)
{
    public bool Refused { get; } = refused;

    /// <summary>What the console answered, in its own words, when it refused; null when it did not answer.</summary>
    public string? Answer { get; init; }
}

/// <summary>
/// The joining server's half of the join: it sends the console a <see cref="JoinRequest"/> over TLS pinned to the console's thumbprint, signed with the key
/// made from the token (the token itself is never sent), waits for the console's callback (<see cref="PendingJoins"/>, which the server's
/// <see cref="JoinCallbackEndpoint"/> answers), and returns the console it now trusts.
/// Design: docs/install.md#pairing
/// </summary>
public static class JoinClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Refusal(string? Error);

    public static async Task<JoinedConsole> JoinAsync(JoinRequestFile file, JoiningServer self, PendingJoins pending, CancellationToken ct = default,
        int ownGeneration = SuiteCompatibility.Generation, string? ownProgramVersion = null, TimeProvider? clock = null, TimeSpan? timeout = null)
    {
        var key = JoinTokens.KeyFor(file.Token) ?? throw new JoinFailedException("The join request's token is not a join token.", refused: false);
        var address = (file.Address ?? self.Address) is { } a ? JoinRequestFile.Text(a) : throw new JoinFailedException("The join needs the address the console can call this server at.", refused: false);
        var joinId = Base64Url.Encode(RandomNumberGenerator.GetBytes(16));
        var unsigned = new JoinRequest(joinId, Base64Url.Encode(key.PublicKey), null, self.Product, self.Group, self.InstanceId, self.Name, address,
            self.CertificateFingerprint, Base64Url.Encode(self.Key.PublicKey), ownGeneration, ownProgramVersion ?? SuiteCompatibility.ProgramVersion,
            self.Listeners is { } listeners ? new Dictionary<string, string[]>(listeners) : null, self.GroupKey);
        var request = unsigned with { Proof = Base64Url.Encode(key.Sign(JoinProof.Request(file.ConsoleThumbprint, unsigned))) };
        var console = JoinRequestFile.Text(file.Console);

        using var waiting = pending.Begin(joinId, file.ConsoleThumbprint, address, self.CertificateFingerprint, self.Key);
        var seen = "";
        using var http = new HttpClient(PairingClient.PinnedSocket(file.ConsoleThumbprint, f => seen = f)) { Timeout = timeout ?? TimeSpan.FromSeconds(60) };
        HttpResponseMessage response;
        try { response = await http.PostAsJsonAsync(new Uri(file.Console, JoinEndpoint.Path), request, Json, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (seen.Length > 0 && seen != file.ConsoleThumbprint)
                throw new JoinFailedException($"{console} presented the certificate {Thumbprints.Format(seen)}, not {Thumbprints.Format(file.ConsoleThumbprint)} as the join command said, so nothing was sent. " +
                    "Check the address; something may be answering in the console's place. If the console's certificate was renewed, make a new command on the console.", refused: false);
            throw new JoinFailedException($"{console} could not be reached to join ({ex.GetType().Name}). Check that the console's address answers from this machine.", refused: false);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                Refusal? refusal = null;
                try { refusal = await response.Content.ReadFromJsonAsync<Refusal>(Json, ct); }
                catch (Exception ex) when (ex is JsonException or NotSupportedException) { /* the status says enough */ }
                var said = refusal?.Error ?? $"The console refused the join ({(int)response.StatusCode}).";
                throw new JoinFailedException($"{console} refused the join: {said}", refused: true) { Answer = said };
            }
            JoinAnswer? answer;
            try { answer = await response.Content.ReadFromJsonAsync<JoinAnswer>(Json, ct); }
            catch (JsonException) { answer = null; }
            var consoleKey = answer?.ConsolePublicKey is { } k ? Base64Url.Decode(k) : null;
            if (answer?.ConsoleId is null || consoleKey is not { Length: ConsoleSigningKey.PublicKeyLength } || !ServerRoles.TryParse(answer.Ceiling ?? [], out var ceiling))
                throw new JoinFailedException($"{console} answered the join with something that is not a join's answer.", refused: false);
            return new JoinedConsole(answer.ConsoleId, answer.ConsoleName ?? answer.ConsoleId, consoleKey, new Uri(console), file.ConsoleThumbprint, ceiling, answer.Group,
                (clock ?? TimeProvider.System).GetUtcNow())
            { GroupKey = answer.GroupKey, Listeners = answer.Listeners, Link = answer.Link };
        }
    }
}
