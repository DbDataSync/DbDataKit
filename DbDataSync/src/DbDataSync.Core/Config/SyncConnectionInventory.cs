using DbDataKit.Connections.Inventory;
using DbDataSync.Core.Lineage;

namespace DbDataSync.Core.Config;

/// <summary>
/// The connections of a Sync server and who uses them (plan 28c ED17, E23): the server itself (its metadata browsing, tests and previews need the <c>read</c> login) and every replication that names the connection as a source or a target (a drain needs the
/// <c>run</c> login). A connection is reported with its engine, host (or none, for one addressed by a connection string, which is not echoed) and database and the secret <i>reference</i> of its credential, never a value; a connection with a second login also reports the reference of the run login (E20).
/// </summary>
public static class SyncConnectionInventory
{
    public const string ServerName = "this server";

    public static IReadOnlyList<InventoryConnection> Build(IReadOnlyList<ConnectionConfig> connections, IReadOnlyList<SyncReplication> replications)
    {
        var uses = UsesOf(replications);
        return [.. connections.OrderBy(c => c.Name, StringComparer.Ordinal).Select(c =>
        {
            var list = new List<ConnectionUse> { new(ProcessKinds.Server, ServerName, UseRoles.Read) };
            if (uses.TryGetValue(c.Name, out var found)) list.AddRange(found);
            return new InventoryConnection(c.Name, "sync", c.DriverType, c.Host is { Length: > 0 } h ? c.Port is { } p ? $"{h}:{p}" : h : null, c.Database, ReferenceOf(c.CredentialSecretRef), list, c.HasRunLogin ? ReferenceOf(c.RunCredentialSecretRef) : null);
        })];
    }

    /// <summary>The credential as the file keeps it is a secret name (<c>dbdata:sync:connection:erp</c>); as a reference it is <c>secret:</c> and that name. Anything else is passed on to be checked as a reference.</summary>
    private static string? ReferenceOf(string? stored) =>
        stored is not null && !DbDataKit.Secrets.SecretRef.LooksLikeRef(stored) && DbDataKit.Secrets.SecretName.TryParse(stored, out var name) ? name.ToRef().ToString() : stored;

    /// <summary>The replications that name each connection, by the connection's name.</summary>
    public static IReadOnlyDictionary<string, List<ConnectionUse>> UsesOf(IReadOnlyList<SyncReplication> replications)
    {
        var found = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        void Note(string? connection, string replication)
        {
            if (string.IsNullOrWhiteSpace(connection)) return;
            if (!found.TryGetValue(connection, out var set)) found[connection] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(replication);
        }

        foreach (var replication in replications)
        {
            var name = replication.Task.Name;
            Note(replication.Task.Endpoints.Source?.ConnectionName, name);
            Note(replication.Task.Endpoints.Target?.ConnectionName, name);
            foreach (var mapping in replication.Mappings)
            {
                foreach (var source in mapping.Sources) Note(source.ConnectionName, name);
                foreach (var target in mapping.Targets) Note(target.ConnectionName, name);
            }
        }

        return found.ToDictionary(f => f.Key, f => f.Value.Order(StringComparer.Ordinal).Select(r => new ConnectionUse(ProcessKinds.Replication, r, UseRoles.Run)).ToList(), StringComparer.Ordinal);
    }
}
