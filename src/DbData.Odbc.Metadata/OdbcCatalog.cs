using System.Globalization;

namespace DbData.Odbc.Metadata;

/// <summary>
/// Typed catalog access over <see cref="IOdbcCatalogFunctions"/>. Open one with <see cref="Connect"/>; it holds one
/// connection (made on first use) and runs one call at a time. Methods throw <see cref="OdbcCatalogException"/> when the
/// driver fails or does not implement a function; check <see cref="OdbcCatalogException.NotSupported"/> to tell those apart.
/// </summary>
/// <remarks>
/// SQLTables and SQLColumns take their schema/table/column arguments as search patterns (<c>_</c> and <c>%</c> are
/// wildcards), so names are escaped with the driver's search-pattern escape and the rows are matched exactly again here.
/// </remarks>
public sealed class OdbcCatalog(IOdbcCatalogFunctions functions) : IAsyncDisposable
{
    // SQLGetInfo information types.
    private const ushort DriverName = 6, SearchPatternEscape = 14, DbmsName = 17, DbmsVersion = 18, SchemaTerm = 39,
        CatalogSeparator = 41, CatalogTerm = 42, IdentifierCaseInfo = 28, IdentifierQuoteChar = 29, QuotedIdentifierCaseInfo = 93;

    private string? patternEscape;
    private bool patternEscapeKnown;

    /// <summary>Opens a catalog over the ODBC driver manager using a connection string or DSN name. The connection is made on first use.</summary>
    public static OdbcCatalog Connect(string connectionString) => new(new OdbcNativeFunctions(connectionString));

    public ValueTask DisposeAsync() => functions.DisposeAsync();

    // ---- Driver facts -----------------------------------------------------------------------------------------------

    public async Task<OdbcDriverInfo> GetInfoAsync(CancellationToken cancellationToken = default)
    {
        string? quote = await TryInfoStringAsync(IdentifierQuoteChar, cancellationToken).ConfigureAwait(false);
        string? separator = await TryInfoStringAsync(CatalogSeparator, cancellationToken).ConfigureAwait(false);
        return new OdbcDriverInfo(
            Clean(await TryInfoStringAsync(DbmsName, cancellationToken).ConfigureAwait(false)),
            Clean(await TryInfoStringAsync(DbmsVersion, cancellationToken).ConfigureAwait(false)),
            Clean(await TryInfoStringAsync(DriverName, cancellationToken).ConfigureAwait(false)),
            // A single space means the driver does not support quoting. Drivers use one character for both ends.
            string.IsNullOrWhiteSpace(quote) ? null : new OdbcIdentifierQuote(quote.Trim(), quote.Trim()),
            await TryCaseAsync(IdentifierCaseInfo, cancellationToken).ConfigureAwait(false),
            await TryCaseAsync(QuotedIdentifierCaseInfo, cancellationToken).ConfigureAwait(false),
            Clean(await TryInfoStringAsync(SearchPatternEscape, cancellationToken).ConfigureAwait(false)),
            Clean(separator),
            Clean(await TryInfoStringAsync(CatalogTerm, cancellationToken).ConfigureAwait(false)),
            Clean(await TryInfoStringAsync(SchemaTerm, cancellationToken).ConfigureAwait(false)));
    }

    private async Task<string?> TryInfoStringAsync(ushort infoType, CancellationToken cancellationToken)
    {
        try { return await functions.GetInfoStringAsync(infoType, cancellationToken).ConfigureAwait(false); }
        catch (OdbcCatalogException ex) when (ex.Function == "SQLGetInfo") { return null; }
    }

    private async Task<OdbcIdentifierCase> TryCaseAsync(ushort infoType, CancellationToken cancellationToken)
    {
        try
        {
            uint value = await functions.GetInfoNumberAsync(infoType, cancellationToken).ConfigureAwait(false);
            return value is >= 1 and <= 4 ? (OdbcIdentifierCase)value : OdbcIdentifierCase.Unknown;
        }
        catch (OdbcCatalogException ex) when (ex.Function == "SQLGetInfo") { return OdbcIdentifierCase.Unknown; }
    }

    // ---- Catalogs, schemas, tables ---------------------------------------------------------------------------------

    /// <summary>The catalog (database) names the driver lists. Empty when the driver has no catalogs.</summary>
    public async Task<IReadOnlyList<string>> CatalogsAsync(CancellationToken cancellationToken = default)
    {
        var rows = await functions.TablesAsync("%", "", "", null, cancellationToken).ConfigureAwait(false);
        return Distinct(rows, 0);
    }

    /// <summary>The schema names the driver lists, optionally within one catalog. Empty when the driver has no schemas.</summary>
    public async Task<IReadOnlyList<string>> SchemasAsync(string? catalog = null, CancellationToken cancellationToken = default)
    {
        // The specification's schema-list request: schema "%", table empty and catalog empty (not null) unless one is named.
        var rows = await functions.TablesAsync(catalog is null ? "" : await EscapeAsync(catalog, cancellationToken).ConfigureAwait(false), "%", "", null, cancellationToken).ConfigureAwait(false);
        return Distinct(rows, 1);
    }

    /// <summary>Tables and views (types "TABLE,VIEW" unless <paramref name="types"/> says otherwise), optionally narrowed to one schema and/or table.</summary>
    public async Task<IReadOnlyList<OdbcTable>> TablesAsync(
        string? catalog = null, string? schema = null, string? table = null, string types = "TABLE,VIEW", CancellationToken cancellationToken = default)
    {
        var rows = await ExactAsync(
            escape => functions.TablesAsync(escape(catalog), escape(schema), escape(table), types, cancellationToken),
            [(1, schema), (2, table)], cancellationToken).ConfigureAwait(false);

        return rows.Where(r => At(r, 2) is not null)
            .Select(r => new OdbcTable(At(r, 0), At(r, 1), r[2]!, At(r, 3) ?? "TABLE", At(r, 4)))
            .DistinctBy(t => (t.Catalog, t.Schema, t.Name))
            .ToList();
    }

    // ---- Columns ----------------------------------------------------------------------------------------------------

    /// <summary>Declared columns, in table order. <paramref name="table"/> null lists every table's columns.</summary>
    public async Task<IReadOnlyList<OdbcColumn>> ColumnsAsync(
        string? catalog = null, string? schema = null, string? table = null, CancellationToken cancellationToken = default)
    {
        var rows = await ExactAsync(
            escape => functions.ColumnsAsync(escape(catalog), escape(schema), escape(table), null, cancellationToken),
            [(1, schema), (2, table)], cancellationToken).ConfigureAwait(false);

        return rows.Where(r => At(r, 2) is not null && At(r, 3) is not null)
            .Select(Column)
            .OrderBy(c => c.Catalog, StringComparer.Ordinal).ThenBy(c => c.Schema, StringComparer.Ordinal)
            .ThenBy(c => c.Table, StringComparer.Ordinal).ThenBy(c => c.Ordinal)
            .ToList();
    }

    private static OdbcColumn Column(string?[] r)
    {
        // NULLABLE: 0 no nulls, 1 nullable, 2 unknown (treated as nullable); IS_NULLABLE is "NO", "YES" or "".
        bool nullable = Number(At(r, 10)) is not 0 && !string.Equals(At(r, 17), "NO", StringComparison.OrdinalIgnoreCase);
        return new OdbcColumn(At(r, 0), At(r, 1), r[2]!, r[3]!, Number(At(r, 4)), At(r, 5), Number(At(r, 6)), Number(At(r, 8)),
            nullable, Number(At(r, 16)) ?? 0, At(r, 12), At(r, 11));
    }

    // ---- Keys and indexes -------------------------------------------------------------------------------------------

    /// <summary>The primary key of one table, or null when it has none (or the driver reports none). Drivers that cannot answer throw with NotSupported.</summary>
    public async Task<OdbcPrimaryKey?> PrimaryKeyAsync(string? catalog, string? schema, string table, CancellationToken cancellationToken = default)
    {
        var rows = (await functions.PrimaryKeysAsync(catalog, schema, table, cancellationToken).ConfigureAwait(false))
            .Where(r => At(r, 3) is not null && Same(At(r, 2), table) && (schema is null || Same(At(r, 1), schema))).ToList();
        if (rows.Count == 0) return null;
        var first = rows[0];
        return new OdbcPrimaryKey(At(first, 0), At(first, 1), table, At(first, 5),
            rows.OrderBy(r => Number(At(r, 4)) ?? 0).Select(r => r[3]!).ToList());
    }

    /// <summary>
    /// The primary keys of many tables, one SQLPrimaryKeys call each (the function is per table). Views are skipped. When the
    /// driver does not implement the function the result is empty and <paramref name="supported"/> says so.
    /// </summary>
    public async Task<(IReadOnlyList<OdbcPrimaryKey> Keys, bool Supported)> PrimaryKeysAsync(IEnumerable<OdbcTable> tables, CancellationToken cancellationToken = default)
    {
        var keys = new List<OdbcPrimaryKey>();
        try
        {
            foreach (var table in tables.Where(t => !t.IsView))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await PrimaryKeyAsync(table.Catalog, table.Schema, table.Name, cancellationToken).ConfigureAwait(false) is { } key) keys.Add(key);
            }
        }
        catch (OdbcCatalogException ex) when (ex.NotSupported) { return ([], false); }
        return (keys, true);
    }

    /// <summary>The foreign keys a table has. A key is the rows sharing an FK_NAME; a driver that names none starts a new key where KEY_SEQ restarts.</summary>
    public async Task<IReadOnlyList<OdbcForeignKey>> ForeignKeysAsync(string? catalog, string? schema, string table, CancellationToken cancellationToken = default)
    {
        var rows = (await functions.ForeignKeysAsync(catalog, schema, table, cancellationToken).ConfigureAwait(false))
            .Where(r => Same(At(r, 6), table) && (schema is null || Same(At(r, 5), schema))).ToList();

        var groups = new List<List<string?[]>>();
        foreach (var r in rows.OrderBy(r => At(r, 11) ?? "", StringComparer.Ordinal).ThenBy(r => Number(At(r, 8)) ?? 0))
        {
            bool sameKey = groups.Count > 0 && At(r, 11) is { } name && name == At(groups[^1][0], 11)
                && (Number(At(r, 8)) ?? 0) > (Number(At(groups[^1][^1], 8)) ?? 0);
            if (sameKey) groups[^1].Add(r); else groups.Add([r]);
        }

        return groups.Select((g, i) => new OdbcForeignKey(
            At(g[0], 11) ?? $"fk_{table}_{At(g[0], 2)}_{i + 1}",
            At(g[0], 4), At(g[0], 5), table, g.Select(r => At(r, 7) ?? "").ToList(),
            At(g[0], 0), At(g[0], 1), At(g[0], 2) ?? "", g.Select(r => At(r, 3) ?? "").ToList(),
            Number(At(g[0], 9)), Number(At(g[0], 10)))).ToList();
    }

    /// <summary>The indexes of a table. The table's own statistics row (TYPE 0) and unnamed indexes are left out.</summary>
    public async Task<IReadOnlyList<OdbcIndex>> IndexesAsync(string? catalog, string? schema, string table, CancellationToken cancellationToken = default)
    {
        var rows = (await functions.StatisticsAsync(catalog, schema, table, cancellationToken).ConfigureAwait(false))
            .Where(r => Same(At(r, 2), table) && (schema is null || Same(At(r, 1), schema)) && Number(At(r, 6)) != 0 && !string.IsNullOrEmpty(At(r, 5))).ToList();

        return rows.GroupBy(r => r[5]!, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new OdbcIndex(At(g.First(), 0), At(g.First(), 1), table, g.Key, Number(At(g.First(), 3)) == 0,
                g.OrderBy(r => Number(At(r, 7)) ?? 0).Select(r => new OdbcIndexColumn(At(r, 8) ?? "<expression>", At(r, 9) == "D")).ToList()))
            .ToList();
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Runs a pattern-taking catalog call with escaped names and keeps only rows whose columns equal the requested names.
    /// If escaping finds nothing (some drivers ignore or mishandle the escape), the call is repeated with the names as written.
    /// Names match exactly first and, failing that, ignoring case (case-insensitive databases report the stored case).
    /// </summary>
    private async Task<List<string?[]>> ExactAsync(
        Func<Func<string?, string?>, Task<IReadOnlyList<string?[]>>> call, (int Column, string? Wanted)[] filters, CancellationToken cancellationToken)
    {
        string? escapeChar = await PatternEscapeAsync(cancellationToken).ConfigureAwait(false);
        var rows = Filter(await call(name => Escape(name, escapeChar)).ConfigureAwait(false), filters);
        if (rows.Count == 0 && escapeChar is not null && filters.Any(f => f.Wanted is not null && NeedsEscape(f.Wanted)))
            rows = Filter(await call(name => name).ConfigureAwait(false), filters);
        return rows;
    }

    private static List<string?[]> Filter(IReadOnlyList<string?[]> rows, (int Column, string? Wanted)[] filters)
    {
        foreach (var (column, wanted) in filters)
        {
            if (wanted is null) continue;
            var exact = rows.Where(r => string.Equals(At(r, column), wanted, StringComparison.Ordinal)).ToList();
            rows = exact.Count > 0 ? exact : rows.Where(r => string.Equals(At(r, column), wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        return rows.ToList();
    }

    private async Task<string?> PatternEscapeAsync(CancellationToken cancellationToken)
    {
        if (!patternEscapeKnown)
        {
            patternEscape = Clean(await TryInfoStringAsync(SearchPatternEscape, cancellationToken).ConfigureAwait(false));
            patternEscapeKnown = true;
        }
        return patternEscape;
    }

    private async Task<string> EscapeAsync(string name, CancellationToken cancellationToken) =>
        Escape(name, await PatternEscapeAsync(cancellationToken).ConfigureAwait(false)) ?? name;

    private static bool NeedsEscape(string name) => name.Contains('_', StringComparison.Ordinal) || name.Contains('%', StringComparison.Ordinal);

    private static string? Escape(string? name, string? escape)
    {
        if (name is null || escape is null) return name;
        return name.Replace(escape, escape + escape, StringComparison.Ordinal)
            .Replace("_", escape + "_", StringComparison.Ordinal).Replace("%", escape + "%", StringComparison.Ordinal);
    }

    private static string? At(string?[] row, int index) => index < row.Length && !string.IsNullOrEmpty(row[index]) ? row[index] : null;

    private static bool Same(string? a, string? b) => string.Equals(a ?? "", b ?? "", StringComparison.Ordinal);

    private static int? Number(string? text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : null;

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static List<string> Distinct(IReadOnlyList<string?[]> rows, int column) =>
        rows.Select(r => At(r, column)).Where(s => s is not null).Select(s => s!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
}
