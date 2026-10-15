namespace DbData.Odbc.Metadata;

/// <summary>A table or view (SQLTables). <paramref name="Type"/> is the driver's TABLE_TYPE, e.g. TABLE or VIEW.</summary>
public sealed record OdbcTable(string? Catalog, string? Schema, string Name, string Type, string? Remarks = null)
{
    public bool IsView => Type.Contains("VIEW", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A column as declared by the catalog (SQLColumns).</summary>
/// <param name="SqlType">The ODBC SQL data type code (SQL_VARCHAR = 12, SQL_INTEGER = 4, ...).</param>
/// <param name="Size">COLUMN_SIZE: length for character types, precision for numeric types.</param>
/// <param name="Scale">DECIMAL_DIGITS.</param>
public sealed record OdbcColumn(
    string? Catalog, string? Schema, string Table, string Name, int? SqlType, string? TypeName,
    int? Size, int? Scale, bool Nullable, int Ordinal, string? Default = null, string? Remarks = null);

/// <summary>The primary key of a table (SQLPrimaryKeys), columns in key order.</summary>
public sealed record OdbcPrimaryKey(string? Catalog, string? Schema, string Table, string? Name, IReadOnlyList<string> Columns);

/// <summary>A foreign key: the columns of <c>Table</c> and the columns they reference (SQLForeignKeys).</summary>
public sealed record OdbcForeignKey(
    string? Name, string? Catalog, string? Schema, string Table, IReadOnlyList<string> Columns,
    string? ReferencedCatalog, string? ReferencedSchema, string ReferencedTable, IReadOnlyList<string> ReferencedColumns,
    int? UpdateRule = null, int? DeleteRule = null);

/// <summary>A column of a statement's result (SQLDescribeCol). <paramref name="Nullable"/> is null when the driver says "unknown"; <paramref name="Size"/> is null when it reports 0.</summary>
public sealed record OdbcResultColumn(int Ordinal, string Name, int SqlType, int? Size, int? Scale, bool? Nullable);

/// <summary>One column of an index; Descending is false when the driver does not say.</summary>
public sealed record OdbcIndexColumn(string Name, bool Descending);

/// <summary>An index (SQLStatistics). The table's own statistics row is not an index and is not reported.</summary>
public sealed record OdbcIndex(string? Catalog, string? Schema, string Table, string Name, bool Unique, IReadOnlyList<OdbcIndexColumn> Columns);

/// <summary>How a driver folds the case of identifiers (SQL_IDENTIFIER_CASE and SQL_QUOTED_IDENTIFIER_CASE).</summary>
public enum OdbcIdentifierCase { Unknown = 0, Upper = 1, Lower = 2, Sensitive = 3, Mixed = 4 }

/// <summary>How a driver delimits identifiers: Prefix and Suffix are the same character for most drivers, different for [ ].</summary>
public sealed record OdbcIdentifierQuote(string Prefix, string Suffix)
{
    /// <summary>Quotes one identifier, doubling any embedded suffix so a name cannot escape the quotes.</summary>
    public string Apply(string identifier) => Prefix + identifier.Replace(Suffix, Suffix + Suffix, StringComparison.Ordinal) + Suffix;
}

/// <summary>What the driver and database say about themselves (SQLGetInfo). Anything not reported is null.</summary>
/// <param name="IdentifierQuote">Null when the driver reports no quoting (SQLGetInfo returns a single space).</param>
public sealed record OdbcDriverInfo(
    string? DbmsName, string? DbmsVersion, string? DriverName, OdbcIdentifierQuote? IdentifierQuote,
    OdbcIdentifierCase IdentifierCase, OdbcIdentifierCase QuotedIdentifierCase,
    string? SearchPatternEscape, string? CatalogSeparator, string? CatalogTerm, string? SchemaTerm);
