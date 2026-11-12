using DbDataKit.Lineage;
using DbDataKit.Serving.Notify;
using DbDataKit.Store;
using DbDataKit.Web.Trust;

namespace DbDataStore.Server;

/// <summary>
/// DbDataStore's producer of <c>data.changed/1</c> (plan 24a UC13): a write that was applied to a model's tables (a direct one, or the approval of a change request; never a draft, a refused or a rejected one) tells the serving groups this server is paired with
/// (<c>proxies.json</c>, the pairing it publishes with) which entities changed. The server signs as itself (<see cref="DataChangeNotifier"/>); each entity is named as lineage names the table of the model's copy (<see cref="LineageIdentity.Dataset"/> over the
/// address of the model's connection and the copy's schema), so a Gateway that serves the model, or a semantic model that reads it, finds it in its graph. No pairing sends nothing; a failure to tell never fails the write that was committed.
/// </summary>
public sealed class StoreDataChanges(IDataChangeNotifier notifier, StoreConnections connections, Action<string>? log = null)
{
    public const string Product = "store";

    public IDataChangeNotifier Notifier => notifier;

    private sealed class FromProxies(StoreProxies proxies) : IGatewayPairings
    {
        public IReadOnlyList<GatewayPairing> All() => [.. proxies.All().Select(p => new GatewayPairing(p.Name, p.Url, p.InstanceId, p.Fingerprint, p.PairedAt))];
    }

    /// <summary>The notifier of a server whose directory is <paramref name="directory"/>: its own key is opened from the server's secret store only when there is something to send to a pairing.</summary>
    public static StoreDataChanges Create(string directory, StoreConnections connections, Action<string>? line = null, DataChangeNotifierOptions? options = null)
    {
        ConsoleKeyStore? keys = null;
        var gate = new object();
        Task<ConsoleIdentity> Identity(CancellationToken ct)
        {
            lock (gate)
            {
                if (keys is null)
                {
                    var secrets = StorePublisher.OpenSecrets(directory);
                    keys = new ConsoleKeyStore(secrets, secrets);
                }
            }

            return keys.GetOrCreateAsync(ct);
        }

        return new StoreDataChanges(new DataChangeNotifier(Product, new FromProxies(new StoreProxies(directory)), Identity, line is null ? null : new LineLogger(line), options), connections, line);
    }

    /// <summary>Rows of <paramref name="entities"/> were applied to the model's copy in <paramref name="connection"/>, schema <paramref name="schema"/>. Never throws and never waits for the network.</summary>
    public async Task AppliedAsync(string connection, string schema, IEnumerable<string> entities, string? run = null, CancellationToken ct = default)
    {
        try
        {
            var names = entities.Distinct(StringComparer.Ordinal).ToList();
            if (names.Count == 0) return;
            var (address, problem) = await connections.AddressOnlyAsync(connection, ct).ConfigureAwait(false);
            if (address is null)
            {
                log?.Invoke($"The serving groups were not told that {string.Join(", ", names)} changed: {problem}");
                return;
            }

            notifier.Publish(names.Select(entity => new DataChange(LineageIdentity.Dataset(address, schema, entity).ToString(), run)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            log?.Invoke($"The serving groups were not told which entities changed ({ex.GetType().Name}).");
        }
    }
}
