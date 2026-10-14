namespace DbData.Odbc.Metadata;

/// <summary>
/// The catalog functions of ODBC and SQLGetInfo, as the raw result sets the specification fixes: columns are read by
/// position, as text, null for NULL. A null argument is a null pointer (not specified); an empty string is an empty
/// string. <see cref="OdbcCatalog"/> turns these rows into typed records; tests can fake this interface.
/// Nothing here prepares or executes a statement.
/// </summary>
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
}
