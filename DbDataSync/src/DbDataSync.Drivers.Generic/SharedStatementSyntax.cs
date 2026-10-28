using DbDataKit.Dialect;
using DbDataKit.Transfer;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.Generic;

/// <summary>
/// Sync's <see cref="SqlDialect"/> as the callbacks the shared statement builders take (<see cref="StatementSyntax"/>; plan 13, A16). Sync keeps its own dialect
/// type because it serves engines the shared dialect catalog does not cover (JDBC, ODBC, descriptor drivers) and because of divergences the shared catalog does not
/// model, such as Oracle's bare parameter name: the shared builders only ever ask this dialect to quote an identifier, to say how a parameter is referred to in
/// text, and to render the head of an INSERT.
/// </summary>
public static class SharedStatementSyntax
{
    /// <summary>
    /// Quoting and the parameter reference are the dialect's; the INSERT head is the dialect's <see cref="SqlDialect.RenderInsertInto"/> (PostgreSQL's
    /// <c>OVERRIDING SYSTEM VALUE</c> goes there). Sync names its parameters, so a parameter used twice is one parameter.
    /// </summary>
    public static StatementSyntax For(SqlDialect dialect) => new()
    {
        QuoteIdentifier = dialect.QuoteIdentifier,
        ParameterReference = (_, name) => dialect.ParameterReference(name),
        ParameterStyle = ParameterStyle.NamedAt,
        InsertInto = dialect.RenderInsertInto,
    };
}
