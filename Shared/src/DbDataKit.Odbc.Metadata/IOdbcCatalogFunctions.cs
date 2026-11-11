using System.Runtime.CompilerServices;

namespace DbDataKit.Odbc.Metadata;

/// <summary>
/// The catalog functions of ODBC and SQLGetInfo, as the raw result sets the specification fixes: columns are read by
/// position, as text, null for NULL. A null argument is a null pointer (not specified); an empty string is an empty
/// string. <see cref="OdbcCatalog"/> turns these rows into typed records; tests can fake this interface.
/// Nothing here prepares or executes a statement.
/// </summary>
/// <remarks>
/// The members added after 0.1 have default implementations, so an existing implementation keeps compiling: the type, procedure
/// and streaming functions report "not supported" (SQLSTATE IM001) until overridden, and streaming falls back to the buffered call.
/// </remarks>
public interface IOdbcCatalogFunctions : IAsyncDisposable
{
    /// <summary>SQLTables: TABLE_CAT, TABLE_SCHEM, TABLE_NAME, TABLE_TYPE, REMARKS. "%" with the others empty lists catalogs or schemas.</summary>
    Task<IReadOnlyList<string?[]>> TablesAsync(string? catalog, string? schema, string? table, string? types, CancellationToken cancellationToken);

    /// <summary>SQLColumns: the 18 columns of the specification.</summary>
    Task<IReadOnlyList<string?[]>> ColumnsAsync(string? catalog, string? schema, string? table, string? column, CancellationToken cancellationToken);

    /// <summary>SQLPrimaryKeys: TABLE_CAT, TABLE_SCHEM, TABLE_NAME, COLUMN_NAME, KEY_SEQ, PK_NAME.</summary>
    Task<IReadOnlyList<string?[]>> PrimaryKeysAsync(string? catalog, string? schema, string table, CancellationToken cancellationToken);

    /// <summary>
    /// SQLForeignKeys for the keys the table has: PKTABLE_CAT, PKTABLE_SCHEM, PKTABLE_NAME, PKCOLUMN_NAME, FKTABLE_CAT, FKTABLE_SCHEM,
    /// FKTABLE_NAME, FKCOLUMN_NAME, KEY_SEQ, UPDATE_RULE, DELETE_RULE, FK_NAME, PK_NAME, DEFERRABILITY.
    /// </summary>
    Task<IReadOnlyList<string?[]>> ForeignKeysAsync(string? catalog, string? schema, string table, CancellationToken cancellationToken);

    /// <summary>SQLStatistics, all indexes: TABLE_CAT, TABLE_SCHEM, TABLE_NAME, NON_UNIQUE, INDEX_QUALIFIER, INDEX_NAME, TYPE, ORDINAL_POSITION, COLUMN_NAME, ASC_OR_DESC, CARDINALITY, PAGES, FILTER_CONDITION.</summary>
    Task<IReadOnlyList<string?[]>> StatisticsAsync(string? catalog, string? schema, string table, CancellationToken cancellationToken);

    /// <summary>SQLGetInfo for an information type that returns a string.</summary>
    Task<string?> GetInfoStringAsync(ushort infoType, CancellationToken cancellationToken);

    /// <summary>SQLGetInfo for an information type that returns a SQLUSMALLINT or SQLUINTEGER.</summary>
    Task<uint> GetInfoNumberAsync(ushort infoType, CancellationToken cancellationToken);

    /// <summary>
    /// SQLGetTypeInfo (<paramref name="sqlType"/> 0 is SQL_ALL_TYPES): TYPE_NAME, DATA_TYPE, COLUMN_SIZE, LITERAL_PREFIX, LITERAL_SUFFIX,
    /// CREATE_PARAMS, NULLABLE, CASE_SENSITIVE, SEARCHABLE, UNSIGNED_ATTRIBUTE, FIXED_PREC_SCALE, AUTO_UNIQUE_VALUE, LOCAL_TYPE_NAME,
    /// MINIMUM_SCALE, MAXIMUM_SCALE, SQL_DATA_TYPE, SQL_DATETIME_SUB, NUM_PREC_RADIX, INTERVAL_PRECISION.
    /// </summary>
    Task<IReadOnlyList<string?[]>> TypeInfoAsync(int sqlType, CancellationToken cancellationToken) =>
        throw new OdbcCatalogException("SQLGetTypeInfo", "IM001");

    /// <summary>SQLProcedures: PROCEDURE_CAT, PROCEDURE_SCHEM, PROCEDURE_NAME, NUM_INPUT_PARAMS, NUM_OUTPUT_PARAMS, NUM_RESULT_SETS, REMARKS, PROCEDURE_TYPE.</summary>
    Task<IReadOnlyList<string?[]>> ProceduresAsync(string? catalog, string? schema, string? procedure, CancellationToken cancellationToken) =>
        throw new OdbcCatalogException("SQLProcedures", "IM001");

    /// <summary>
    /// SQLProcedureColumns: PROCEDURE_CAT, PROCEDURE_SCHEM, PROCEDURE_NAME, COLUMN_NAME, COLUMN_TYPE, DATA_TYPE, TYPE_NAME, COLUMN_SIZE,
    /// BUFFER_LENGTH, DECIMAL_DIGITS, NUM_PREC_RADIX, NULLABLE, REMARKS, COLUMN_DEF, SQL_DATA_TYPE, SQL_DATETIME_SUB, CHAR_OCTET_LENGTH,
    /// ORDINAL_POSITION, IS_NULLABLE.
    /// </summary>
    Task<IReadOnlyList<string?[]>> ProcedureColumnsAsync(string? catalog, string? schema, string? procedure, string? column, CancellationToken cancellationToken) =>
        throw new OdbcCatalogException("SQLProcedureColumns", "IM001");

    /// <summary>
    /// SQLPrepare followed by SQLNumResultCols and SQLDescribeCol for each column: the result columns of a statement without executing it.
    /// Drivers that cannot describe an unexecuted statement fail with their own SQLSTATE (IM001 here when not implemented).
    /// </summary>
    Task<IReadOnlyList<OdbcResultColumn>> DescribeQueryAsync(string sql, CancellationToken cancellationToken) =>
        throw new OdbcCatalogException("SQLPrepare", "IM001");

    /// <summary>SQLTables as a stream, rows in driver order, not capped by <see cref="OdbcCatalogOptions.MaxRows"/>. Disposing the enumerator early frees the statement.</summary>
    IAsyncEnumerable<string?[]> StreamTablesAsync(string? catalog, string? schema, string? table, string? types, CancellationToken cancellationToken) =>
        OdbcStreams.From(TablesAsync(catalog, schema, table, types, cancellationToken));

    /// <summary>SQLColumns as a stream, rows in driver order, not capped by <see cref="OdbcCatalogOptions.MaxRows"/>.</summary>
    IAsyncEnumerable<string?[]> StreamColumnsAsync(string? catalog, string? schema, string? table, string? column, CancellationToken cancellationToken) =>
        OdbcStreams.From(ColumnsAsync(catalog, schema, table, column, cancellationToken));
}

internal static class OdbcStreams
{
    public static async IAsyncEnumerable<string?[]> From(Task<IReadOnlyList<string?[]>> rows, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var row in await rows.ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return row;
        }
    }
}
