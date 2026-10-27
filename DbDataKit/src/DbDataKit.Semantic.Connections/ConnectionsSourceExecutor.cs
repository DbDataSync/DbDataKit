using System.Data.Common;
using DbDataKit.Connections;
using DbDataKit.Dialect;
using DbDataKit.Semantic.Abstractions;

namespace DbDataKit.Semantic.Connections;

/// <summary>A catalog source's connection: the provider that opens it and the context to open it with (a read context, for a semantic query).</summary>
public sealed record SemanticSource(IDbProvider Provider, ConnectionContext Context);

/// <summary>
/// Runs the queries the semantic engine plans on the connections of <c>DbDataKit.Connections</c>. <paramref name="sources"/> maps a catalog
/// source name to its connection; a source that is not in the map, or whose dialect this executor cannot run, goes to <paramref name="fallback"/>.
/// </summary>
public sealed class ConnectionsSourceExecutor(IReadOnlyDictionary<string, SemanticSource> sources, ISourceExecutor? fallback = null) : ISourceExecutor
{
    /// <summary>False: agent-written native text is for a host that puts its statement classifier in front of it.</summary>
    public bool RunsNativeText => false;

    /// <inheritdoc />
    public QueryRows Execute(CompiledQuery query, int maxRows)
    {
        if (!sources.TryGetValue(query.Source, out var source)) return Fallback(query, maxRows, $"has no connection for source '{query.Source}'");
        var engine = source.Provider.Engine;
        if (engine == EngineId.SqlServer)
        {
            if (query.Native.Dialect != "tsql") return Fallback(query, maxRows, $"cannot run {query.Native.Dialect} on source '{query.Source}' (SQL Server)");
            return Run(source, query.ExecText ?? query.Native.Text, query.DuckDbParameters, named: true, maxRows);
        }
        if (engine == EngineId.DuckDb)
            return Run(source, query.DuckDbSql, query.DuckDbParameters.Select(DuckDbValue).ToList(), named: false, maxRows);
        return Fallback(query, maxRows, $"cannot run source '{query.Source}': the {engine.Value} engine is not supported yet");
    }

    /// <inheritdoc />
    public QueryRows ExecuteNativeText(string source, NativeQuery native, int maxRows) =>
        throw new NotSupportedException("Agent-written native text is not run by the connections executor (RunsNativeText is false).");

    private QueryRows Fallback(CompiledQuery query, int maxRows, string why) =>
        fallback is not null ? fallback.Execute(query, maxRows) : throw new NotSupportedException($"The connections executor {why}.");

    private static QueryRows Run(SemanticSource source, string text, IReadOnlyList<object?> parameters, bool named, int maxRows)
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
            return QueryRowReader.Read(cmd, maxRows);
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

    /// <summary>
    /// DuckDB.NET sends a decimal parameter as text, which DuckDB then won't compare with a number. A whole number goes as a long and
    /// the rest as a double, so the same parameters stay exact for SQL Server.
    /// </summary>
    private static object? DuckDbValue(object? v) =>
        v is decimal d ? (d == Math.Truncate(d) && Math.Abs(d) < long.MaxValue ? (long)d : (double)d) : v;
}
