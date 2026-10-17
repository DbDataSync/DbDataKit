namespace DbDataKit.Odbc.Metadata;

/// <summary>
/// ODBC SQL data type codes (sql.h / sqlext.h, plus the common SQL Server driver extensions). The package exposes the raw codes;
/// mapping them to a canonical type belongs to a consumer.
/// </summary>
public static class OdbcSqlType
{
    public const int Unknown = 0, Char = 1, Numeric = 2, Decimal = 3, Integer = 4, SmallInt = 5, Float = 6, Real = 7, Double = 8,
        DateTime = 9, Varchar = 12, TypeDate = 91, TypeTime = 92, TypeTimestamp = 93,
        LongVarchar = -1, Binary = -2, VarBinary = -3, LongVarBinary = -4, BigInt = -5, TinyInt = -6, Bit = -7,
        WChar = -8, WVarchar = -9, WLongVarchar = -10, Guid = -11,
        IntervalYear = 101, IntervalMonth = 102, IntervalDay = 103, IntervalHour = 104, IntervalMinute = 105, IntervalSecond = 106,
        IntervalYearToMonth = 107, IntervalDayToHour = 108, IntervalDayToMinute = 109, IntervalDayToSecond = 110,
        IntervalHourToMinute = 111, IntervalHourToSecond = 112, IntervalMinuteToSecond = 113,
        // SQL Server: sql_variant.
        SqlServerVariant = -150,
        // SQL Server: CLR user-defined type.
        SqlServerUdt = -151,
        // SQL Server: xml.
        SqlServerXml = -152,
        // SQL Server: table-valued parameter.
        SqlServerTable = -153,
        // SQL Server: time with fractional seconds (SQL_SS_TIME2).
        SqlServerTime2 = -154,
        // SQL Server: datetimeoffset (SQL_SS_TIMESTAMPOFFSET).
        SqlServerTimestampOffset = -155;

    /// <summary>The ODBC name of a code (<c>SQL_VARCHAR</c> for 12), or <c>SQL_TYPE_</c> followed by the number for an unknown one.</summary>
    public static string Name(int code) => code switch
    {
        Char => "SQL_CHAR", Numeric => "SQL_NUMERIC", Decimal => "SQL_DECIMAL", Integer => "SQL_INTEGER", SmallInt => "SQL_SMALLINT",
        Float => "SQL_FLOAT", Real => "SQL_REAL", Double => "SQL_DOUBLE", DateTime => "SQL_DATETIME", Varchar => "SQL_VARCHAR",
        TypeDate => "SQL_TYPE_DATE", TypeTime => "SQL_TYPE_TIME", TypeTimestamp => "SQL_TYPE_TIMESTAMP",
        LongVarchar => "SQL_LONGVARCHAR", Binary => "SQL_BINARY", VarBinary => "SQL_VARBINARY", LongVarBinary => "SQL_LONGVARBINARY",
        BigInt => "SQL_BIGINT", TinyInt => "SQL_TINYINT", Bit => "SQL_BIT", WChar => "SQL_WCHAR", WVarchar => "SQL_WVARCHAR",
        WLongVarchar => "SQL_WLONGVARCHAR", Guid => "SQL_GUID",
        SqlServerVariant => "SQL_SS_VARIANT", SqlServerUdt => "SQL_SS_UDT", SqlServerXml => "SQL_SS_XML", SqlServerTable => "SQL_SS_TABLE",
        SqlServerTime2 => "SQL_SS_TIME2", SqlServerTimestampOffset => "SQL_SS_TIMESTAMPOFFSET",
        >= 101 and <= 113 => "SQL_INTERVAL_" + code,
        _ => "SQL_TYPE_" + code.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}

/// <summary>How a column can be used in a WHERE clause (SEARCHABLE of SQLGetTypeInfo).</summary>
public enum OdbcSearchable { Unknown = -1, Unsearchable = 0, LikeOnly = 1, AllExceptLike = 2, Searchable = 3 }

/// <summary>One data type a data source supports (SQLGetTypeInfo).</summary>
/// <param name="TypeName">The data source's name for the type (<c>varchar</c>, <c>datetime2</c>).</param>
/// <param name="SqlType">The ODBC SQL type code; see <see cref="OdbcSqlType"/>.</param>
/// <param name="ColumnSize">Maximum length or precision.</param>
/// <param name="CreateParams">What follows the name in DDL, e.g. <c>max length</c> or <c>precision,scale</c>.</param>
public sealed record OdbcTypeInfo(
    string TypeName, int SqlType, int? ColumnSize, string? LiteralPrefix, string? LiteralSuffix, string? CreateParams,
    bool Nullable, bool CaseSensitive, OdbcSearchable Searchable, bool? Unsigned, bool FixedPrecisionScale, bool? AutoUnique,
    string? LocalTypeName, int? MinimumScale, int? MaximumScale);

/// <summary>Whether a stored procedure result has a return value (PROCEDURE_TYPE of SQLProcedures).</summary>
public enum OdbcProcedureKind { Unknown = 0, NoResult = 1, ReturnsValue = 2 }

/// <summary>A stored procedure or function (SQLProcedures). Names have SQL Server's <c>;1</c> version suffix removed.</summary>
public sealed record OdbcProcedure(
    string? Catalog, string? Schema, string Name, int? InputParameters, int? OutputParameters, int? ResultSets, string? Remarks, OdbcProcedureKind Kind);

/// <summary>The role of a procedure column (COLUMN_TYPE of SQLProcedureColumns).</summary>
public enum OdbcParameterKind { Unknown = 0, Input = 1, InputOutput = 2, ResultColumn = 3, Output = 4, ReturnValue = 5 }

/// <summary>A parameter, return value or result column of a procedure (SQLProcedureColumns).</summary>
public sealed record OdbcProcedureColumn(
    string? Catalog, string? Schema, string Procedure, string Name, OdbcParameterKind Kind, int? SqlType, string? TypeName,
    int? Size, int? Scale, bool Nullable, int Ordinal, string? Default = null, string? Remarks = null);
