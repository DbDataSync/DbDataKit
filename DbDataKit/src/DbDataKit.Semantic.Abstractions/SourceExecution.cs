namespace DbDataKit.Semantic.Abstractions;

/// <summary>One compiled source query: the native text for the real source, plus a DuckDB equivalent.
/// ExecText is the native text with the limit raised by one so truncation can be detected; it defaults to Native.Text.</summary>
public sealed record CompiledQuery(string Source, NativeQuery Native, string DuckDbSql, List<object?> DuckDbParameters, string? ExecText = null);

/// <summary>The rows a source returned: column names, and one dictionary per row.</summary>
public sealed record QueryRows(List<string> Columns, List<Dictionary<string, object?>> Rows);

/// <summary>Runs compiled queries. Production implementations run Native; a test stand-in runs DuckDbSql.</summary>
public interface ISourceExecutor
{
    /// <summary>True if this executor can run agent-written native text (level 0 batches).</summary>
    bool RunsNativeText { get; }

    /// <summary>Runs one compiled query and returns at most <paramref name="maxRows"/> rows.</summary>
    QueryRows Execute(CompiledQuery query, int maxRows);

    /// <summary>Runs agent-written native text (level 0).</summary>
    QueryRows ExecuteNativeText(string source, NativeQuery native, int maxRows);
}
