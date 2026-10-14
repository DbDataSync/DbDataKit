using System.Data.Common;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.ArrowFlightSql;

/// <summary>
/// The SQL a Flight SQL server is sent, in the dialect of whatever engine sits behind the protocol.
/// <para>
/// A Flight SQL endpoint is usually a proxy: it speaks Arrow on the wire and passes the SQL through to
/// SQL Server, Postgres, MySQL, Oracle, DuckDB or something that merely resembles ANSI. So every
/// statement-shaping decision — quoting, row limits, parameter markers, casts, literals — is delegated
/// to the dialect of the engine named when the connection's driver variant was chosen
/// (<c>ArrowFlightSqlPostgres</c>, ...), and only the things that are about *Flight SQL* are decided here:
/// <list type="bullet">
/// <item><see cref="UseDatabaseAsync"/> sends nothing. Flight SQL has no "switch database" statement, and
/// the inner dialect's own (<c>USE</c> on SQL Server, a validated <c>ChangeDatabase</c> elsewhere) would
/// send SQL the proxy may not accept. It only records the name on the connection, where metadata calls
/// read it as the catalog to look in.</item>
/// <item><see cref="ToCanonicalType"/> reads the ANSI type names a Flight SQL column reports (an Arrow
/// schema carries no engine-specific names), then falls back to the engine's own for any name that
/// is not one of those.</item>
/// <item>The identifier quote characters, which the base class reads through two protected members the
/// inner dialect's instance cannot be asked for.</item>
/// </list>
/// </para>
/// <para>
/// **Every overridable member of <see cref="SqlDialect"/> is forwarded**, and a test fails if one is
/// added without being forwarded here — a member left out would silently use the base class's ANSI
/// default under an engine that does it differently.
/// </para>
/// </summary>
public sealed class FlightSqlDialect(SqlDialect inner, char quoteOpen = '"', char quoteClose = '"') : SqlDialect
{
    public SqlDialect Inner => inner;

    protected override char IdentifierQuoteOpen => quoteOpen;
    protected override char IdentifierQuoteClose => quoteClose;

    public override string QuoteIdentifier(string identifier) => inner.QuoteIdentifier(identifier);
    public override string ParameterReference(string name) => inner.ParameterReference(name);
    public override string ParameterName(string name) => inner.ParameterName(name);
    public override string QualifyTable(string schema, string table) => inner.QualifyTable(schema, table);

    public override Task UseDatabaseAsync(DbConnection connection, string database, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(database) && connection is FlightSqlConnection)
            connection.ChangeDatabase(database);

        return Task.CompletedTask;
    }

    public override int MaxParametersPerStatement => inner.MaxParametersPerStatement;
    public override string OperationMarkerColumnType => inner.OperationMarkerColumnType;
    public override string ChangeOrderingColumnType => inner.ChangeOrderingColumnType;
    public override string ChangedAtColumnType => inner.ChangedAtColumnType;
    public override RowLimitStyle RowLimitStyle => inner.RowLimitStyle;
    public override bool SupportsTieSafeRowLimit => inner.SupportsTieSafeRowLimit;

    public override (string Prefix, string Suffix) RenderTieSafeRowLimit(string parameterName) =>
        inner.RenderTieSafeRowLimit(parameterName);

    public override (string Prefix, string Suffix) RenderRowLimit(int n) => inner.RenderRowLimit(n);
    public override string RenderStagingOrdinalColumn(string column) => inner.RenderStagingOrdinalColumn(column);

    public override string RenderMultiRowInsert(string qualifiedTable, string columnList, IReadOnlyList<string> rowValueTuples) =>
        inner.RenderMultiRowInsert(qualifiedTable, columnList, rowValueTuples);

    public override string RenderAddColumn(string qualifiedTable, string column, string type) =>
        inner.RenderAddColumn(qualifiedTable, column, type);

    public override string? RenderAlterColumnType(string qualifiedTable, string column, string type) =>
        inner.RenderAlterColumnType(qualifiedTable, column, type);

    public override string RenderRenameColumn(string qualifiedTable, string from, string to) =>
        inner.RenderRenameColumn(qualifiedTable, from, to);

    public override string CastToText(string expression) => inner.CastToText(expression);
    public override string TrueLiteral => inner.TrueLiteral;
    public override string FalseLiteral => inner.FalseLiteral;
    public override string Concat(IEnumerable<string> expressions) => inner.Concat(expressions);
    public override string RenderLiteral(object? value) => inner.RenderLiteral(value);

    public override string? RenderDeclarations(IReadOnlyList<PreviewParameter> parameters) =>
        inner.RenderDeclarations(parameters);

    public override string RenderSampleSelect(string qualifiedTable, int rows) => inner.RenderSampleSelect(qualifiedTable, rows);
    public override string RenderDropTableIfExists(string qualifiedTable) => inner.RenderDropTableIfExists(qualifiedTable);
    public override BucketableKind ClassifyForBucketing(string baseTypeName) => inner.ClassifyForBucketing(baseTypeName);

    public override string RenderInsertInto(string qualifiedTable, string columnList, bool overrideGenerated) =>
        inner.RenderInsertInto(qualifiedTable, columnList, overrideGenerated);

    public override Task<T> WriteWithGeneratedColumnOverrideAsync<T>(
        DbConnection connection, DbTransaction? transaction, string qualifiedTable, bool overrideRequired,
        Func<Task<T>> write, CancellationToken cancellationToken) =>
        inner.WriteWithGeneratedColumnOverrideAsync(
            connection, transaction, qualifiedTable, overrideRequired, write, cancellationToken);

    public override CanonicalType ToCanonicalType(string nativeType)
    {
        var ansi = AnsiDialect.Instance.ToCanonicalType(nativeType);
        return ansi.Kind != CanonicalTypeKind.Unmappable ? ansi : inner.ToCanonicalType(nativeType);
    }

    public override RenderedColumnType RenderColumnType(CanonicalType type) => inner.RenderColumnType(type);
}
