using System.Data.Common;
using DbDataKit.Dialect;

namespace DbDataKit.Metadata;

/// <summary>
/// PostgreSQL through <c>pg_catalog</c> (INFORMATION_SCHEMA hides tables the login does not own and has no identity-by-default or index detail):
/// identity (including <c>serial</c> defaults), generated columns (PostgreSQL 12+), collations, keys with correct column order, indexes with
/// sort direction, and row estimates from <c>pg_class.reltuples</c>. Query shapes use the provider's schema-only describe (Parse/Describe without
/// Bind/Execute). Works over any <see cref="DbConnection"/> that binds <c>$1</c> positional parameters (Npgsql).
/// </summary>
/// <remarks>The catalog is always the connected database; PostgreSQL cannot read another database's catalog on one connection.</remarks>
public class PostgresCatalogReader(DbConnection connection, ISqlDialect? dialect = null, CatalogReaderOptions? options = null)
    : InformationSchemaCatalogReader(connection, dialect ?? DialectCatalog.For(EngineId.Postgres), options)
{
    private readonly TimeSpan timeout = (options ?? new CatalogReaderOptions()).CommandTimeout;

    public override CatalogCapabilities Capabilities => new(
        Catalogs: true, Schemas: true, ForeignKeys: true, Indexes: true, RowEstimates: true, Procedures: false,
        IdentityAndComputed: true, Describe: DescribeStrategy.PreparedStatement);

    protected override string? ProductName => "PostgreSQL";

    protected override bool UsesCatalogColumn => false;

    protected override string CatalogsSql => "SELECT datname FROM pg_database WHERE NOT datistemplate AND datallowconn ORDER BY datname";

    protected override string TablesSql =>
        "SELECT current_database()::text, n.nspname::text, c.relname::text, c.relkind::text FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE c.relkind IN ('r', 'p', 'v', 'm', 'f')";

    protected override TableKind MapKind(string? type) => (type ?? "").Trim() switch
    {
        "r" or "p" or "f" => TableKind.Table,
        "v" => TableKind.View,
        "m" => TableKind.MaterializedView,
        _ => TableKind.Other,
    };

    private static (string Where, object?[] Args) Of(TableRef t, string schemaColumn = "n.nspname", string nameColumn = "c.relname")
    {
        var conditions = new List<string>();
        var args = new List<object?>();
        if (t.Schema is not null) { conditions.Add($"{schemaColumn} = {{{args.Count}}}"); args.Add(t.Schema); }
        conditions.Add($"{nameColumn} = {{{args.Count}}}");
        args.Add(t.Name);
        return (string.Join(" AND ", conditions), [.. args]);
    }

    protected override (string Sql, object?[] Args) ColumnsQuery(TableRef t)
    {
        var (where, args) = Of(t);
        // Layout: name, ordinal, type, nullable, default, char_length, precision, scale, collation, identity (identity column or serial default), computed.
        return ("SELECT a.attname::text, a.attnum::int, format_type(a.atttypid, a.atttypmod), NOT a.attnotnull, pg_get_expr(d.adbin, d.adrelid), NULL, NULL, NULL, "
            + "CASE WHEN a.attcollation <> 0 AND coll.collname <> 'default' THEN coll.collname::text END, "
            + "(a.attidentity <> '' OR COALESCE(pg_get_expr(d.adbin, d.adrelid), '') LIKE 'nextval(%'), a.attgenerated <> '' "
            + "FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid JOIN pg_namespace n ON n.oid = c.relnamespace "
            + "LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum LEFT JOIN pg_collation coll ON coll.oid = a.attcollation "
            + $"WHERE a.attnum > 0 AND NOT a.attisdropped AND {where} ORDER BY a.attnum", args);
    }

    protected override (string Sql, object?[] Args)? PrimaryKeyQuery(TableRef t)
    {
        var (where, args) = Of(t);
        return ("SELECT con.conname::text, a.attname::text, k.ord::int FROM pg_constraint con JOIN pg_class c ON c.oid = con.conrelid JOIN pg_namespace n ON n.oid = c.relnamespace "
            + "CROSS JOIN LATERAL unnest(con.conkey) WITH ORDINALITY AS k(attnum, ord) JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = k.attnum "
            + $"WHERE con.contype = 'p' AND {where} ORDER BY k.ord", args);
    }

    protected override (string Sql, object?[] Args)? ForeignKeysQuery(TableRef t)
    {
        var (where, args) = Of(t);
        const string rule = "CASE {0} WHEN 'a' THEN 'NO ACTION' WHEN 'r' THEN 'RESTRICT' WHEN 'c' THEN 'CASCADE' WHEN 'n' THEN 'SET NULL' WHEN 'd' THEN 'SET DEFAULT' END";
        // {0} in the rule template is a C# format hole, not a parameter marker.
        string update = string.Format(System.Globalization.CultureInfo.InvariantCulture, rule, "con.confupdtype");
        string delete = string.Format(System.Globalization.CultureInfo.InvariantCulture, rule, "con.confdeltype");
        return ("SELECT con.conname::text, k.ord::int, a.attname::text, current_database()::text, rn.nspname::text, rc.relname::text, ra.attname::text, " + update + ", " + delete + " "
            + "FROM pg_constraint con JOIN pg_class c ON c.oid = con.conrelid JOIN pg_namespace n ON n.oid = c.relnamespace "
            + "JOIN pg_class rc ON rc.oid = con.confrelid JOIN pg_namespace rn ON rn.oid = rc.relnamespace "
            + "CROSS JOIN LATERAL unnest(con.conkey, con.confkey) WITH ORDINALITY AS k(attnum, fattnum, ord) "
            + "JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = k.attnum JOIN pg_attribute ra ON ra.attrelid = con.confrelid AND ra.attnum = k.fattnum "
            + $"WHERE con.contype = 'f' AND {where} ORDER BY con.conname, k.ord", args);
    }

    protected override (string Sql, object?[] Args)? IndexesQuery(TableRef t)
    {
        var (where, args) = Of(t);
        // Key columns only (INCLUDE columns are not part of the key); attnum 0 is an expression, reported with a null name.
        return ("SELECT ic.relname::text, i.indisunique, k.ord::int, a.attname::text, (i.indoption[k.ord - 1] & 1) <> 0 "
            + "FROM pg_index i JOIN pg_class ic ON ic.oid = i.indexrelid JOIN pg_class c ON c.oid = i.indrelid JOIN pg_namespace n ON n.oid = c.relnamespace "
            + "CROSS JOIN LATERAL unnest(i.indkey::int2[]) WITH ORDINALITY AS k(attnum, ord) LEFT JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum = k.attnum "
            + $"WHERE NOT i.indisprimary AND k.ord <= i.indnkeyatts AND {where} ORDER BY ic.relname, k.ord", args);
    }

    protected override (string Sql, object?[] Args)? RowEstimateQuery(TableRef t)
    {
        var (where, args) = Of(t);
        // reltuples is -1 for a table never analysed (PostgreSQL 14+): unknown, not zero.
        return ("SELECT CASE WHEN c.reltuples < 0 THEN NULL ELSE c.reltuples::bigint END FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE " + where, args);
    }

    public override async Task<ServerFacts> GetServerFactsAsync(CancellationToken cancellationToken = default)
    {
        var rows = await QueryAsync("SELECT current_setting('server_version'), current_setting('max_identifier_length')", [], cancellationToken).ConfigureAwait(false);
        var facts = ServerFactsProbe.FromDialect(Dialect, ProductName, rows.Count > 0 ? rows[0].Text(0) : null);
        return rows.Count > 0 && rows[0].Int(1) is { } max ? facts with { MaxIdentifierLength = max } : facts;
    }

    public override Task<QueryShape> DescribeQueryAsync(string sql, CancellationToken cancellationToken = default) =>
        QueryProbe.SchemaOnlyAsync(Connection, Dialect, sql, timeout, cancellationToken);
}
