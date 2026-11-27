using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Connections;

namespace DbDataKit.Lineage;

/// <summary>
/// The design lineage a Runner package carries (plan 18 G18): the file <see cref="Path"/> of the package, <c>{"schema": "package-lineage/1", "graph": {nodes, edges}}</c> in the wire form of <see cref="LineageJson"/>, written by the product that builds the
/// package (Build's models and column edges, Sync's replications) and read by the Runner's lineage route, which lays it beside the deployment, so the console's Lineage page has the graph the deleted Build and Sync servers served.
/// <para>A package is built where its connections' addresses are not known (a Build project takes them from the logins of the environment it runs in): a table of such a connection is named by its slot, with the host <c>@&lt;slot&gt;</c>
/// (<see cref="SlotConnection"/>), and the Runner names it again with the connection the channel binds to the slot (<see cref="Resolve"/>), so the node is the one the run reports (<c>run.Lineage</c>) and a Gateway serves. A product that knows
/// the addresses when it builds (Sync's repository holds them) writes the tables by their own identity, and nothing is resolved.</para>
/// </summary>
public static class PackageLineage
{
    /// <summary>The file in the package (under its <c>files/</c>).</summary>
    public const string Path = "lineage/design.json";

    /// <summary>The value of <c>schema</c>.</summary>
    public const string Schema = "package-lineage/1";

    /// <summary>The host of a table that is named by its slot.</summary>
    public const string SlotPrefix = "@";

    /// <summary>The connection a slot stands for while the package is built: its engine and the host <c>@&lt;slot&gt;</c>, no database (the bound connection's).</summary>
    public static ConnectionDefinition SlotConnection(string slot, string engine) =>
        new() { Name = slot, Engine = engine, Address = new AddressSpec { Host = SlotPrefix + slot } };

    /// <summary>The document, LF and indented, the same bytes for the same graph (so publishing it again sends nothing).</summary>
    public static string Write(LineageGraph graph)
    {
        var document = new JsonObject { ["schema"] = Schema, ["graph"] = LineageJson.Write(graph) };
        return document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n") + "\n";
    }

    /// <summary>The graph of a document; a document of another schema or shape is a <see cref="LineageGraphException"/> naming <paramref name="file"/> (a newer number says so: a clean break, never a silent one).</summary>
    public static LineageGraph Read(string text, string file)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(text); }
        catch (JsonException ex) { throw new LineageGraphException($"{file} is not JSON.", ex); }
        var schema = root is JsonObject o && o["schema"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        if (schema != Schema)
            throw new LineageGraphException(schema is null ? $"{file} has no `schema`; this reads {Schema}." : $"{file} is `{schema}`; this reads {Schema} (a newer package needs a newer runner).");
        try { return LineageJson.Read(root!["graph"]); }
        catch (LineageGraphException ex) { throw new LineageGraphException($"{file}: {ex.Message}", ex); }
    }

    /// <summary>The slot a dataset or column id names, or null when it names a table by its own identity (<c>engine://@slot/schema.table[#column]</c>).</summary>
    public static string? SlotOf(string id)
    {
        var scheme = id.IndexOf("://", StringComparison.Ordinal);
        if (scheme <= 0 || string.CompareOrdinal(id, scheme + 3, SlotPrefix, 0, SlotPrefix.Length) != 0) return null;
        var end = id.IndexOf('/', scheme + 3);
        return end < 0 ? null : id[(scheme + 3 + SlotPrefix.Length)..end];
    }

    /// <summary>
    /// The graph with every table and column of a slot named by the connection <paramref name="connectionOfSlot"/> gives for it (the one the channel binds, as the Runner holds it): <c>engine://@slot/schema.table#column</c> becomes the identity
    /// <see cref="LineageIdentity.Dataset"/> and <see cref="LineageIdentity.Column"/> give. A slot it gives none for is left as it is and said once in <paramref name="problems"/>, so its tables stay apart rather than joining a wrong node.
    /// </summary>
    public static LineageGraph Resolve(LineageGraph graph, Func<string, ConnectionDefinition?> connectionOfSlot, List<string> problems)
    {
        var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        var known = new Dictionary<string, ConnectionDefinition?>(StringComparer.Ordinal);
        string Rename(string id)
        {
            if (renamed.TryGetValue(id, out var done)) return done;
            if (SlotOf(id) is not { } slot) return renamed[id] = id;
            if (!known.TryGetValue(slot, out var connection)) known[slot] = connection = connectionOfSlot(slot);
            if (connection is null) { missing.Add(slot); return renamed[id] = id; }
            var rest = id[(id.IndexOf('/', id.IndexOf("://", StringComparison.Ordinal) + 3) + 1)..];
            var hash = rest.IndexOf('#', StringComparison.Ordinal);
            var name = hash < 0 ? rest : rest[..hash];
            var dot = name.IndexOf('.', StringComparison.Ordinal);
            var dataset = LineageIdentity.Dataset(connection, dot < 0 ? null : name[..dot], dot < 0 ? name : name[(dot + 1)..]);
            return renamed[id] = hash < 0 ? dataset.ToString() : LineageIdentity.Column(connection, dataset, rest[(hash + 1)..]);
        }

        var nodes = graph.Nodes.Select(n => n with { Id = Rename(n.Id) }).ToList();
        var edges = graph.Edges.Select(e => e with { From = Rename(e.From), To = Rename(e.To) }).Where(e => e.From != e.To).ToList();
        foreach (var slot in missing) problems.Add($"The slot `{slot}` is not bound to a connection this runner has, so its tables are named by the slot.");
        return new LineageGraph(nodes, edges);
    }

    /// <summary>
    /// What ran along the graph (plan 18 G06, G10b, on the Runner): for each dataset a task reported it wrote (<c>run.Lineage</c>), the newest run that wrote it laid along every table-level edge into it and every column edge into one of its columns,
    /// as the deleted Build and Sync servers laid their loads. <paramref name="written"/> is the dataset with when that run ended and how.
    /// </summary>
    public static IReadOnlyList<EdgeRun> Runs(LineageGraph graph, IEnumerable<(string Dataset, DateTimeOffset At, RunOutcome Outcome)> written)
    {
        var newest = new Dictionary<string, (DateTimeOffset At, RunOutcome Outcome)>(StringComparer.Ordinal);
        foreach (var (dataset, at, outcome) in written)
            if (!newest.TryGetValue(dataset, out var seen) || seen.At < at) newest[dataset] = (at, outcome);
        var columnsOf = graph.Nodes.Where(n => n.Kind == NodeKind.Column && n.Id.LastIndexOf('#') > 0)
            .GroupBy(n => n.Id[..n.Id.LastIndexOf('#')], StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(n => n.Id).ToList(), StringComparer.Ordinal);
        var runs = new List<EdgeRun>();
        foreach (var (dataset, (at, outcome)) in newest.OrderBy(d => d.Key, StringComparer.Ordinal))
        {
            if (graph.Node(dataset) is null) continue;
            foreach (var edge in graph.Upstream(dataset).Where(e => e.Granularity == Granularity.Table && e.Kind is EdgeKind.Derives or EdgeKind.Copies or EdgeKind.Replicates))
                runs.Add(new EdgeRun(edge.From, edge.To, edge.Kind, at, outcome));
            foreach (var column in columnsOf.GetValueOrDefault(dataset) ?? [])
                foreach (var edge in graph.Upstream(column).Where(e => e.Granularity == Granularity.Column && e.Kind is EdgeKind.Derives or EdgeKind.Copies or EdgeKind.Replicates))
                    runs.Add(new EdgeRun(edge.From, edge.To, edge.Kind, at, outcome));
        }
        return runs;
    }

    /// <summary>The bytes of <paramref name="text"/> as the package carries them (UTF-8, no mark).</summary>
    public static byte[] Bytes(string text) => new UTF8Encoding(false).GetBytes(text);
}
