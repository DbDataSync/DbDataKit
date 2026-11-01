using System.Text.Json.Nodes;

namespace DbDataKit.Lineage;

/// <summary>
/// A graph as the servers send it to a console (plan 18, G09): <c>{"nodes": [{"id", "kind", "name", "unknown_upstream", "properties"?}], "edges": [{"from", "to", "kind", "granularity", "confidence", "detail"?}]}</c>, kinds in lower case.
/// Reading is strict: a document that is not this shape, or names a kind that does not exist, is a <see cref="LineageGraphException"/> saying where, so a server that sends something else is reported and not half-read.
/// </summary>
public static class LineageJson
{
    public static JsonObject Write(LineageGraph graph)
    {
        var nodes = new JsonArray();
        foreach (var n in graph.Nodes.OrderBy(n => n.Id, StringComparer.Ordinal))
        {
            var node = new JsonObject { ["id"] = n.Id, ["kind"] = n.Kind.ToString().ToLowerInvariant(), ["name"] = n.Name, ["unknown_upstream"] = n.UnknownUpstream };
            if (n.PropertyMap.Count > 0) node["properties"] = new JsonObject(n.PropertyMap.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, (JsonNode?)JsonValue.Create(p.Value))));
            nodes.Add(node);
        }
        var edges = new JsonArray();
        foreach (var e in graph.Edges)
        {
            var edge = new JsonObject
            {
                ["from"] = e.From, ["to"] = e.To, ["kind"] = e.Kind.ToString().ToLowerInvariant(),
                ["granularity"] = e.Granularity.ToString().ToLowerInvariant(), ["confidence"] = e.Confidence.ToString().ToLowerInvariant(),
            };
            if (e.Detail is not null) edge["detail"] = e.Detail;
            edges.Add(edge);
        }
        return new JsonObject { ["nodes"] = nodes, ["edges"] = edges };
    }

    public static LineageGraph Read(JsonNode? document)
    {
        if (document is not JsonObject root || root["nodes"] is not JsonArray nodeList || root["edges"] is not JsonArray edgeList)
            throw new LineageGraphException("A lineage graph is an object with `nodes` and `edges` lists.");
        var nodes = new List<LineageNode>();
        for (var i = 0; i < nodeList.Count; i++)
        {
            if (nodeList[i] is not JsonObject n) throw new LineageGraphException($"nodes[{i}] is not an object.");
            var properties = n["properties"] is JsonObject p ? p.ToDictionary(kv => kv.Key, kv => kv.Value?.GetValue<string>() ?? "") : null;
            nodes.Add(new LineageNode(Text(n, "id", $"nodes[{i}]"), Parse<NodeKind>(Text(n, "kind", $"nodes[{i}]"), $"nodes[{i}].kind"), Text(n, "name", $"nodes[{i}]"), n["unknown_upstream"]?.GetValue<bool>() ?? false, properties));
        }
        var edges = new List<LineageEdge>();
        for (var i = 0; i < edgeList.Count; i++)
        {
            if (edgeList[i] is not JsonObject e) throw new LineageGraphException($"edges[{i}] is not an object.");
            edges.Add(new LineageEdge(Text(e, "from", $"edges[{i}]"), Text(e, "to", $"edges[{i}]"), Parse<EdgeKind>(Text(e, "kind", $"edges[{i}]"), $"edges[{i}].kind"),
                Parse<Granularity>(Text(e, "granularity", $"edges[{i}]"), $"edges[{i}].granularity"), Parse<Confidence>(Text(e, "confidence", $"edges[{i}]"), $"edges[{i}].confidence"), e["detail"]?.GetValue<string>()));
        }
        return new LineageGraph(nodes, edges);
    }

    private static string Text(JsonObject o, string key, string where) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : throw new LineageGraphException($"{where} has no `{key}`.");

    private static T Parse<T>(string text, string where) where T : struct, Enum =>
        Enum.TryParse<T>(text, ignoreCase: true, out var value) && Enum.IsDefined(value) ? value : throw new LineageGraphException($"{where} is `{text}`, which is not one of: {string.Join(", ", Enum.GetNames<T>().Select(n => n.ToLowerInvariant()))}.");
}
