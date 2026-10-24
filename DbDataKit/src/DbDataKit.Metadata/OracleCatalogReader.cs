using System.Data.Common;
using DbDataKit.Dialect;

namespace DbDataKit.Metadata;

/// <summary>
/// Oracle through the <c>ALL_*</c> dictionary views. There is no catalog above the schema (the owner), so a table is <c>TableRef(null, owner, name)</c>, and names are
/// matched as given: Oracle stores unquoted names in upper case, so callers pass <c>"HR"</c>, not <c>"hr"</c>. Native types are composed in the query
/// (<c>VARCHAR2(50)</c>, <c>NUMBER(10,2)</c>, <c>RAW(16)</c>; <c>TIMESTAMP</c> already carries its precision). Needs 12c: identity columns come from
/// <c>ALL_TAB_IDENTITY_COLS</c>, virtual (computed) columns from <c>ALL_TAB_COLS.VIRTUAL_COLUMN</c> (hidden columns excluded), and Oracle-supplied schemas from <c>ALL_USERS.ORACLE_MAINTAINED</c>.
/// Works over any <see cref="DbConnection"/> whose provider binds <c>:p0</c> parameters in order (ODP.NET).
/// </summary>
/// <remarks>
/// Column defaults and collations are not read: <c>DATA_DEFAULT</c> is a <c>LONG</c>, which ODP.NET returns empty unless the command opts in, and
/// <c>COLLATION</c> exists only from 12.2. <c>ColumnInfo.DefaultExpr</c> and <c>Collation</c> are null. Oracle has no <c>ON UPDATE</c> rule, so foreign keys report
/// <c>NO ACTION</c> for it. A descending key part is stored by Oracle as a hidden <c>SYS_NC…$</c> expression column, so it is reported with no name (<c>&lt;expression&gt;</c>) and the descending flag. There is no read-only session to open, so the describe probe is the wrapped zero-row query.
/// </remarks>
public class OracleCatalogReader(DbConnection connection, ISqlDialect? dialect = null, CatalogReaderOptions? options = null)
    : InformationSchemaCatalogReader(connection, dialect ?? DialectCatalog.For(EngineId.Oracle), options)
{
    public override CatalogCapabilities Capabilities => new(
        Catalogs: false, Schemas: true, ForeignKeys: true, Indexes: true, RowEstimates: true, Procedures: false,
        IdentityAndComputed: true, Describe: DescribeStrategy.WrappedZeroRowProbe);

    protected override string? ProductName => "Oracle";

    protected override bool UsesCatalogColumn => false;

    protected override string CatalogsSql => "SELECT username FROM all_users WHERE 1 = 0";

    protected override string SchemasSql => "SELECT NULL, username FROM all_users";

    protected override string TablesSql =>
        "SELECT NULL, t.owner, t.table_name, CASE WHEN u.oracle_maintained = 'Y' THEN 'SYSTEM TABLE' ELSE 'TABLE' END FROM all_tables t JOIN all_users u ON u.username = t.owner "
        + "UNION ALL SELECT NULL, v.owner, v.view_name, CASE WHEN u.oracle_maintained = 'Y' THEN 'SYSTEM VIEW' ELSE 'VIEW' END FROM all_views v JOIN all_users u ON u.username = v.owner";

    protected override bool IsSystemSchema(string? schema) => false;

    protected override (string Sql, object?[] Args) ColumnsQuery(TableRef t)
    {
        var (where, args) = Where(t, "", "c.owner", "c.table_name");
        // Layout: name, ordinal, type, nullable, default, char_length, precision, scale, collation, identity, computed.
        return ("SELECT c.column_name, c.column_id, "
            + "CASE WHEN c.data_type IN ('VARCHAR2', 'NVARCHAR2', 'CHAR', 'NCHAR') THEN c.data_type || '(' || c.char_length || ')' "
            + "WHEN c.data_type = 'NUMBER' AND c.data_precision IS NOT NULL THEN 'NUMBER(' || c.data_precision || ',' || NVL(c.data_scale, 0) || ')' "
            + "WHEN c.data_type = 'RAW' THEN 'RAW(' || c.data_length || ')' ELSE c.data_type END, "
            + "CASE WHEN c.nullable = 'Y' THEN 1 ELSE 0 END, NULL, NULL, NULL, NULL, NULL, "
            + "CASE WHEN EXISTS (SELECT 1 FROM all_tab_identity_cols i WHERE i.owner = c.owner AND i.table_name = c.table_name AND i.column_name = c.column_name) THEN 1 ELSE 0 END, "
            + "CASE WHEN c.virtual_column = 'YES' THEN 1 ELSE 0 END "
            + $"FROM all_tab_cols c WHERE c.hidden_column = 'NO' AND {where} ORDER BY c.column_id", args);
    }

    protected override (string Sql, object?[] Args)? PrimaryKeyQuery(TableRef t)
    {
        var (where, args) = Where(t, "", "c.owner", "c.table_name");
        return ("SELECT c.constraint_name, cc.column_name, cc.position FROM all_constraints c "
            + "JOIN all_cons_columns cc ON cc.owner = c.owner AND cc.constraint_name = c.constraint_name "
            + $"WHERE c.constraint_type = 'P' AND {where} ORDER BY cc.position", args);
    }

    protected override (string Sql, object?[] Args)? ForeignKeysQuery(TableRef t)
    {
        var (where, args) = Where(t, "", "c.owner", "c.table_name");
        return ("SELECT c.constraint_name, cc.position, cc.column_name, NULL, r.owner, r.table_name, rc.column_name, 'NO ACTION', c.delete_rule "
            + "FROM all_constraints c JOIN all_cons_columns cc ON cc.owner = c.owner AND cc.constraint_name = c.constraint_name "
            + "JOIN all_constraints r ON r.owner = c.r_owner AND r.constraint_name = c.r_constraint_name "
            + "JOIN all_cons_columns rc ON rc.owner = r.owner AND rc.constraint_name = r.constraint_name AND rc.position = cc.position "
            + $"WHERE c.constraint_type = 'R' AND {where} ORDER BY c.constraint_name, cc.position", args);
    }

    protected override (string Sql, object?[] Args)? IndexesQuery(TableRef t)
    {
        var (where, args) = Where(t, "", "i.table_owner", "i.table_name");
        // The index behind the primary key is the key, not an index of its own.
        return ("SELECT i.index_name, CASE WHEN i.uniqueness = 'UNIQUE' THEN 1 ELSE 0 END, ic.column_position, CASE WHEN ic.column_name LIKE 'SYS_NC%$' THEN NULL ELSE ic.column_name END, CASE WHEN ic.descend = 'DESC' THEN 1 ELSE 0 END "
            + "FROM all_indexes i JOIN all_ind_columns ic ON ic.index_owner = i.owner AND ic.index_name = i.index_name "
            + "WHERE NOT EXISTS (SELECT 1 FROM all_constraints k WHERE k.owner = i.table_owner AND k.index_name = i.index_name AND k.constraint_type = 'P') "
            + $"AND {where} ORDER BY i.index_name, ic.column_position", args);
    }

    protected override (string Sql, object?[] Args)? RowEstimateQuery(TableRef t)
    {
        var (where, args) = Where(t, "", "owner", "table_name");
        // num_rows is null until statistics are gathered: unknown, not zero.
        return ($"SELECT num_rows FROM all_tables WHERE {where}", args);
    }
}
