using System.Data.Common;
using DbDataKit.Dialect;

namespace DbDataKit.Metadata;

/// <summary>
/// MySQL and MariaDB through <c>information_schema</c>. A MySQL "schema" is a database and there is no catalog above it, so a table is
/// <c>TableRef(null, database, name)</c>. Columns carry the full <c>column_type</c> (<c>int unsigned</c>, <c>enum('a','b')</c>), <c>auto_increment</c> as
/// identity and <c>GENERATED</c> as computed; indexes come from <c>statistics</c>, row estimates from <c>table_rows</c> (InnoDB's own estimate).
/// Works over any <see cref="DbConnection"/> whose provider binds <c>?</c> positional parameters (MySqlConnector).
/// </summary>
/// <remarks>Listings are not limited to the connected database: <c>information_schema</c> is server-wide, and the system schemas are flagged rather than hidden.</remarks>
public class MySqlCatalogReader(DbConnection connection, ISqlDialect? dialect = null, CatalogReaderOptions? options = null)
    : InformationSchemaCatalogReader(connection, dialect ?? DialectCatalog.For(EngineId.MySql), options)
{
    public override CatalogCapabilities Capabilities => new(
        Catalogs: false, Schemas: true, ForeignKeys: true, Indexes: true, RowEstimates: true, Procedures: false,
        IdentityAndComputed: true, Describe: DescribeStrategy.WrappedZeroRowProbe);

    protected override string? ProductName => "MySQL";

    protected override bool UsesCatalogColumn => false;

    protected override string CatalogsSql => "SELECT schema_name FROM information_schema.schemata WHERE 1 = 0";

    protected override string SchemasSql => "SELECT NULL, schema_name FROM information_schema.schemata";

    protected override string TablesSql => "SELECT NULL, table_schema, table_name, table_type FROM information_schema.tables";

    protected override bool IsSystemSchema(string? schema) =>
        schema is not null && schema.ToLowerInvariant() is "information_schema" or "mysql" or "performance_schema" or "sys";

    protected override (string Sql, object?[] Args) ColumnsQuery(TableRef t)
    {
        var (where, args) = Where(t, "table_catalog", "table_schema", "table_name");
        // Layout: name, ordinal, type, nullable, default, char_length, precision, scale, collation, identity, computed. column_type already carries length and precision.
        return ("SELECT column_name, ordinal_position, column_type, is_nullable, column_default, NULL, NULL, NULL, collation_name, "
            + "extra LIKE '%auto_increment%', extra LIKE '%GENERATED%' "
            + $"FROM information_schema.columns WHERE {where} ORDER BY ordinal_position", args);
    }

    protected override (string Sql, object?[] Args)? ForeignKeysQuery(TableRef t)
    {
        // key_column_usage names the referenced table and column itself; the standard join through the unique constraint's name cannot,
        // because every table's primary key is called PRIMARY.
        var (where, args) = Where(t, "", "kcu.table_schema", "kcu.table_name");
        return ("SELECT kcu.constraint_name, kcu.ordinal_position, kcu.column_name, NULL, kcu.referenced_table_schema, kcu.referenced_table_name, kcu.referenced_column_name, rc.update_rule, rc.delete_rule "
            + "FROM information_schema.key_column_usage kcu JOIN information_schema.referential_constraints rc "
            + "ON rc.constraint_schema = kcu.constraint_schema AND rc.constraint_name = kcu.constraint_name AND rc.table_name = kcu.table_name "
            + $"WHERE kcu.referenced_table_name IS NOT NULL AND {where} ORDER BY kcu.constraint_name, kcu.ordinal_position", args);
    }

    protected override (string Sql, object?[] Args)? IndexesQuery(TableRef t)
    {
        var (where, args) = Where(t, "table_catalog", "table_schema", "table_name");
        return ("SELECT index_name, non_unique = 0, seq_in_index, column_name, collation = 'D' "
            + $"FROM information_schema.statistics WHERE index_name <> 'PRIMARY' AND {where} ORDER BY index_name, seq_in_index", args);
    }

    protected override (string Sql, object?[] Args)? RowEstimateQuery(TableRef t)
    {
        var (where, args) = Where(t, "table_catalog", "table_schema", "table_name");
        return ($"SELECT table_rows FROM information_schema.tables WHERE {where}", args);
    }

    public override async Task<ServerFacts> GetServerFactsAsync(CancellationToken cancellationToken = default)
    {
        var rows = await QueryAsync("SELECT VERSION()", [], cancellationToken).ConfigureAwait(false);
        string? version = rows.Count > 0 ? rows[0].Text(0) : null;
        return ServerFactsProbe.FromDialect(Dialect, version?.Contains("MariaDB", StringComparison.OrdinalIgnoreCase) == true ? "MariaDB" : ProductName, version);
    }
}
