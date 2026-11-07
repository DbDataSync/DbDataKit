using DbDataKit.Connections;
using DbDataKit.Connections.Inventory;

namespace DbDataBuild.Cli.Server;

/// <summary>
/// The connections of a Build server and who uses them (plan 28c ED17, E23). A project's connections are its <c>connections/&lt;name&gt;.yaml</c> files; they hold no address, so only the engine is reported, and the logins are the server's secrets
/// <c>dbdata:build:connection:&lt;name&gt;:read</c> and <c>...:write</c> (the reference of the read login is reported; the write one is named by the flow that needs it, never a value). The users:
/// <list type="bullet">
/// <item>the server itself, with the <c>read</c> login (plans, previews, reports, history);</item>
/// <item>each scheduled refresh plan, with the <c>write</c> login (a refresh changes the target), unless the connection is read-only;</item>
/// <item>the flows a person starts that write (<c>deploy --apply</c>, <c>ack</c>, seeds), with the <c>write</c> login: the write login stays in Build for them (ED16).</item>
/// </list>
/// </summary>
internal static class BuildConnectionInventory
{
    public static readonly string[] WriteFlows = ["deploy --apply", "ack", "seed"];

    public static IReadOnlyList<InventoryConnection> Of(IEnumerable<(string Id, string Root)> projects)
    {
        var rows = new SortedDictionary<string, (string Engine, bool Readonly, List<ConnectionUse> Uses)>(StringComparer.Ordinal);
        foreach (var (id, root) in projects)
        {
            var folder = Path.Combine(root, "connections");
            if (!Directory.Exists(folder)) continue;
            foreach (var path in Directory.EnumerateFiles(folder, "*.yaml").Order(StringComparer.Ordinal))
            {
                string text;
                try { text = File.ReadAllText(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                // a file that is not a valid connection is left out (the project's own checks say why); nothing of its text is echoed
                if (ConnectionDefinitionLoader.Load(text, path, addressRequired: false).Definition is not { } definition || !definition.Name.Equals(Path.GetFileNameWithoutExtension(path), StringComparison.Ordinal)) continue;
                var readOnly = definition.Access == AccessMode.Readonly;
                if (!rows.TryGetValue(definition.Name, out var row)) rows[definition.Name] = row = (definition.Engine, readOnly, [new ConnectionUse(ProcessKinds.Server, "this server", UseRoles.Read, Channel: id)]);
                if (readOnly) continue;
                foreach (var flow in WriteFlows) row.Uses.Add(new ConnectionUse(ProcessKinds.Flow, $"{id}: {flow}", UseRoles.Write));
            }

            foreach (var job in RefreshJobs.Discover(root))
                if (rows.TryGetValue(job.Connection, out var row) && !row.Readonly)
                    row.Uses.Add(new ConnectionUse(ProcessKinds.Plan, $"{id}/{job.Connection}/{job.Plan}", UseRoles.Write, Channel: id));
        }

        return [.. rows.Select(r => new InventoryConnection(r.Key, "build", r.Value.Engine, null, null, ServerSecrets.NameOf(r.Key, LoginRole.Read).ToRef().ToString(), r.Value.Uses))];
    }
}
