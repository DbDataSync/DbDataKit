using System.Data.Common;
using DbDataKit.Connections;
using DbDataKit.Dialect;
using DbDataKit.Semantic.Abstractions;

namespace DbDataKit.Semantic.Connections;

/// <summary>A catalog source's connection: the provider that opens it and the context to open it with (a read context, for a semantic query).</summary>
public sealed record SemanticSource(IDbProvider Provider, ConnectionContext Context);

/// <summary>
/// Runs the queries the semantic engine plans on the connections of <c>DbDataKit.Connections</c>. <paramref name="sources"/> maps a catalog
/// source name to its connection (SQL Server runs the T-SQL, PostgreSQL its own text, DuckDB the twin); a source that is not in the map, or whose dialect this executor cannot run, goes to <paramref name="fallback"/>.
/// </summary>
public sealed class ConnectionsSourceExecutor(IReadOnlyDictionary<string, SemanticSource> sources, ISourceExecutor? fallback = null) : IColumnarSourceExecutor
{
    /// <summary>False: agent-written native text is for a host that puts its statement classifier in front of it.</summary>
    public bool RunsNativeText => false;

    /// <inheritdoc />
    public QueryRows Execute(CompiledQuery query, int maxRows) => Route(query, maxRows, columnar: false).AsRows();

    /// <summary>Reads the rows as Arrow arrays where every column has an Arrow form here, otherwise as rows. A fallback executor answers in rows.</summary>
    public ColumnarOutcome ExecuteColumnar(CompiledQuery query, int maxRows) => Route(query, maxRows, columnar: true);

    private ColumnarOutcome Route(CompiledQuery query, int maxRows, bool columnar)
    {
        if (!sources.TryGetValue(query.Source, out var source)) return Fallback(query, maxRows, $"has no connection for source '{query.Source}'");
        var engine = source.Provider.Engine;
        SourceEngine? routed = engine == EngineId.SqlServer ? SourceEngine.SqlServer : engine == EngineId.Postgres ? SourceEngine.Postgres : engine == EngineId.DuckDb ? SourceEngine.DuckDb : null;
        if (routed is null) return Fallback(query, maxRows, $"cannot run source '{query.Source}': the {engine.Value} engine is not supported yet");
        var text = NativeRoute.For(routed.Value, query, out var why);
        return text is null ? Fallback(query, maxRows, why!) : Run(source, text.Text, text.Parameters, text.Named, maxRows, columnar);
    }

    /// <inheritdoc />
    public QueryRows ExecuteNativeText(string source, NativeQuery native, int maxRows) =>
        throw new NotSupportedException("Agent-written native text is not run by the connections executor (RunsNativeText is false).");

    private ColumnarOutcome Fallback(CompiledQuery query, int maxRows, string why) =>
        fallback is not null ? new(null, fallback.Execute(query, maxRows)) : throw new NotSupportedException($"The connections executor {why}.");

    private static ColumnarOutcome Run(SemanticSource source, string text, IReadOnlyList<object?> parameters, bool named, int maxRows, bool columnar)
    {
        var provider = source.Provider;
        var classifier = new FailureClassifier([provider.FailureMapper]);
        try
        {
            using var connection = provider.OpenAsync(source.Context).GetAwaiter().GetResult();
            using var cmd = connection.CreateCommand();
            cmd.CommandTimeout = source.Context.Definition.Timeouts.CommandS;
            cmd.CommandText = text;
            for (int i = 0; i < parameters.Count; i++) cmd.Parameters.Add(Parameter(cmd, named ? $"@p{i + 1}" : null, parameters[i]));
            return columnar ? QueryRowReader.ReadColumnar(cmd, maxRows) : new(null, QueryRowReader.Read(cmd, maxRows));
        }
        catch (Exception e) when (e is not (ConnectionFailureException or ConnectionPolicyException or OperationCanceledException))
        {
            throw classifier.ToException(e, provider.Engine);
        }
    }

    private static DbParameter Parameter(DbCommand cmd, string? name, object? value)
    {
        var p = cmd.CreateParameter();
        if (name is not null) p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        return p;
    }
}
