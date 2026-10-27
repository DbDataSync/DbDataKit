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

/// <summary>
/// A source's rows as one Arrow record batch (whole numbers Int64, numbers Double or Decimal128, booleans, and text for strings and dates), read without boxing each value.
/// <see cref="ToRows"/> gives the same rows <see cref="QueryRowReader.Read"/> would have.
/// </summary>
public sealed record ColumnarRows(List<string> Columns, Apache.Arrow.RecordBatch Batch)
{
    public int Count => Batch.Length;
    public QueryRows ToRows() => ArrowRows.ToRows(this);
}

/// <summary>What <see cref="IColumnarSourceExecutor.ExecuteColumnar"/> returns: the columns, or the row form when a column's type has no Arrow form here (the read does not run twice).</summary>
public sealed record ColumnarOutcome(ColumnarRows? Columnar, QueryRows? Rows)
{
    public QueryRows AsRows() => Rows ?? Columnar!.ToRows();
}

/// <summary>An executor that can also hand its rows over as Arrow. The engine asks for the columnar form where it only loads the rows into the sandbox.</summary>
public interface IColumnarSourceExecutor : ISourceExecutor
{
    ColumnarOutcome ExecuteColumnar(CompiledQuery query, int maxRows);
}
