using DbData.Dialect;

namespace DbData.Metadata;

public enum TableKind { Table, View, MaterializedView, SystemTable, TemporaryTable, Other }

/// <summary>A table or view by its catalog parts. <see cref="Catalog"/> and <see cref="Schema"/> are null where the engine has none.</summary>
public sealed record TableRef(string? Catalog, string? Schema, string Name, TableKind Kind = TableKind.Table)
{
    /// <summary>The non-null parts in order (catalog, schema, name), ready for <c>ISqlDialect.QuoteQualified</c>.</summary>
    public IReadOnlyList<string> Parts => [.. new[] { Catalog, Schema }.OfType<string>(), Name];

    /// <summary>Identity ignoring <see cref="Kind"/> (a cache key part): parts joined with a NUL-free, length-prefixed form.</summary>
    public string Key => string.Join('|', new[] { Catalog, Schema, Name }.Select(p => p is null ? "-" : p.Length + ":" + p));

    public override string ToString() => string.Join('.', Parts);
}

/// <summary>
/// One column. <see cref="Type"/> is the canonical type; <see cref="NativeType"/> is the engine's own type text as reported
/// (<c>nvarchar(50)</c>). <see cref="IsIdentity"/>, <see cref="IsComputed"/> and <see cref="Collation"/> are only reliable where
/// <see cref="ShapeSupport.IdentityAndComputed"/> is true; elsewhere they are false/null because the source cannot say.
/// </summary>
public sealed record ColumnInfo(
    string Name, int Ordinal, CanonicalType Type, string NativeType, bool Nullable,
    string? DefaultExpr = null, bool IsIdentity = false, bool IsComputed = false, string? Collation = null);

/// <summary>A key (primary or unique) as an ordered column list.</summary>
public sealed record KeyInfo(string? Name, IReadOnlyList<string> Columns);

public enum ReferentialAction { Unknown, NoAction, Cascade, SetNull, SetDefault, Restrict }

public sealed record ForeignKeyInfo(
    string? Name, IReadOnlyList<string> Columns, TableRef Referenced, IReadOnlyList<string> ReferencedColumns,
    ReferentialAction OnUpdate = ReferentialAction.Unknown, ReferentialAction OnDelete = ReferentialAction.Unknown);

public sealed record IndexColumn(string Name, bool Descending = false);

public sealed record IndexInfo(string Name, bool Unique, IReadOnlyList<IndexColumn> Columns);

/// <summary>
/// Which parts of a <see cref="TableShape"/> the source could actually answer. False means "unknown", never "none": a driver
/// without SQLPrimaryKeys reports <c>PrimaryKeys = false</c> and an empty key, which is different from a table with no key.
/// </summary>
public sealed record ShapeSupport(
    bool PrimaryKeys = true, bool ForeignKeys = true, bool Indexes = true, bool RowEstimate = false, bool IdentityAndComputed = true)
{
    public static readonly ShapeSupport Full = new(RowEstimate: true);
}

/// <summary>A table's columns, keys, indexes and row estimate. <see cref="Warnings"/> carry truncation and partial-failure notes.</summary>
public sealed record TableShape(
    TableRef Table, IReadOnlyList<ColumnInfo> Columns, KeyInfo? PrimaryKey, IReadOnlyList<ForeignKeyInfo> ForeignKeys,
    IReadOnlyList<IndexInfo> Indexes, long? RowEstimate, ShapeSupport Support, IReadOnlyList<string>? Warnings = null)
{
    public IReadOnlyList<string> WarningList => Warnings ?? [];
}

/// <summary>How a query's result columns were found.</summary>
public enum DescribeStrategy
{
    None,
    /// <summary>SQL Server <c>sp_describe_first_result_set</c>.</summary>
    SqlServerDescribeFirstResultSet,
    /// <summary>Server-side prepared statement description (PostgreSQL parse/describe), nothing executed.</summary>
    PreparedStatement,
    /// <summary>ODBC <c>SQLPrepare</c> + <c>SQLDescribeCol</c>.</summary>
    OdbcPrepare,
    /// <summary>An engine statement that describes a query (DuckDB <c>DESCRIBE</c>), nothing executed.</summary>
    EngineDescribeStatement,
    /// <summary><c>SELECT * FROM (query) t WHERE 1 = 0</c> run through the read-only policy.</summary>
    WrappedZeroRowProbe,
}

/// <summary>The result columns of a query, found without fetching rows.</summary>
public sealed record QueryShape(IReadOnlyList<ColumnInfo> Columns, DescribeStrategy Strategy);

/// <summary>What a reader can answer. Consumers check these instead of catching failures.</summary>
public sealed record CatalogCapabilities(
    bool Catalogs = true, bool Schemas = true, bool ForeignKeys = true, bool Indexes = true, bool RowEstimates = false,
    bool Procedures = false, bool IdentityAndComputed = true, DescribeStrategy Describe = DescribeStrategy.None);

/// <summary>
/// What a server (or its driver) says about itself. Anything unreported is null. One record whether it came from ODBC SQLGetInfo,
/// a provider's connection properties or a probe query; <c>DbData.Metadata.DialectProfile</c> turns it into a dialect.
/// </summary>
public sealed record ServerFacts(
    string? ProductName = null, string? ProductVersion = null, string? DriverName = null,
    string? QuoteOpen = null, string? QuoteClose = null,
    IdentifierCase? StoredCase = null, IdentifierCase? QuotedCase = null, int? MaxIdentifierLength = null,
    string? CatalogSeparator = null, string? CatalogTerm = null, string? SchemaTerm = null, bool? ReadOnly = null,
    bool? SupportsTop = null, bool? SupportsLimit = null, bool? SupportsOffsetFetch = null, bool? SupportsMultipleStatements = null)
{
    /// <summary>True when the server reports no way to quote identifiers (an ODBC driver answering a single space).</summary>
    public bool HasQuoteCharacter => !string.IsNullOrWhiteSpace(QuoteOpen);
}
