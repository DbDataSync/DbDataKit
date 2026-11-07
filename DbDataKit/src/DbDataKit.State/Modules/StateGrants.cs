using System.Text.RegularExpressions;

namespace DbDataKit.State;

/// <summary>What a service's login is allowed to do to the namespaces it uses.</summary>
public enum GrantLevel
{
    /// <summary>
    /// Everything a running service does: read and write the rows of its modules' tables and read the registry. Enough to open a database whose modules are already applied; a service that has
    /// to apply a migration needs <see cref="Migrate"/>.
    /// </summary>
    Runtime,

    /// <summary>Also create and alter what the modules' migrations create, and write the registry: what the login that upgrades the database needs. Usually another login than the one the service runs as.</summary>
    Migrate,
}

/// <summary>
/// The <c>GRANT</c> statements that give one login exactly the namespaces a service uses (plan 29 section 3.2, "rights per namespace"; plan 29a Q23): with a schema each, a login granted only its schemas
/// can open its modules and cannot read another's. Written for SQL Server (to a database user) and PostgreSQL (to a role); SQLite has no rights to grant. The statements are text to print or run, in the order
/// to run them; none of them takes a table that is not in the layout.
/// </summary>
public static partial class StateGrants
{
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex Identifier { get; }

    /// <param name="layout">The modules the service uses, in the namespaces it will use them in.</param>
    /// <param name="principal">The database role (PostgreSQL) or database user (SQL Server) the service's login maps to. A plain identifier.</param>
    public static IReadOnlyList<string> Statements(StateLayout layout, string principal, GrantLevel level = GrantLevel.Runtime)
    {
        if (!Identifier.IsMatch(principal))
            throw new ArgumentException($"'{principal}' is not a plain database role or user name (letters, digits and underscores, not starting with a digit).", nameof(principal));
        return layout.Engine switch
        {
            StateEngineIds.Postgres => Postgres(layout, principal, level),
            StateEngineIds.MsSql => MsSql(layout, principal, level),
            _ => throw new InvalidOperationException($"The engine {layout.Engine} has no rights to grant: a SQLite database is a file, and who may open it is the file system's business."),
        };
    }

    private static List<string> Postgres(StateLayout layout, string principal, GrantLevel level)
    {
        var statements = new List<string>();
        foreach (var schema in layout.Schemas.Where(s => !string.Equals(s, layout.RegistrySchema, StringComparison.OrdinalIgnoreCase)))
        {
            statements.Add($"GRANT USAGE ON SCHEMA {schema} TO {principal};");
            statements.Add($"GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA {schema} TO {principal};");
            statements.Add($"GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA {schema} TO {principal};");
            // tables a later migration creates, made by the login that runs it
            statements.Add($"ALTER DEFAULT PRIVILEGES IN SCHEMA {schema} GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO {principal};");
            statements.Add($"ALTER DEFAULT PRIVILEGES IN SCHEMA {schema} GRANT USAGE, SELECT ON SEQUENCES TO {principal};");
            if (level == GrantLevel.Migrate) statements.Add($"GRANT CREATE ON SCHEMA {schema} TO {principal};");
        }

        // a namespace without a schema (an empty one, so the engine's own default schema) is granted table by table
        var tables = layout.Namespaces.Where(n => n.Schema is null).SelectMany(n => n.Tables.Values).ToList();
        if (tables.Count > 0) statements.Add($"GRANT SELECT, INSERT, UPDATE, DELETE ON {string.Join(", ", tables)} TO {principal};");

        // a namespace that is new to the database needs its schema made, which takes the right to create schemas in it
        if (level == GrantLevel.Migrate)
            statements.Add($"DO $$ BEGIN EXECUTE format('GRANT CREATE ON DATABASE %I TO {principal}', current_database()); END $$;");

        if (layout.RegistrySchema is { } registrySchema)
        {
            statements.Add($"GRANT USAGE ON SCHEMA {registrySchema} TO {principal};");
            statements.Add(level == GrantLevel.Migrate
                ? $"GRANT SELECT, INSERT, UPDATE ON {layout.Registry} TO {principal};"
                : $"GRANT SELECT ON {layout.Registry} TO {principal};");
            if (level == GrantLevel.Migrate) statements.Add($"GRANT CREATE ON SCHEMA {registrySchema} TO {principal};");
        }
        else
            statements.Add(level == GrantLevel.Migrate ? $"GRANT SELECT, INSERT, UPDATE ON {layout.Registry} TO {principal};" : $"GRANT SELECT ON {layout.Registry} TO {principal};");

        return statements;
    }

    private static List<string> MsSql(StateLayout layout, string principal, GrantLevel level)
    {
        var statements = new List<string>();
        foreach (var schema in layout.Schemas.Where(s => !string.Equals(s, layout.RegistrySchema, StringComparison.OrdinalIgnoreCase)))
        {
            // a schema-level grant covers the tables a later migration creates in it
            statements.Add($"GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::{schema} TO [{principal}];");
            if (level == GrantLevel.Migrate) statements.Add($"GRANT ALTER, REFERENCES ON SCHEMA::{schema} TO [{principal}];");
        }

        var tables = layout.Namespaces.Where(n => n.Schema is null).SelectMany(n => n.Tables.Values).ToList();
        foreach (var table in tables) statements.Add($"GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::{table} TO [{principal}];");

        if (layout.RegistrySchema is { } registrySchema)
            statements.Add(level == GrantLevel.Migrate ? $"GRANT SELECT, INSERT, UPDATE, ALTER ON SCHEMA::{registrySchema} TO [{principal}];" : $"GRANT SELECT ON SCHEMA::{registrySchema} TO [{principal}];");
        else
            statements.Add(level == GrantLevel.Migrate ? $"GRANT SELECT, INSERT, UPDATE ON OBJECT::{layout.Registry} TO [{principal}];" : $"GRANT SELECT ON OBJECT::{layout.Registry} TO [{principal}];");

        // creating schemas and tables takes database-level rights: only the login that upgrades the database needs them
        if (level == GrantLevel.Migrate) statements.Add($"GRANT CREATE TABLE, CREATE SCHEMA TO [{principal}];");
        return statements;
    }
}
