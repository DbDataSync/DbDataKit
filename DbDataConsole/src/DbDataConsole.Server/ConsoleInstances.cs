using System.Net.Http.Json;
using System.Text.Json;
using DbDataKit.Web.Trust;

namespace DbDataConsole.Server;

/// <summary>How a paired server answered when it was asked.</summary>
/// <param name="Instance">The registered server.</param>
/// <param name="Reachable">Whether it answered at all with a certificate and management API version this console accepts.</param>
/// <param name="Ready">Whether it said it was ready (null when it did not answer).</param>
/// <param name="Product">What it says it is (<c>dbdatabuild</c>, <c>dbdatasync</c>, <c>dbdataproxy</c>).</param>
/// <param name="Version">The program version it reported.</param>
/// <param name="ApiVersion">The management API version it reported.</param>
/// <param name="Problem">Why it could not be reached or is not usable, in words for the person.</param>
/// <param name="Generation">The compatibility generation it reported (24a UC04); null when it did not answer or states none.</param>
public sealed record InstanceStatus(RegisteredInstance Instance, bool Reachable, bool? Ready, string? Product, string? Version, int? ApiVersion, string? Problem, int? Generation = null)
{
    /// <summary>The generation it reported now, or the one the registry recorded when it was last seen (24a UC05).</summary>
    public int? KnownGeneration => Generation ?? Instance.Generation;

    /// <summary>Null when the server is within N and N-1 of this console (or states no generation); otherwise the words that say how far apart they are and which side to upgrade (<see cref="SuiteCompatibility"/>).</summary>
    public string? OutsideSupport =>
        KnownGeneration is not { } known || SuiteCompatibility.Supports(known) ? null
            // a server that refused the call already has the message, with both program versions
            : Generation is not null && !Reachable && Problem is not null ? Problem : SuiteCompatibility.Incompatibility(known, Version);
}

/// <summary>
/// The servers this console manages (plan 13, U12 and T04): pairing one with the code its administrator made, asking each how it is, and forgetting one. The pin of each server's certificate is in
/// <c>instances.yaml</c>; the key that signs for them is in the secret store. Every call to a server goes through the pin, so a server whose certificate changed is unreachable until it is paired again.
/// </summary>
public sealed class ConsoleInstances(InstanceRegistry registry, ConsoleKeyStore keys)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static readonly TimeSpan AskTimeout = TimeSpan.FromSeconds(5);

    /// <summary>What a console calls itself to the servers it pairs with, in their list of paired clients.</summary>
    public const string ClientName = "dbdatakit console";

    public InstanceRegistry Registry => registry;

    /// <summary>Pairs with the server at <paramref name="url"/>. Pairing a server that is already registered replaces its entry (a renewed certificate, a new address).</summary>
    public async Task<RegisteredInstance> PairAsync(Uri url, string code, string? name, CancellationToken ct = default)
    {
        var identity = await keys.GetOrCreateAsync(ct);
        var server = await PairingClient.PairAsync(url, code, identity, ClientKind.Console, ClientName, ct);
        // The server now holds the key that signed this pairing. During a key rotation that may be the new one, so its fingerprint is what is recorded.
        return registry.Add(server, name, identity.Key.Fingerprint);
    }

    /// <summary>
    /// Confirms a server that announced itself: pairs with it at the address and with the code it gave, then takes it off the waiting list. The address and the code are the server's own claim, which is why an administrator
    /// confirms; the pairing still pins the certificate the server presents then, as any pairing does. A server whose code has lapsed announces again and can be confirmed on the next listing.
    /// </summary>
    public async Task<RegisteredInstance> ConfirmAsync(PendingInstances pending, string idOrName, CancellationToken ct = default)
    {
        var waiting = pending.Find(idOrName) ?? throw new PairingFailedException($"No server waiting is called '{idOrName}' (or the name is not unique; use its id).");
        var paired = await PairAsync(waiting.Url, waiting.Code, waiting.Name, ct);
        pending.Remove(waiting.InstanceId);
        return paired;
    }

    public IReadOnlyList<RegisteredInstance> All() => registry.All();

    /// <summary>Forgets a server. It still lists this console among its paired clients until its administrator runs <c>server unpair</c> there.</summary>
    public bool Remove(string idOrName) => registry.Find(idOrName) is { } found && registry.Remove(found.InstanceId);

    /// <summary>Asks every registered server how it is, all at once, each for at most <see cref="AskTimeout"/>.</summary>
    public async Task<IReadOnlyList<InstanceStatus>> StatusOfAllAsync(CancellationToken ct = default) =>
        await Task.WhenAll(registry.All().Select(i => StatusAsync(i, ct)));

    public async Task<InstanceStatus> StatusAsync(RegisteredInstance instance, CancellationToken ct = default)
    {
        int? apiVersion = null;
        int? generation = null;
        try
        {
            using var http = new HttpClient(PairingClient.Pinned(instance.CertificateFingerprint, v => apiVersion = v, g => generation = g)) { Timeout = AskTimeout };
            using var version = await http.GetAsync(new Uri(instance.Url, "/version"), ct);
            var info = version.IsSuccessStatusCode ? await version.Content.ReadFromJsonAsync<VersionAnswer>(Json, ct) : null;
            // the registry keeps the generation the server last reported: an upgraded server reports another, and it is recorded when it changes
            if (generation is { } reported && instance.Generation != reported) registry.SetGeneration(instance.InstanceId, reported);
            return new InstanceStatus(instance, true, info?.Ready, info?.Product, info?.Version, apiVersion,
                version.IsSuccessStatusCode ? null : $"answered {(int)version.StatusCode} to a version request", generation);
        }
        catch (IncompatibleServerException ex)
        {
            return new InstanceStatus(instance, false, null, null, null, ex.ServerVersion, ex.Message, ex.ServerGeneration);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // The pin refuses a certificate that changed; say so in words rather than with the socket's, which name neither.
            var changed = ex is HttpRequestException { InnerException: System.Security.Authentication.AuthenticationException };
            return new InstanceStatus(instance, false, null, null, null, null,
                changed ? "its certificate is not the one this console paired with; pair it again if the change was intended" : "did not answer");
        }
    }

    private sealed record VersionAnswer(string? Product, string? Version, bool Ready);
}
