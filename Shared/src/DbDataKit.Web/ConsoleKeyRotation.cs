using System.Net.Http.Json;
using System.Text.Json;

namespace DbDataKit.Web.Trust;

/// <summary>What happened to one server in a key rotation.</summary>
public sealed record RotationReport(RegisteredInstance Instance, bool Rotated, string? Problem);

/// <summary>
/// Moves a console to a new signing key without pairing again (plan 13, W12): the new key is made and kept beside the current one; each server is asked to take it, the request signed by the old key and by the
/// new; the registry notes which key each server has, so the console signs for each with the key it knows; and only when every server has the new key does it become the current one. A server that could not be
/// reached stays on the old key, the old key keeps working for it, and running the rotation again continues where it stopped.
/// </summary>
public static class ConsoleKeyRotation
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<IReadOnlyList<RotationReport>> RunAsync(ConsoleKeyStore keys, InstanceRegistry registry, TimeProvider? clock = null, CancellationToken ct = default)
    {
        var current = await keys.GetOrCreateAsync(ct);
        var next = await keys.BeginRotationAsync(ct);
        var reports = new List<RotationReport>();
        foreach (var instance in registry.All())
        {
            if (instance.KeyFingerprint == next.Key.Fingerprint) { reports.Add(new RotationReport(instance, true, null)); continue; }
            // a server paired before this was recorded has the current key
            var has = instance.KeyFingerprint is { } fp ? await keys.ForKeyAsync(fp, ct) : current;
            if (has is null) { reports.Add(new RotationReport(instance, false, "the console no longer has the key this server was told about")); continue; }
            try
            {
                await RotateOnAsync(instance, has, next, clock, ct);
                registry.SetKey(instance.InstanceId, next.Key.Fingerprint);
                reports.Add(new RotationReport(instance, true, null));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IncompatibleServerException or PairingFailedException)
            {
                reports.Add(new RotationReport(instance, false, ex is HttpRequestException or TaskCanceledException ? "could not be reached" : ex.Message));
            }
        }
        if (registry.All().All(i => i.KeyFingerprint == next.Key.Fingerprint)) await keys.PromoteNextAsync(ct);
        return reports;
    }

    /// <summary>Asks one server to replace the key <paramref name="current"/> with <paramref name="next"/> for this console.</summary>
    public static async Task RotateOnAsync(RegisteredInstance instance, ConsoleIdentity current, ConsoleIdentity next, TimeProvider? clock = null, CancellationToken ct = default)
    {
        var request = KeyRotationRequest.Create(instance.InstanceId, current.Id, current.Key, next.Key, (clock ?? TimeProvider.System).GetUtcNow());
        using var http = new HttpClient(PairingClient.Pinned(instance.CertificateFingerprint)) { Timeout = TimeSpan.FromSeconds(30) };
        using var response = await http.PostAsJsonAsync(new Uri(instance.Url, ManagementApi.RotationPath), new
        {
            clientId = request.ClientId,
            newPublicKey = Base64Url.Encode(request.NewPublicKey),
            timestamp = request.Timestamp,
            nonce = request.Nonce,
            signedByOld = Base64Url.Encode(request.SignedByOld),
            signedByNew = Base64Url.Encode(request.SignedByNew),
        }, Json, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden) throw new PairingFailedException($"{instance.Name} did not accept the new key. It may have been unpaired, or its clock may differ from this console's by more than {KeyRotationService.SkewSeconds} seconds.");
        if (!response.IsSuccessStatusCode) throw new PairingFailedException($"{instance.Name} refused the new key ({(int)response.StatusCode}).");
    }
}
