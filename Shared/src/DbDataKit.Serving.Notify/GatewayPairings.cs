using System.Text;
using System.Text.Json;
using DbDataKit.Web.Trust;

namespace DbDataKit.Serving.Notify;

/// <summary>A serving group (or a single Gateway) a product server is paired with: its name here, address, instance id and the certificate fingerprint every later call pins. The same shape as Build's <c>proxies.json</c> and DbDataStore's, so a product that already pairs to publish passes that list.</summary>
public sealed record GatewayPairing(string Name, string Url, string InstanceId, string Fingerprint, DateTimeOffset PairedAt);

/// <summary>Where a notifier finds the serving groups its server is paired with. A product that pairs to publish (Build, DbDataStore) adapts its own list; one that has none (Sync, the Runner) uses <see cref="GatewayPairingsFile"/>.</summary>
public interface IGatewayPairings
{
    IReadOnlyList<GatewayPairing> All();
}

/// <summary>The pairings in one JSON file of the server's directory (<c>gateways.json</c> for a server that had none): a list of <see cref="GatewayPairing"/>, replaced whole through a temporary file. A file that is not that list is refused naming it.</summary>
public sealed class GatewayPairingsFile(string path) : IGatewayPairings
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();

    public string Path { get; } = path;

    public IReadOnlyList<GatewayPairing> All()
    {
        lock (_gate)
        {
            if (!File.Exists(Path)) return [];
            try { return JsonSerializer.Deserialize<List<GatewayPairing>>(File.ReadAllText(Path), Json) ?? []; }
            catch (JsonException) { throw new InvalidDataException($"{Path} is not a list of paired gateways; remove it and pair again."); }
        }
    }

    public GatewayPairing? Find(string name) => All().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));

    public void Add(GatewayPairing pairing)
    {
        lock (_gate) Write(All().Where(p => p.Name != pairing.Name).Append(pairing));
    }

    public bool Remove(string name)
    {
        lock (_gate)
        {
            var rest = All().Where(p => p.Name != name).ToList();
            if (rest.Count == All().Count) return false;
            Write(rest);
            return true;
        }
    }

    private void Write(IEnumerable<GatewayPairing> pairings)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        var temporary = Path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(pairings.OrderBy(p => p.Name, StringComparer.Ordinal), Json), new UTF8Encoding(false));
        File.Move(temporary, Path, overwrite: true);
    }

    /// <summary>
    /// Pairs this server with the Gateway at <paramref name="url"/> as a server client with the single-use code its administrator made (or the console made for it, plan 13 T11), and remembers the pairing as <paramref name="name"/>.
    /// The key this server signs with is the one of <paramref name="identity"/> (its own, in its own secret store): the same pairing a product uses to publish.
    /// </summary>
    public async Task<GatewayPairing> PairAsync(ConsoleKeyStore identity, string name, Uri url, string code, string clientName, CancellationToken ct = default) =>
        await PairAsync(await identity.GetOrCreateAsync(ct).ConfigureAwait(false), name, url, code, clientName, ct).ConfigureAwait(false);

    /// <summary>As the other overload, with the identity already in hand.</summary>
    public async Task<GatewayPairing> PairAsync(ConsoleIdentity self, string name, Uri url, string code, string clientName, CancellationToken ct = default)
    {
        var paired = await PairingClient.PairAsync(url, code, self, ClientKind.Server, clientName, ct).ConfigureAwait(false);
        var pairing = new GatewayPairing(name, paired.Url.ToString().TrimEnd('/'), paired.InstanceId, paired.CertificateFingerprint, DateTimeOffset.UtcNow);
        Add(pairing);
        return pairing;
    }
}
