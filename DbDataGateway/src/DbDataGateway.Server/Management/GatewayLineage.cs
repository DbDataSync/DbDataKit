using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DbDataKit.Connections;
using DbDataKit.Lineage;
using DbDataKit.Serving.Semantic;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.State;

namespace DbDataGateway.Server.Management;

/// <summary>
/// The Gateway's part of the suite's lineage graph (plan 18, G08 and G11b), in the shared <c>DbDataKit.Lineage</c> types and the identity <see cref="LineageIdentity"/> gives a table, so a table the Gateway serves, the same table Sync writes and
/// the same table a Build model produces are one node. What is the Gateway's:
/// <list type="bullet">
/// <item>A <c>serving</c> node for each data source (<c>suite://&lt;instance&gt;/data-source/&lt;name&gt;</c>), and for each table that is known to be served, a <c>serves</c> edge from the table (a dataset by its identity) to the data source: <em>observed</em> when
/// statements read it, <em>derived</em> when a served semantic model names it. The Gateway does not list a data source's tables in the graph (that would be a live read of the database on every call, with a login the call does not have): a data source
/// shows the tables something used or modelled, and says so (<c>tables: used or modelled</c>).</item>
/// <item>The <b>run summary</b> of a table (G08): <c>reads</c>, <c>last_read</c> and the readers, as properties of its node, from <see cref="TableReadStore"/>; never a record per query (those belong to the Gateway's activity, under its own retention).
/// The names of the readers are for an administrator; anyone else gets only how many (<c>reader_count</c>).</item>
/// <item>The semantic models the Gateway serves (G11b): a <c>serving</c> node for each (<c>suite://&lt;instance&gt;/semantic-model/&lt;name&gt;</c>), a <c>semantic_member</c> node for each measure and attribute, a <c>binds</c> edge from the column
/// it reads to the member (derived from the catalog), and a <c>serves</c> edge from each member to the model. A column that is an expression and not a name has no column node: its table is bound to the member instead, with the expression as the detail.
/// A derived measure binds nothing (it is made of other measures) and says so by having no edge into it.</item>
/// </list>
/// A data source whose connection string cannot be read (a secret that is not there, an address that holds no server or database) is named by itself, so two of them never become one node and nothing joins wrongly; it is said in <paramref name="notes"/>.
/// </summary>
public static class GatewayLineageGraph
{
    private static readonly Regex Plain = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <param name="dataSources">The data sources the Gateway serves (its configuration, and what was published to the group), by name.</param>
    /// <param name="models">The semantic models it serves.</param>
    /// <param name="resolve">The connection string of a data source with its secret references resolved (<c>name</c>, as written); a failure is a data source with no address.</param>
    /// <param name="reads">What statements have read (<see cref="TableReadStore.Summaries"/>).</param>
    /// <param name="readers">Whether the names of the readers are told (an administrator's call).</param>
    public static LineageGraph Design(string instanceId, IReadOnlyDictionary<string, DataSourceOptions> dataSources, IEnumerable<SemanticModel> models, Func<string, string, string> resolve,
        IReadOnlyList<TableReadSummary> reads, bool readers, List<string>? notes = null)
    {
        var nodes = new Dictionary<string, LineageNode>(StringComparer.Ordinal);
        var edges = new List<LineageEdge>();
        var definitions = new Dictionary<string, ConnectionDefinition?>(StringComparer.OrdinalIgnoreCase);

        ConnectionDefinition? Definition(string source)
        {
            if (definitions.TryGetValue(source, out var known)) return known;
            ConnectionDefinition? made = null;
            if (dataSources.TryGetValue(source, out var options))
            {
                try
                {
                    var text = resolve(source, options.ConnectionString);
                    made = DefinitionOf(source, options, text);
                    // an address with no server and no database names nothing: the data source stands for itself
                    var probe = LineageIdentity.Dataset(made, null, "t");
                    if (probe.Namespace.EndsWith("://", StringComparison.Ordinal) && probe.Name == "t")
                    {
                        notes?.Add($"The data source `{source}` has no server or database in its connection string, so its tables are named by the data source and join with no other product's.");
                        made = Placeholder(source, options);
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    notes?.Add($"The data source `{source}` has a connection string that cannot be read ({ex.GetType().Name}), so its tables are named by the data source and join with no other product's.");
                    made = Placeholder(source, options);
                }
            }
            return definitions[source] = made;
        }

        string ServingNode(string source)
        {
            var id = LineageIdentity.SuiteObject(instanceId, "data-source", source);
            if (!nodes.ContainsKey(id))
                nodes[id] = new LineageNode(id, NodeKind.Serving, source, UnknownUpstream: false,
                    new Dictionary<string, string> { ["serving"] = "data-source", ["provider"] = dataSources[source].Provider.ToLowerInvariant(), ["tables"] = "used or modelled" });
            return id;
        }

        string Dataset(string source, string? schema, string table)
        {
            var identity = LineageIdentity.Dataset(Definition(source)!, schema, table).ToString();
            if (!nodes.ContainsKey(identity))
                nodes[identity] = new LineageNode(identity, NodeKind.Dataset, schema is null ? table : $"{schema}.{table}", UnknownUpstream: true, new Dictionary<string, string> { ["connection"] = source });
            return identity;
        }

        // the tables statements read: the data source's summary, observed
        foreach (var read in reads.Where(r => dataSources.ContainsKey(r.DataSource) && Definition(r.DataSource) is not null))
        {
            var id = Dataset(read.DataSource, read.Schema, read.Table);
            var properties = new Dictionary<string, string>(nodes[id].PropertyMap)
            {
                ["reads"] = read.Reads.ToString(CultureInfo.InvariantCulture),
                ["last_read"] = read.LastRead.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                ["reader_count"] = read.Readers.Count.ToString(CultureInfo.InvariantCulture),
            };
            if (readers) properties["readers"] = string.Join(", ", read.Readers.Take(20));
            nodes[id] = nodes[id] with { Properties = properties };
            edges.Add(new LineageEdge(id, ServingNode(read.DataSource), EdgeKind.Serves, Granularity.Table, Confidence.Observed));
        }

        foreach (var model in models.OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            var served = LineageIdentity.SuiteObject(instanceId, "semantic-model", model.Name);
            nodes[served] = new LineageNode(served, NodeKind.Serving, model.Name, UnknownUpstream: false,
                new Dictionary<string, string> { ["serving"] = "semantic-model", ["catalog"] = model.Name, ["version"] = model.Version?.ToString(CultureInfo.InvariantCulture) ?? "configured" });
            Bind(model, served);
        }
        return new LineageGraph(nodes.Values, edges);

        void Bind(SemanticModel model, string served)
        {
            var json = model.Catalog.Source;
            // the tables of the catalog by name: the serving source, the physical table and the columns' own names for each column
            var tables = new Dictionary<string, (string Source, string? Schema, string Table, Dictionary<string, string> Columns)>(StringComparer.Ordinal);
            foreach (var table in json["tables"] is JsonArray list ? list.OfType<JsonObject>() : [])
            {
                if (table["name"]?.GetValue<string>() is not { } name || table["x_source"]?.GetValue<string>() is not { } sourceId || table["source"]?.GetValue<string>() is not { } physical) continue;
                if (model.Ssas?.ContainsKey(SemanticModel.BaseSource(sourceId)) == true) continue;       // an Analysis Services source is not a table of a data source of the Gateway
                var serving = model.ServingSource(sourceId);
                if (!dataSources.ContainsKey(serving) || Definition(serving) is null) { notes?.Add($"The model `{model.Name}` reads `{sourceId}`, which no data source named `{serving}` serves here, so it has no tables in the graph."); continue; }
                var (schema, tableName) = Split(physical);
                var columns = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var column in table["columns"] is JsonArray cols ? cols.OfType<JsonObject>() : [])
                    if (column["name"]?.GetValue<string>() is { } columnName)
                        columns[columnName] = PlainExpression(column) ?? "";
                tables[name] = (serving, schema, tableName, columns);
            }

            void Member(string member, string kind, string table, string column, string? detail)
            {
                if (!tables.TryGetValue(table, out var t)) return;
                var datasetId = Dataset(t.Source, t.Schema, t.Table);
                var memberId = LineageIdentity.SuiteObject(instanceId, "semantic", $"{model.Name}/{member}");
                nodes[memberId] = new LineageNode(memberId, NodeKind.SemanticMember, $"{model.Name}: {member}", UnknownUpstream: false,
                    new Dictionary<string, string> { ["catalog"] = model.Name, ["member"] = member, ["member_kind"] = kind });
                var ds = nodes[datasetId];
                // the table is served by its data source, and by it because the model names it
                edges.Add(new LineageEdge(datasetId, ServingNode(t.Source), EdgeKind.Serves, Granularity.Table, nodes[datasetId].PropertyMap.ContainsKey("reads") ? Confidence.Observed : Confidence.Derived));
                var physicalColumn = t.Columns.TryGetValue(column, out var expression) ? expression : column;
                if (physicalColumn.Length > 0 && Plain.IsMatch(physicalColumn))
                {
                    var columnId = LineageIdentity.Column(Definition(t.Source)!, LineageIdentity.Dataset(Definition(t.Source)!, t.Schema, t.Table), physicalColumn);
                    if (!nodes.ContainsKey(columnId)) nodes[columnId] = new LineageNode(columnId, NodeKind.Column, physicalColumn, UnknownUpstream: true);
                    edges.Add(new LineageEdge(columnId, memberId, EdgeKind.Binds, Granularity.Column, Confidence.Derived, detail));
                }
                else
                    edges.Add(new LineageEdge(datasetId, memberId, EdgeKind.Binds, Granularity.Table, Confidence.Derived, detail ?? (expression is { Length: > 0 } ? expression : null)));
                edges.Add(new LineageEdge(memberId, served, EdgeKind.Serves, Granularity.Table, Confidence.Derived));
                _ = ds;
            }

            if (json["dimensions"] is JsonObject dimensions)
                foreach (var (dimension, node) in dimensions)
                    if (node?["attributes"] is JsonObject attributes)
                        foreach (var (attribute, a) in attributes)
                            if (a?["bindings"] is JsonObject bindings)
                                foreach (var (_, b) in bindings)
                                    if (b is JsonObject binding && binding["table"]?.GetValue<string>() is { } table && binding["column"]?.GetValue<string>() is { } column)
                                        Member($"{dimension}.{attribute}", "attribute", table, column, null);
            if (json["measures"] is JsonObject measures)
                foreach (var (name, m) in measures)
                    if (m is JsonObject measure && measure["of"] is JsonObject of && of["table"]?.GetValue<string>() is { } table && of["column"]?.GetValue<string>() is { } column)
                        Member(name, "measure", table, column, measure["agg"]?.GetValue<string>());
        }
    }

    // the Gateway's own configuration shape as the shared connection type: what lineage reads of it is the server and the database, and a DuckDB file is its path
    private static ConnectionDefinition DefinitionOf(string name, DataSourceOptions options, string connectionString)
    {
        var engine = options.Provider.ToLowerInvariant();
        if (engine == "duckdb")
        {
            var pairs = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
                .ToDictionary(p => p[0].Trim().ToLowerInvariant(), p => p[1].Trim(), StringComparer.Ordinal);
            var file = pairs.GetValueOrDefault("data source") ?? pairs.GetValueOrDefault("database") ?? pairs.GetValueOrDefault("filename");
            return new ConnectionDefinition { Name = name, Engine = engine, Address = file is null ? null : new AddressSpec { Database = file } };
        }
        return new ConnectionDefinition { Name = name, Engine = engine, ConnectionString = connectionString };
    }

    private static ConnectionDefinition Placeholder(string name, DataSourceOptions options) =>
        new() { Name = name, Engine = options.Provider.ToLowerInvariant(), Address = new AddressSpec { Host = "unresolved-" + name } };

    /// <summary>
    /// The newest run of each <c>serves</c> edge that statements used (plan 18, G10b): the last read of the table, laid along the edge from the table to its data source, as a run that succeeded (the Gateway counts statements and not rows, so it
    /// has no row count). Read from the node's <c>last_read</c>; an edge nothing read has no run.
    /// </summary>
    public static IReadOnlyList<EdgeRun> Runs(LineageGraph graph)
    {
        var runs = new List<EdgeRun>();
        foreach (var edge in graph.Edges.Where(e => e.Kind == EdgeKind.Serves && e.Confidence == Confidence.Observed))
            if (graph.Node(edge.From)?.PropertyMap.GetValueOrDefault("last_read") is { } text
                && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
                runs.Add(new EdgeRun(edge.From, edge.To, EdgeKind.Serves, at, RunOutcome.Succeeded));
        return runs;
    }

    /// <summary>A physical table as the catalog writes it (<c>marts.fct_sales</c>, <c>[dbo].[orders]</c>, <c>"S"."T"</c>) as its schema and table.</summary>
    internal static (string? Schema, string Table) Split(string physical)
    {
        var parts = physical.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(p => p.Trim('[', ']', '"', '`')).ToArray();
        return parts.Length switch { 0 => (null, physical), 1 => (null, parts[0]), _ => (parts[^2], parts[^1]) };
    }

    private static string? PlainExpression(JsonObject column)
    {
        if (column["expression"]?["dialects"] is not JsonArray dialects) return null;
        foreach (var d in dialects.OfType<JsonObject>())
            if (d["expression"]?.GetValue<string>() is { } text) return text.Trim().Trim('[', ']', '"', '`');
        return null;
    }
}

/// <summary>
/// <c>GET /api/v1/lineage</c> (plan 18, G08; viewer role): the Gateway's graph in the wire form every server's lineage has (<see cref="LineageJson"/>), so the console joins it with the others. The graph is built on request from the
/// data sources and models in force and the counted reads (<see cref="TableReadStore"/>), which are written first so a read a moment ago is in the answer. An administrator also gets the names of the readers.
/// </summary>
public sealed class GatewayLineage(Func<GatewayOptions> options, Func<IEnumerable<SemanticModel>> models, Func<string, string, string> resolve, TableReadRecorder recorder, TableReadStore reads, string instanceId)
{
    public const string Route = GatewayManagement.Prefix + "/lineage";

    public static bool Handles(string route) => route == Route;

    public ManagementResult Handle(string method, ManagementCaller caller)
    {
        if (method != "GET") return ManagementResult.Problem(405, "The lineage is read, not written.");
        if (!caller.Holds(ServerRole.Viewer)) return ManagementResult.Problem(403, "This call needs the viewer role.");
        try
        {
            var graph = Build(caller.Holds(ServerRole.Admin), out var notes);
            var body = LineageJson.Write(graph);
            body["runs"] = LineageJson.WriteRuns(GatewayLineageGraph.Runs(graph));
            if (notes.Count > 0) body["problems"] = new JsonArray([.. notes.Select(n => (JsonNode)JsonValue.Create(n)!)]);
            return ManagementResult.Ok(body);
        }
        catch (LineageGraphException ex) { return ManagementResult.Problem(500, $"The Gateway's lineage cannot be built: {ex.Message}"); }
    }

    /// <summary>The graph, for the command line's and the console's use alike.</summary>
    public LineageGraph Build(bool readers, out List<string> notes)
    {
        recorder.Flush();
        notes = [];
        return GatewayLineageGraph.Design(instanceId, options().DataSources, models(), resolve, reads.Summaries(), readers, notes);
    }
}
