using System.Data.Common;
using System.Globalization;

namespace DbDataKit.State;

/// <summary>
/// Asks the engine which of the rights <see cref="StateGrants"/> would give a login the connected one already has (plan 29b, C01), without changing anything: PostgreSQL's <c>has_schema_privilege</c>,
/// <c>has_table_privilege</c> and <c>has_database_privilege</c>, SQL Server's <c>HAS_PERMS_BY_NAME</c>. A table or schema that is not there yet has no right to ask about; what creating them takes is the <see cref="GrantLevel.Migrate"/>
/// level, which checks the right to create. SQLite has no rights: who may open the file is the file system's business.
/// </summary>
public static class StateRights
{
    private static readonly string[] Runtime = ["SELECT", "INSERT", "UPDATE", "DELETE"];

    /// <summary>
    /// What the connected login lacks for <paramref name="level"/>, each as a short phrase (<c>INSERT on dbdatakit_alpha.item</c>); empty when it has everything.
    /// </summary>
    /// <param name="registered">The registry's rows, when they could be read. SQL Server shows a login only what it has some right on, so a schema the login has no right on looks as if it were not there; the registry says it is.</param>
    public static IReadOnlyList<string> Missing(DbConnection connection, StateLayout layout, GrantLevel level, IReadOnlyList<RegisteredModule>? registered = null) =>
        layout.Engine switch
        {
            StateEngineIds.Postgres => MissingPostgres(connection, layout, level),
            StateEngineIds.MsSql => MissingMsSql(connection, layout, level, registered),
            _ => [],
        };

    private static List<string> MissingPostgres(DbConnection connection, StateLayout layout, GrantLevel level)
    {
        var missing = new List<string>();
        var newSchemas = false;
        foreach (var schema in layout.Schemas)
        {
            if (!Flag(connection, "SELECT EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = @s);", ("s", schema))) { newSchemas = true; continue; }
            if (!Flag(connection, "SELECT has_schema_privilege(@s, 'USAGE');", ("s", schema))) missing.Add($"USAGE on schema {schema}");
            if (level == GrantLevel.Migrate && !Flag(connection, "SELECT has_schema_privilege(@s, 'CREATE');", ("s", schema))) missing.Add($"CREATE on schema {schema}");
        }

        foreach (var table in layout.Namespaces.SelectMany(n => n.Tables.Values).Prepend(layout.Registry).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var privilege in Wanted(layout, table, level))
                if (PostgresTable(connection, table, privilege) == false) missing.Add($"{privilege} on {table}");
        }

        if (level == GrantLevel.Migrate && newSchemas && !Flag(connection, "SELECT has_database_privilege(current_database(), 'CREATE');"))
            missing.Add("CREATE on the database (to make a schema)");
        return missing;
    }

    private static List<string> MissingMsSql(DbConnection connection, StateLayout layout, GrantLevel level, IReadOnlyList<RegisteredModule>? registered)
    {
        var missing = new List<string>();
        var newSchemas = false;
        foreach (var schema in layout.Schemas)
        {
            var known = Value(connection, "SELECT SCHEMA_ID(@s);", ("s", schema)) is not null || (registered?.Any(r => string.Equals(r.SchemaName, schema, StringComparison.OrdinalIgnoreCase)) ?? false);
            if (!known) { newSchemas = true; continue; }
            // a schema of modules is granted as a whole: asking for the right on it also says so for a schema the login cannot see
            if (layout.Namespaces.Any(n => string.Equals(n.Schema, schema, StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var privilege in Runtime)
                    if (!Flag(connection, $"SELECT CASE WHEN HAS_PERMS_BY_NAME(@s, 'SCHEMA', '{privilege}') = 1 THEN 1 ELSE 0 END;", ("s", schema))) missing.Add($"{privilege} on schema {schema}");
                if (level == GrantLevel.Migrate && !Flag(connection, "SELECT CASE WHEN HAS_PERMS_BY_NAME(@s, 'SCHEMA', 'ALTER') = 1 THEN 1 ELSE 0 END;", ("s", schema))) missing.Add($"ALTER on schema {schema}");
            }
        }

        // the registry, and the tables of a namespace with no schema of its own, are asked about one by one
        var loose = layout.Namespaces.Where(n => n.Schema is null).SelectMany(n => n.Tables.Values).Prepend(layout.Registry).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var table in loose)
        {
            foreach (var privilege in Wanted(layout, table, level))
                if (MsSqlObject(connection, table, privilege) == false) missing.Add($"{privilege} on {table}");
        }

        if (level == GrantLevel.Migrate)
        {
            if (!Flag(connection, "SELECT CASE WHEN HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CREATE TABLE') = 1 THEN 1 ELSE 0 END;")) missing.Add("CREATE TABLE in the database");
            if (newSchemas && !Flag(connection, "SELECT CASE WHEN HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CREATE SCHEMA') = 1 THEN 1 ELSE 0 END;")) missing.Add("CREATE SCHEMA in the database");
        }

        return missing;
    }

    private static string[] Wanted(StateLayout layout, string table, GrantLevel level) =>
        string.Equals(table, layout.Registry, StringComparison.OrdinalIgnoreCase) ? (level == GrantLevel.Migrate ? ["SELECT", "INSERT", "UPDATE"] : ["SELECT"]) : Runtime;

    /// <summary>The right to create what a first start makes (the registry, so the schemas and tables) on a database that has none of it; the question <see cref="Missing"/> cannot ask of tables that are not there.</summary>
    public static bool CanCreate(DbConnection connection, StateLayout layout)
    {
        if (layout.Engine == StateEngineIds.Sqlite) return true;
        if (layout.Engine == StateEngineIds.Postgres)
        {
            if (!layout.Schemas.All(s => Flag(connection, "SELECT EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = @s);", ("s", s)) ? Flag(connection, "SELECT has_schema_privilege(@s, 'CREATE');", ("s", s)) : Flag(connection, "SELECT has_database_privilege(current_database(), 'CREATE');")))
                return false;
            return true;
        }

        return Flag(connection, "SELECT CASE WHEN HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CREATE TABLE') = 1 THEN 1 ELSE 0 END;")
            && layout.Schemas.All(s => Value(connection, "SELECT SCHEMA_ID(@s);", ("s", s)) is not null
                ? Flag(connection, "SELECT CASE WHEN HAS_PERMS_BY_NAME(@s, 'SCHEMA', 'ALTER') = 1 THEN 1 ELSE 0 END;", ("s", s))
                : Flag(connection, "SELECT CASE WHEN HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CREATE SCHEMA') = 1 THEN 1 ELSE 0 END;"));
    }

    /// <summary>The schema and table of a rendered name (<c>dbdatakit_alpha.item</c>); the schema is null for a bare one.</summary>
    internal static (string? Schema, string Table) Split(string qualified)
    {
        var dot = qualified.LastIndexOf('.');
        return dot < 0 ? (null, qualified) : (qualified[..dot], qualified[(dot + 1)..]);
    }

    /// <summary>Null when the table is not there (nothing to ask).</summary>
    private static bool? PostgresTable(DbConnection connection, string qualified, string privilege)
    {
        var (schema, table) = Split(qualified);
        var has = Value(connection, $"SELECT has_table_privilege(c.oid, '{privilege}') FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE c.relkind IN ('r', 'p') AND c.relname = @t AND n.nspname = COALESCE(@s, current_schema());", ("t", table), ("s", schema));
        return has is null ? null : Convert.ToBoolean(has, CultureInfo.InvariantCulture);
    }

    private static bool? MsSqlObject(DbConnection connection, string qualified, string privilege)
    {
        // HAS_PERMS_BY_NAME answers 0, not null, for a table that is not there
        var result = Value(connection, $"SELECT CASE WHEN OBJECT_ID(@n, 'U') IS NULL THEN NULL ELSE HAS_PERMS_BY_NAME(@n, 'OBJECT', '{privilege}') END;", ("n", qualified));
        return result is null ? null : Convert.ToInt32(result, CultureInfo.InvariantCulture) == 1;
    }

    private static bool Flag(DbConnection connection, string sql, params (string Name, object? Value)[] parameters) =>
        Value(connection, sql, parameters) switch
        {
            null => false,
            bool b => b,
            var v => Convert.ToInt64(v, CultureInfo.InvariantCulture) != 0,
        };

    private static object? Value(DbConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) Add(cmd, name, value);
        var result = cmd.ExecuteScalar();
        return result is DBNull ? null : result;
    }

    private static void Add(DbCommand cmd, string name, object? value)
    {
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = "@" + name;
        parameter.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(parameter);
    }
}
