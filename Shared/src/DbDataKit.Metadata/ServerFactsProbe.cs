using DbDataKit.Dialect;
using DbDataKit.Odbc.Metadata;

namespace DbDataKit.Metadata;

/// <summary>Produces <see cref="ServerFacts"/> from the places a server can be asked: ODBC SQLGetInfo, or a dialect plus a version string.</summary>
public static class ServerFactsProbe
{
    // SQLGetInfo information types beyond those OdbcCatalog.GetInfoAsync reads.
    private const ushort InfoMaxIdentifierLength = 10005, InfoBatchSupport = 121;

    /// <summary>
    /// Facts from an ODBC connection: <see cref="OdbcCatalog.GetInfoAsync"/> plus the maximum identifier length and batch support. Row-limit support is
    /// not something SQLGetInfo reports reliably; it is inferred from the product name (see <see cref="DialectProfiles.RowLimitFor"/>) and left null when unknown.
    /// </summary>
    public static async Task<ServerFacts> FromOdbcAsync(OdbcCatalog catalog, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return FromOdbc(await catalog.GetInfoAsync(cancellationToken).ConfigureAwait(false),
            (int?)await catalog.GetInfoNumberAsync(InfoMaxIdentifierLength, cancellationToken).ConfigureAwait(false),
            await catalog.GetInfoNumberAsync(InfoBatchSupport, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>The same from values already read. A max length of 0 means "no limit or unknown" (ODBC) and becomes null.</summary>
    public static ServerFacts FromOdbc(OdbcDriverInfo info, int? maxIdentifierLength = null, uint? batchSupport = null)
    {
        ArgumentNullException.ThrowIfNull(info);
        var (top, limit, offsetFetch) = DialectProfiles.RowLimitFor(info.DbmsName, info.DbmsVersion);
        return new ServerFacts(
            info.DbmsName, info.DbmsVersion, info.DriverName,
            info.IdentifierQuote?.Prefix, info.IdentifierQuote?.Suffix,
            Fold(info.IdentifierCase), Fold(info.QuotedIdentifierCase),
            maxIdentifierLength is > 0 ? maxIdentifierLength : null,
            info.CatalogSeparator, info.CatalogTerm, info.SchemaTerm, ReadOnly: null,
            SupportsTop: top, SupportsLimit: limit, SupportsOffsetFetch: offsetFetch,
            SupportsMultipleStatements: batchSupport is { } b ? b != 0 : null);
    }

    /// <summary>Facts implied by a dialect (what a provider-based reader knows without asking the server) plus the version the connection reported.</summary>
    public static ServerFacts FromDialect(ISqlDialect dialect, string? productName = null, string? version = null)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        string? open = null, close = null;
        try
        {
            string q = dialect.Quote("x");
            if (q.Length > 1) { open = q[..q.IndexOf('x')]; close = q[(q.IndexOf('x') + 1)..]; }
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException) { /* the dialect cannot quote */ }

        return new ServerFacts(
            productName ?? dialect.Id.Value, version, null, open, close,
            dialect.Identifiers.Fold, dialect.Identifiers.Fold, dialect.Identifiers.MaxLength,
            SupportsTop: dialect.SupportedRowLimitStyles.Contains(RowLimitStyle.TopN),
            SupportsLimit: dialect.SupportedRowLimitStyles.Contains(RowLimitStyle.LimitOffset),
            SupportsOffsetFetch: dialect.SupportedRowLimitStyles.Contains(RowLimitStyle.OffsetFetch),
            SupportsMultipleStatements: (dialect.Caps & EngineCaps.MultiStatement) != 0);
    }

    private static IdentifierCase? Fold(OdbcIdentifierCase c) => c switch
    {
        OdbcIdentifierCase.Upper => IdentifierCase.Upper,
        OdbcIdentifierCase.Lower => IdentifierCase.Lower,
        // Sensitive and mixed both keep the case as written.
        OdbcIdentifierCase.Sensitive or OdbcIdentifierCase.Mixed => IdentifierCase.Preserve,
        _ => null,
    };
}
