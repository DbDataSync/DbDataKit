using System.Text.Json.Nodes;
using DbDataKit.Connections.Inventory;
using DbDataKit.Secrets;
using DbDataKit.State;

namespace DbDataKit.Runner.Api;

/// <summary>
/// The connections of a runner and who uses them (plan 28c ED17, E23): for every package with an active version in a channel, each connection slot its tasks declare, the name the channel binds it to (<c>definition_binding</c>), the task and
/// version that use it, and the runner instances that have that version active or ready. The runner holds the binding, a name or a reference, never a login: no value is read here.
/// </summary>
public static class RunnerConnections
{
    /// <summary>What <c>GET /api/v1/connections</c> answers. <paramref name="group"/> is the runner group this state belongs to.</summary>
    public static JsonObject Inventory(StateDatabase state, string? group, string product)
    {
        var publisher = new DefinitionPublisher(state, RunnerPackages.Kinds(), new PayloadStore(state), "runner");
        var store = new DefinitionStore(state);
        var byConnection = new SortedDictionary<string, (string Kind, List<ConnectionUse> Uses)>(StringComparer.Ordinal);
        var problems = new JsonArray();

        void Add(string connection, string kind, ConnectionUse use)
        {
            if (!byConnection.TryGetValue(connection, out var entry)) byConnection[connection] = entry = (kind, []);
            if (!entry.Uses.Contains(use)) entry.Uses.Add(use);
        }

        foreach (var package in publisher.Names(RunnerPackages.Kind))
        {
            foreach (var head in publisher.HeadsOf(RunnerPackages.Kind, package))
            {
                if (head.Active is not { } active || store.Get(RunnerPackages.Kind, package, active) is not { } version) continue;
                var loaded = PackageManifestReader.Load(version.Content, $"{package}@{active}");
                if (loaded is not { Ok: true, Manifest: { } manifest }) { problems.Add($"{package}@{active} in {head.Channel}: the manifest cannot be read."); continue; }
                var bindings = publisher.Bindings(head.Channel, RunnerPackages.Kind, package).ToDictionary(b => b.Slot, b => b.BoundTo, StringComparer.Ordinal);
                var kinds = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var task in manifest.Tasks)
                    foreach (var slot in task.Connections)
                        if (bindings.TryGetValue(slot.Name, out var boundTo))
                        {
                            kinds[boundTo] = slot.Kind;
                            Add(boundTo, slot.Kind, new ConnectionUse(ProcessKinds.Task, $"{package}/{task.Name}", UseRoles.Run, active.ToString(), group, null, head.Channel));
                        }

                // the instances that can run the version: that is where the logins have to be
                foreach (var status in publisher.Status.List(RunnerPackages.Kind, package, head.Channel))
                {
                    if (status.Gone || status.Version != active || status.State is not (InstanceStates.Active or InstanceStates.Ready)) continue;
                    foreach (var (boundTo, kind) in kinds)
                        Add(boundTo, kind, new ConnectionUse(ProcessKinds.Runner, package, UseRoles.Run, active.ToString(), group, status.Instance, head.Channel));
                }
            }
        }

        var connections = byConnection.Select(c => new InventoryConnection(c.Key, c.Value.Kind, null, null, null, SecretRef.LooksLikeRef(c.Key) ? c.Key : null, c.Value.Uses));
        var document = ConnectionInventoryJson.Write(product, connections);
        document["group"] = group;
        document["problems"] = problems;
        return document;
    }
}
