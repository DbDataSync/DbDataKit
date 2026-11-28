using DbDataKit.Dialect;
using System.Data.Common;

namespace DbDataKit.Sync.Core.Sql;

/// <summary>SQL Server's answers to the mechanical variations in <see cref="SqlDialect"/>: bracket
/// quoting and <c>@</c>-prefixed parameters, both in statement text and when binding.</summary>
public sealed class MsSqlDialect : SqlDialect
{
    public static MsSqlDialect Instance { get; } = new();

    private MsSqlDialect() { }

    private static readonly ISqlDialect Shared = DialectCatalog.Builtin.Get("sqlserver");

    public override string QuoteIdentifier(string identifier) => SharedQuoting.Quote(Shared, identifier);

    public override string ParameterReference(string name) => $"@{name}";

    /// <summary>SQL Server caps a request at 2100 parameters.</summary>
    public override int MaxParametersPerStatement => 2100;

    /// <summary>SQL Server puts its row limit at the front, and has no <c>FETCH FIRST … WITH TIES</c>.
    /// The parentheses around the parameter are required for a parameterised <c>TOP</c>.</summary>
    public override (string Prefix, string Suffix) RenderTieSafeRowLimit(string parameterName) =>
        ($"TOP ({ParameterReference(parameterName)}) WITH TIES ", "");

    public override (string Prefix, string Suffix) RenderRowLimit(int n) => ($"TOP ({n}) ", "");

    /// <summary>Purely declarative — matches the rendering above, doesn't drive it.
    /// <c>SupportsTieSafeRowLimit</c> is left at the base class's own default (true): SQL Server's
    /// <c>TOP (n) WITH TIES</c> really does promise it.</summary>
    public override RowLimitStyle RowLimitStyle => RowLimitStyle.TopN;

    /// <summary>SQL Server has no <c>GENERATED ALWAYS AS IDENTITY</c>; the key is clustered because
    /// staging is only ever appended to in ordinal order, so the insert stays at the end of it.</summary>
    public override string RenderStagingOrdinalColumn(string column) =>
        $"{QuoteIdentifier(column)} BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY CLUSTERED";

    /// <summary>The spellings the shared list does not carry. Everything else falls through to the
    /// base classification, so this is SQL Server's additions rather than a restatement.</summary>
    public override BucketableKind ClassifyForBucketing(string baseTypeName) => baseTypeName switch
    {
        "money" or "smallmoney" => BucketableKind.Numeric,
        "smalldatetime" or "datetime2" => BucketableKind.DateTime,
        "datetimeoffset" => BucketableKind.DateTimeOffset,
        _ => base.ClassifyForBucketing(baseTypeName),
    };

    /// <summary>Explicit values for an IDENTITY column need the session flag, which SQL Server permits
    /// on one table at a time — so it is always turned back off, including when the write fails.</summary>
    public override Task<T> WriteWithGeneratedColumnOverrideAsync<T>(
        DbConnection connection,
        DbTransaction? transaction,
        string qualifiedTable,
        bool overrideRequired,
        Func<Task<T>> write,
        CancellationToken cancellationToken) =>
        MsSqlIdentityInsert.RunAsync(connection, transaction, qualifiedTable, overrideRequired, write, cancellationToken);

    /// <summary>SQL Server's ADD takes no COLUMN keyword.</summary>
    public override string RenderAddColumn(string qualifiedTable, string column, string type) =>
        $"ALTER TABLE {qualifiedTable} ADD {QuoteIdentifier(column)} {type} NULL";

    /// <summary>SQL Server spells the type change ALTER COLUMN &lt;name&gt; &lt;type&gt;, with no TYPE
    /// keyword.</summary>
    public override string? RenderAlterColumnType(string qualifiedTable, string column, string type) =>
        $"ALTER TABLE {qualifiedTable} ALTER COLUMN {QuoteIdentifier(column)} {type} NULL";

    /// <summary>
    /// SQL Server has no <c>ALTER TABLE … RENAME COLUMN</c>; the rename is a stored procedure whose
    /// first argument is the column's *qualified* name as a string literal and whose second is the new
    /// name **unqualified** — passing a qualified new name is the classic way to end up with a column
    /// literally called <c>dbo.T.NewName</c>.
    /// </summary>
    public override string RenderRenameColumn(string qualifiedTable, string from, string to) =>
        $"EXEC sp_rename N'{Literal($"{qualifiedTable}.{QuoteIdentifier(from)}")}', N'{Literal(to)}', 'COLUMN'";

    private static string Literal(string value) => value.Replace("'", "''");

    /// <summary>SQL Server wraps identifiers in brackets, not ANSI double quotes.</summary>
    protected override char IdentifierQuoteOpen => '[';

    protected override char IdentifierQuoteClose => ']';

    /// <summary>SQL Server's canonical Boolean is <c>bit</c>, which takes 1 and 0 and refuses TRUE.</summary>
    public override string TrueLiteral => "1";

    public override string FalseLiteral => "0";

    /// <summary>SQL Server concatenates with <c>+</c>; <c>||</c> is a syntax error.</summary>
    public override string Concat(IEnumerable<string> expressions) => string.Join(" + ", expressions);

    /// <summary>SQL Server has no LIMIT.</summary>
    public override string RenderSampleSelect(string qualifiedTable, int rows) =>
        $"SELECT TOP ({rows}) * FROM {qualifiedTable}";

    /// <summary>Plain <c>DECLARE @name type = literal;</c>, one per line — valid on its own ahead of
    /// any batch, which is what makes pasting this block followed by the statement it belongs to just
    /// work in SSMS or any other tool that runs a script rather than one bound statement at a time.</summary>
    public override string? RenderDeclarations(IReadOnlyList<PreviewParameter> parameters) =>
        parameters.Count == 0 ? null : string.Join(
            "\n", parameters.Select(p => $"DECLARE {ParameterReference(p.Name)} {p.SqlType} = {p.Literal};"));

    /// <summary>The shared <c>sqlserver</c> table (<c>dialects/sqlserver.yaml</c>): <c>tinyint</c> is an unsigned 8-bit integer, <c>binary(n)</c>
    /// and <c>rowversion</c> are fixed-length binary.</summary>
    public override TypeMapping<CanonicalType> ReadColumnType(string nativeType) => DialectTypes.Read(Shared, nativeType);

    public override RenderedColumnType WriteColumnType(CanonicalType type, string? sourceNote = null) =>
        DialectTypes.Write(Shared, type, sourceNote);
}
