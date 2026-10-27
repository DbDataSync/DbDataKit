using DbDataBuild.Execution;
using DbDataBuild.Models;
using DbDataBuild.State;
using DbDataKit.Connections;
using DbDataKit.Lineage;

namespace DbDataBuild.Cli;

/// <summary>
/// Build's part of the suite's lineage graph (plan 18, G05 and G06), in the shared <c>DbDataKit.Lineage</c> types and the identity <see cref="LineageIdentity"/> gives a table, so Build's tables and the same tables seen by Sync or Proxy are one node.
/// What is Build's: the model graph, the column lineage the query analysis computes (the same one the metadata documents and `project show graph --columns` carry) and the tracking tables.
/// <list type="bullet">
/// <item>A dataset node for every model on every connection it is built for or declared on, and for every table a query reads that is none of the project's (its upstream is unknown). A mapped model's upstream is unknown too (the tool does not build it); a native model's is
/// unknown when it declares no `reads:`.</item>
/// <item>Table level: a `derives` edge from each table a model reads to the model (derived from the query), from each `reads:` of a native model (declared), and a `copies` edge from a copy's origin to the copy (declared). These are the load edges runs attach to.</item>
/// <item>Column level, for built models other than copies: a `derives` edge from each upstream column to the output column, with the transform as its detail. A column of a native model, a copy or a table of the engine has no column lineage and says so (unknown upstream).</item>
/// </list>
/// A connection's identity needs its server and database, which a project does not hold (logins come from the environment): <c>connectionOf</c> supplies it (see <see cref="IdentityOf"/>). Without an address a connection is named by itself, so two of a project's
/// connections never become one node, and nothing joins with another product's node until the address is known.
/// </summary>
internal static class BuildLineage
{
    /// <summary>The connection a project names, as lineage identifies it: the address from its login when there is one in the environment, else a placeholder host made of the connection's name.</summary>
    public static Func<string, ConnectionDefinition> IdentityOf(ProjectConfig config, Func<string, string?> environment) => name =>
    {
        var (login, _) = LoginSettings.FromEnvironment(config, name, Login.Read, environment);
        if (login != null && login.AddressOnlyDefinition() is { ConnectionString.Length: > 0 } known) return known;
        return new ConnectionDefinition { Name = name, Engine = config.EngineOf(name) ?? name, Address = new AddressSpec { Host = "unresolved-" + name } };
    };

    /// <summary>The design lineage of the project: what its definitions say can flow where. Offline; nothing is connected to.</summary>
    public static LineageGraph Design(ProjectContext ctx, Func<string, ConnectionDefinition> connectionOf)
    {
        var nodes = new Dictionary<string, LineageNode>(StringComparer.Ordinal);
        var edges = new List<LineageEdge>();
        var definitions = new Dictionary<string, ConnectionDefinition>(StringComparer.Ordinal);
        ConnectionDefinition Definition(string connection) => definitions.TryGetValue(connection, out var d) ? d : definitions[connection] = connectionOf(connection);

        var built = ctx.Project.BuiltModels.ToDictionary(m => m.Definition.Name, StringComparer.OrdinalIgnoreCase);
        var declared = ctx.Project.DeclaredModels.Where(d => !d.Generated).ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);
        var known = built.Keys.Concat(declared.Keys).ToDictionary(n => n, n => n, StringComparer.OrdinalIgnoreCase);
        string Canonical(string table) => known.GetValueOrDefault(table, table);

        string DatasetId(string connection, string table)
        {
            var (schema, name) = DbDataBuild.Targets.Ddl.DdlGenerator.Split(Canonical(table));
            var def = Definition(connection);
            return LineageIdentity.Dataset(def, schema, name).ToString();
        }
        string Dataset(string connection, string table, string kind, bool unknownUpstream)
        {
            var id = DatasetId(connection, table);
            nodes[id] = new LineageNode(id, NodeKind.Dataset, Canonical(table), unknownUpstream, new Dictionary<string, string> { ["model_kind"] = kind, ["connection"] = connection });
            return id;
        }

        // the columns whose upstream the analysis gave (to a column of a model: an edge, or no edge when it is made of constants)
        var columnEdges = new List<(string Connection, ColumnEdge Edge)>();
        var analysed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in ctx.Project.BuiltModels.Where(m => !m.Definition.IsCopy).OrderBy(m => m.Definition.Name, StringComparer.Ordinal))
        {
            var edgesOfModel = ProjectGraph.ColumnEdges(ctx, model);
            foreach (var e in edgesOfModel)
            {
                analysed.Add(e.ToModel + "\n" + e.ToColumn);
                foreach (var connection in ctx.TargetsOf(model.Definition)) columnEdges.Add((connection, e));
            }
        }
        string ColumnId(string connection, string table, string column) => LineageIdentity.Column(Definition(connection), IdentityDataset(Definition(connection), Canonical(table)), column);
        DatasetIdentity IdentityDataset(ConnectionDefinition def, string table)
        {
            var (schema, name) = DbDataBuild.Targets.Ddl.DdlGenerator.Split(table);
            return LineageIdentity.Dataset(def, schema, name);
        }
        void Column(string connection, string table, string column, bool knownUpstream)
        {
            var id = ColumnId(connection, table, column);
            if (nodes.TryGetValue(id, out var existing) && !existing.UnknownUpstream) return;
            nodes[id] = new LineageNode(id, NodeKind.Column, column, !knownUpstream);
        }

        // built models: a dataset on each connection, its declared columns, and what it reads
        foreach (var model in ctx.Project.BuiltModels.OrderBy(m => m.Definition.Name, StringComparer.Ordinal))
        {
            var def = model.Definition;
            foreach (var connection in ctx.TargetsOf(def))
            {
                var self = Dataset(connection, def.Name, def.Kind, unknownUpstream: false);
                if (def.IsCopy)
                {
                    // a copy is table level: its origin is declared, its columns are the origin's and are not followed
                    if (def.From is { } from)
                    {
                        var origins = def.LocalCopy ? [connection] : ctx.OriginsOf(def, connection).Select(o => o.Connection).ToList();
                        foreach (var origin in origins)
                        {
                            var kind = built.TryGetValue(from, out var b) ? b.Definition.Kind : declared.TryGetValue(from, out var d) ? d.Kind : "unknown";
                            var originId = Dataset(origin, from, kind, UnknownUpstreamOf(from));
                            if (originId != self) edges.Add(new LineageEdge(originId, self, EdgeKind.Copies, Granularity.Table, Confidence.Declared));
                        }
                    }
                    continue;
                }
                foreach (var column in def.Columns) Column(connection, def.Name, column.Name, analysed.Contains(def.Name + "\n" + column.Name));
                foreach (var read in ctx.Graph.Reads(def.Name).Where(r => !r.EndsWith("()", StringComparison.Ordinal)))
                {
                    var kind = built.TryGetValue(read, out var b) ? b.Definition.Kind : declared.TryGetValue(read, out var d) ? d.Kind : "unknown";
                    var upstreamId = Dataset(connection, read, kind, UnknownUpstreamOf(read));
                    if (upstreamId != self) edges.Add(new LineageEdge(upstreamId, self, EdgeKind.Derives, Granularity.Table, Confidence.Derived));
                }
            }
        }

        // declared models: mapped ones are sources, native ones know what they say they read
        foreach (var d in declared.Values.OrderBy(d => d.Name, StringComparer.Ordinal))
            foreach (var connection in d.Connections ?? ctx.Config.DefaultConnections)
            {
                var self = Dataset(connection, d.Name, d.Kind, UnknownUpstreamOf(d.Name));
                if (!d.IsNative) continue;
                foreach (var read in d.Native!.Reads)
                {
                    var kind = built.TryGetValue(read, out var b) ? b.Definition.Kind : declared.TryGetValue(read, out var r) ? r.Kind : "unknown";
                    var upstreamId = Dataset(connection, read, kind, UnknownUpstreamOf(read));
                    if (upstreamId != self) edges.Add(new LineageEdge(upstreamId, self, EdgeKind.Derives, Granularity.Table, Confidence.Declared));
                }
            }

        // column level
        foreach (var (connection, e) in columnEdges)
        {
            if (e.FromTable == null || e.FromColumn == null) continue;
            var fromBuilt = built.TryGetValue(e.FromTable, out var fb) && !fb.Definition.IsCopy;
            if (!nodes.ContainsKey(DatasetId(connection, e.FromTable)))
            {
                var kind = fromBuilt ? fb!.Definition.Kind : declared.TryGetValue(e.FromTable, out var d) ? d.Kind : "unknown";
                Dataset(connection, e.FromTable, kind, UnknownUpstreamOf(e.FromTable));
            }
            // a column of a table the project builds from a query has its upstream in this graph; any other (a mapped table, a native model, a copy, a table of the engine) is where column lineage stops
            Column(connection, e.FromTable, e.FromColumn, fromBuilt && analysed.Contains(e.FromTable + "\n" + e.FromColumn));
            var from = ColumnId(connection, e.FromTable, e.FromColumn);
            var to = ColumnId(connection, e.ToModel, e.ToColumn);
            if (from != to) edges.Add(new LineageEdge(from, to, EdgeKind.Derives, Granularity.Column, Confidence.Derived, e.Transform));
        }
        return new LineageGraph(nodes.Values, edges);

        // what feeds a table is unknown to the project unless it builds it from a query or a copy, or a native model says what it reads
        bool UnknownUpstreamOf(string table)
        {
            if (built.ContainsKey(table)) return false;
            if (declared.TryGetValue(table, out var d)) return !d.IsNative || d.Native!.Reads.Count == 0;
            return true;
        }
    }

    /// <summary>
    /// The runs of the project's loads (G06): for each model on <paramref name="connection"/> that has run, the last run laid along every table-level edge into the model (a load reads all its inputs, so each of its load edges carries it), with when it
    /// ended, whether every step of it ended `ok`, and the rows it wrote. A model that reads nothing (a copy of nothing, constants) has no load edge to carry a run.
    /// </summary>
    public static IReadOnlyList<EdgeRun> Runs(LineageGraph design, ProjectContext ctx, string connection, ConnectionDefinition definition, IReadOnlyList<AuditLog.LastLoad> lastLoads)
    {
        var runs = new List<EdgeRun>();
        foreach (var load in lastLoads)
        {
            var (schema, name) = DbDataBuild.Targets.Ddl.DdlGenerator.Split(load.Model);
            var id = LineageIdentity.Dataset(definition, schema, name).ToString();
            var at = new DateTimeOffset(DateTime.SpecifyKind(load.EndedUtc, DateTimeKind.Utc));
            foreach (var edge in design.Upstream(id).Where(e => e.Granularity == Granularity.Table && e.Kind is EdgeKind.Derives or EdgeKind.Copies))
                runs.Add(new EdgeRun(edge.From, edge.To, edge.Kind, at, load.Succeeded ? RunOutcome.Succeeded : RunOutcome.Failed, load.Rows));
        }
        return runs;
    }

    /// <summary>The last run of every model on a connection from its tracking tables (read only).</summary>
    public static Task<IReadOnlyList<AuditLog.LastLoad>> LastLoadsAsync(ReadSession read, TrackingScope scope, CancellationToken ct = default) =>
        AuditLog.LastLoadsAsync(read, scope, HookEvents.All.Select(e => e.Name).ToList(), ct: ct);

    /// <summary>
    /// The design graph of one project, in the shape <c>LineageNoun.Create(source)</c> takes. The server's <c>lineage</c> noun (<see cref="Server.ServerLineage"/>) merges one of these per hosted project; Build's per-project command line has no <c>lineage</c> verb.
    /// </summary>
    public static Func<CancellationToken, Task<LineageGraph>> Source(string projectRoot, Func<string, string?> environment) => _ =>
    {
        var ctx = ProjectContext.Load(projectRoot);
        return Task.FromResult(Design(ctx, IdentityOf(ctx.Config, environment)));
    };
}
