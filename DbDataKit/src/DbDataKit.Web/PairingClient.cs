using System.Net.Http.Json;
using System.Net.Security;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DbDataKit.Web.Trust;

/// <summary>A server a client has paired with: where it is, who it is, the certificate fingerprint to pin and what the client may grant there. <paramref name="Generation"/> and <paramref name="ProgramVersion"/> are what the server said at pairing (24a UC04); null from a server that predates them.</summary>
public sealed record PairedServer(Uri Url, string InstanceId, string CertificateFingerprint, int ApiVersion, string Name, ServerRole Ceiling, int? Generation = null, string? ProgramVersion = null);

public sealed class PairingFailedException(string message) : Exception(message);

/// <summary>
/// The client's half of pairing (plan 13, W12; <c>dbdatakit instance add</c> is this): it looks at the certificate the server presents, proves with the code that it holds it and that it saw that certificate,
/// and accepts the answer only if the server proves the same. What it keeps is the certificate's fingerprint, which every later call pins (<see cref="Pinned(string, Action{int}, Action{int})"/>): a self-signed certificate is fine, and so is a
/// renewed one only when it is paired again.
/// </summary>
public static class PairingClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Hello(string? InstanceId, int ApiVersion, int? Generation = null, string? ProgramVersion = null);

    private sealed record Answer(string? InstanceId, string? CertificateFingerprint, string? ServerProof, int ApiVersion, string? Name, string[]? Ceiling, int? Generation = null, string? ProgramVersion = null);

    private sealed record Refusal(string? Error);

    /// <summary>Pairs <paramref name="self"/> with the server at <paramref name="url"/> using the code its administrator made. <paramref name="ownGeneration"/> is this side's compatibility generation (<see cref="SuiteCompatibility"/>, which tests replace); <paramref name="ownProgramVersion"/> is named in the refusal of a pair too far apart and is the running program's by default.</summary>
    public static async Task<PairedServer> PairAsync(Uri url, string code, ConsoleIdentity self, ClientKind kind = ClientKind.Console, string? name = null, CancellationToken ct = default, int ownGeneration = SuiteCompatibility.Generation, string? ownProgramVersion = null)
    {
        if (url.Scheme != Uri.UriSchemeHttps) throw new PairingFailedException("A server is paired over HTTPS; the address must start with https://.");
        var key = PairingCodes.KeyFor(code) ?? throw new PairingFailedException("That is not a pairing code (20 letters and digits, usually written in four groups of five).");
        var endpoint = new Uri(url, ManagementApi.PairingPath);

        // 1. see the certificate (and which server this claims to be); nothing secret is sent
        string seen = "";
        using (var probe = new HttpClient(PinnedSocket(null, f => seen = f)) { Timeout = TimeSpan.FromSeconds(30) })
        {
            Hello? hello;
            try { hello = await probe.GetFromJsonAsync<Hello>(endpoint, Json, ct); }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException) { throw new PairingFailedException($"{url.Authority} did not answer as a DbDataKit server ({ex.GetType().Name})."); }
            if (hello?.InstanceId is null || seen.Length == 0) throw new PairingFailedException($"{url.Authority} did not answer as a DbDataKit server.");
            if (ManagementApi.Incompatibility(hello.ApiVersion) is { } message) throw new IncompatibleServerException(message, hello.ApiVersion);
            // a pair further apart than N and N-1 is refused before anything is proved or sent: the message names both versions and the side to upgrade (24a UC04)
            if (SuiteCompatibility.Incompatibility(hello.Generation, hello.ProgramVersion, ownGeneration, ownProgramVersion) is { } apart) throw new IncompatibleServerException(apart, hello.ApiVersion, hello.Generation);
        }

        // 2. prove, over a connection that must present that same certificate
        var proof = PairingProof.ForRequest(key, self.Id, self.Key.PublicKey, kind, seen);
        using var http = new HttpClient(Pinned(seen)) { Timeout = TimeSpan.FromSeconds(30) };
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync(endpoint, new { proof, clientId = self.Id, name, kind = kind.ToString().ToLowerInvariant(), publicKey = Base64Url.Encode(self.Key.PublicKey), generation = ownGeneration, programVersion = ownProgramVersion ?? SuiteCompatibility.ProgramVersion }, Json, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { throw new PairingFailedException($"{url.Authority} could not be reached for pairing ({ex.GetType().Name})."); }
        using (response)
        {
            // the server's own half of the generation check (it sees this client's generation): its message names both versions and the side to upgrade
            if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                Refusal? refusal = null;
                try { refusal = await response.Content.ReadFromJsonAsync<Refusal>(Json, ct); }
                catch (JsonException) { /* the status says enough */ }
                throw new IncompatibleServerException(refusal?.Error ?? "The server refused the pairing: it is not within one compatibility generation of this client.");
            }
            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden) throw new PairingFailedException("The server did not accept the code. It may have expired or been used; make a new one with 'server pair'.");
            if (!response.IsSuccessStatusCode) throw new PairingFailedException($"The server refused the pairing ({(int)response.StatusCode}).");
            Answer? answer;
            try { answer = await response.Content.ReadFromJsonAsync<Answer>(Json, ct); }
            catch (JsonException) { throw new PairingFailedException("The server's answer was not understood."); }
            if (answer?.InstanceId is null || answer.ServerProof is null || answer.CertificateFingerprint != seen
                || !PairingProof.Matches(answer.ServerProof, PairingProof.ForResponse(key, answer.InstanceId, seen, self.Id, self.Key.PublicKey)))
                throw new PairingFailedException("The server's answer did not prove that it holds the code, so nothing was paired. Check the address; something may be answering in its place.");
            if (!ServerRoles.TryParse(answer.Ceiling ?? [], out var ceiling)) throw new PairingFailedException("The server's answer named roles that are not known here.");
            return new PairedServer(url, answer.InstanceId, seen, answer.ApiVersion, answer.Name ?? answer.InstanceId, ceiling, answer.Generation, answer.ProgramVersion);
        }
    }

    /// <summary>
    /// A handler for calls to a paired server: it accepts the certificate with this fingerprint and no other, whatever the certificate chains to, and refuses a response from a server whose management API
    /// version is not one this client supports (<see cref="ManagementVersionHandler"/>).
    /// </summary>
    public static HttpMessageHandler Pinned(string fingerprint, Action<int>? observedVersion = null, Action<int>? observedGeneration = null) =>
        new ManagementVersionHandler(PinnedSocket(fingerprint, null), observed: observedVersion, observedGeneration: observedGeneration);

    private static SocketsHttpHandler PinnedSocket(string? fingerprint, Action<string>? observe) => new()
    {
        SslOptions = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            {
                if (certificate is null) return false;
                var actual = PairingProof.CertificateFingerprint(certificate);
                observe?.Invoke(actual);
                return fingerprint is null || string.Equals(actual, fingerprint, StringComparison.Ordinal);
            },
        },
    };
}
