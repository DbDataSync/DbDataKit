namespace DbData.Odbc.Metadata;

/// <summary>What happens when a catalog call returns more rows than <see cref="OdbcCatalogOptions.MaxRows"/>.</summary>
public enum OdbcRowLimitBehavior
{
    /// <summary>Throw <see cref="OdbcResultTruncatedException"/>. Nothing is silently dropped.</summary>
    Throw = 0,

    /// <summary>Return the first <c>MaxRows</c> rows; the result carries an <see cref="OdbcResultTruncated"/> (see <see cref="OdbcResultList{T}"/>).</summary>
    Truncate = 1,
}

/// <summary>Limits and policy for one catalog connection. Immutable; use <c>with</c> to change a value.</summary>
public sealed record OdbcCatalogOptions
{
    /// <summary>The defaults: 30 s login timeout, 60 s statement timeout, 500,000 rows per call, <see cref="OdbcConnectionPolicy.Default"/>.</summary>
    public static OdbcCatalogOptions Default { get; } = new();

    /// <summary>SQL_ATTR_LOGIN_TIMEOUT. Null leaves the driver's default.</summary>
    public TimeSpan? LoginTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// SQL_ATTR_QUERY_TIMEOUT for every catalog statement (rounded up to whole seconds). Null means no limit. A driver that rejects
    /// the attribute is tolerated: the call then runs without a driver-side timeout (cancellation still works through SQLCancel).
    /// </summary>
    public TimeSpan? StatementTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The most rows one catalog call may return (SQLColumns without a table can be huge). Null means unlimited. Streaming calls
    /// (<c>StreamTablesAsync</c>, <c>StreamColumnsAsync</c>) are not capped; the consumer stops reading when it has enough.
    /// </summary>
    public int? MaxRows { get; init; } = 500_000;

    /// <summary>What to do when <see cref="MaxRows"/> is exceeded.</summary>
    public OdbcRowLimitBehavior RowLimitBehavior { get; init; } = OdbcRowLimitBehavior.Throw;

    /// <summary>Checked against the connection string before SQLDriverConnect is called.</summary>
    public OdbcConnectionPolicy Policy { get; init; } = OdbcConnectionPolicy.Default;
}
