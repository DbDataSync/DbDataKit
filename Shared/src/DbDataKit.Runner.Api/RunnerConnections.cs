using System.Text.Json.Nodes;
using DbDataKit.Connections;
using DbDataKit.Connections.Inventory;
using DbDataKit.Secrets;
using DbDataKit.State;

namespace DbDataKit.Runner.Api;

/// <summary>
/// The connections of a runner and who uses them (plan 28c ED17, E23): for every package with an active version in a channel, each connection slot its tasks declare, the name the channel binds it to (<c>definition_binding</c>), the task and
/// version that use it, and the runner instances that have that version active or ready. The runner holds the binding, a name or a reference, never a login: no value is read here.
/// <para>
/// Plan 28c E27 (G4, ED17): a connection is also described by the definition this runner holds under its name, read the way a run resolves it: a file in the runner's own connections directory first, else the copy the console sent (<c>connection_copy</c>).
/// <c>engine</c>, <c>host</c> and <c>database</c> come from that definition, <c>source</c> says where it came from (<see cref="SourceFile"/>, <see cref="SourceCopy"/>, <see cref="SourceReference"/> for a slot bound straight to a secret reference, <see cref="SourceUnknown"/> for a bound
/// name this runner holds nothing under), <c>version</c> is the version of its text (<see cref="ConnectionCopyVersion"/>, what the console compares), and a copy or a file <b>nothing is bound to</b> is listed with no use. Only references are read, never a value.
/// </para>
/// </summary>
public static class RunnerConnections
{
    /// <summary>The definition is a <c>connection/*</c> file of the runner's own connections directory (it answers to the name first, as a run resolves it).</summary>
    public const string SourceFile = "file";

    /// <summary>The definition is the copy the console sent (<c>connection_copy</c>).</summary>
    public const string SourceCopy = "copy";

    /// <summary>The slot is bound straight to a secret reference: there is no definition, and none is needed.</summary>
    public const string SourceReference = "reference";

    /// <summary>The slot is bound to a name this runner holds no file and no copy under: a run would fail naming it.</summary>
    public const string SourceUnknown = "unknown";

    /// <summary>What <c>GET /api/v1/connections</c> answers. <paramref name="group"/> is the runner group this state belongs to.</summary>
    /// <param name="state">The runner's state.</param>
    /// <param name="group">The runner group.</param>
    /// <param name="product">The product name written to the answer.</param>
    /// <param name="connectionsDirectory">The runner's own folder of <c>connection/*</c> files (<c>RunnerHost.ConnectionsDirectory</c>); null: only the copies are read.</param>
    public static JsonObject Inventory(StateDatabase state, string? group, string product, string? connectionsDirectory = null)
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

        var copies = new ConnectionCopies(state).List().ToDictionary(c => c.Name, StringComparer.Ordinal);
        var files = FilesOf(connectionsDirectory, problems);
        var connections = new List<InventoryConnection>();
        foreach (var (name, entry) in byConnection)
            connections.Add(Describe(name, entry.Kind, entry.Uses, files, copies, problems));
        // what this runner holds that nothing is bound to: a file of its own, a copy the console sent
        foreach (var name in files.Keys.Concat(copies.Keys).Distinct(StringComparer.Ordinal).Where(n => !byConnection.ContainsKey(n)))
            connections.Add(Describe(name, null, [], files, copies, problems));
        var document = ConnectionInventoryJson.Write(product, connections);
        document["group"] = group;
        document["problems"] = problems;
        return document;
    }

    /// <summary>The <c>connection/*</c> files of the directory by name, with their text (secret references, never a value). A file that cannot be read is named in <paramref name="problems"/> and is not listed.</summary>
    private static Dictionary<string, string> FilesOf(string? directory, JsonArray problems)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        if (directory is null || !Directory.Exists(directory)) return files;
        foreach (var path in Directory.EnumerateFiles(directory).Where(f => f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            try { files.TryAdd(name, File.ReadAllText(path)); }
            catch (IOException ex) { problems.Add($"The connection file '{Path.GetFileName(path)}' cannot be read: {ex.Message}"); }
        }

        return files;
    }

    private static InventoryConnection Describe(string name, string? slotKind, List<ConnectionUse> uses, IReadOnlyDictionary<string, string> files, IReadOnlyDictionary<string, ConnectionCopy> copies, JsonArray problems)
    {
        // a file answers to the name before a copy does, as a run resolves it
        string source, text = "", version = "";
        var hasText = true;
        if (!SecretRef.LooksLikeRef(name) && files.TryGetValue(name, out var file)) { source = SourceFile; text = file; version = ConnectionCopyVersion.Of(file); }
        else if (copies.TryGetValue(name, out var copy)) { source = SourceCopy; text = copy.Definition; version = copy.Version; }
        else { source = SecretRef.LooksLikeRef(name) ? SourceReference : SourceUnknown; hasText = false; }

        ConnectionDefinition? definition = null;
        if (hasText)
        {
            var load = ConnectionDefinitionLoader.LoadConnectionOfAnyKind(text, $"the {source} of the connection '{name}'");
            if (load.Ok) definition = load.Definition;
            else problems.Add($"The {source} of the connection '{name}' does not load, so its engine and host are not shown: send it again from the console.");
        }

        var kind = slotKind ?? definition?.KindName ?? "database";
        var secretRef = definition is not null ? ConnectionSecretReferences.Of(definition).Select(r => r.Reference).FirstOrDefault() : SecretRef.LooksLikeRef(name) ? name : null;
        return new InventoryConnection(name, kind, EngineOf(definition), HostOf(definition), definition?.Address?.Database, secretRef, uses, Source: source, Version: hasText ? version : null);
    }

    private static string? EngineOf(ConnectionDefinition? definition) => definition is { Engine: { Length: > 0 } engine } ? engine : null;

    private static string? HostOf(ConnectionDefinition? definition)
    {
        if (definition is null) return null;
        if (definition.Address is { Host: { Length: > 0 } host } address) return address.Port is { } port ? $"{host}:{port}" : host;
        // an outside system: the address of its endpoint template, when the host part is a plain name
        return definition.Endpoint is { Length: > 0 } endpoint && Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri.Authority : null;
    }
}
