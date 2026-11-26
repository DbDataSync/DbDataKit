using Microsoft.Data.Sqlite;

namespace DbDataSync.TaskRunner.Tests.Runner;

/// <summary>
/// A Sync repository whose one connection, <c>lite</c>, runs on a YAML driver of the repository (<c>drivers/sqlite.generic/driver.yaml</c>, its catalog read by its own queries) over a SQLite file holding <c>items</c> (two rows) and
/// <c>notes</c>. The provider is the process's own Microsoft.Data.Sqlite: the library names its factory, and nothing is restored. SQLite needs no server.
/// </summary>
internal static class SqliteYamlSet
{
    /// <summary>Writes the repository's files under <paramref name="root"/> and the database beside them; returns the database's path.</summary>
    public static string Write(string root)
    {
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "lite.db");
        using (var connection = new SqliteConnection($"Data Source={database}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE items (id INTEGER NOT NULL PRIMARY KEY, name TEXT NOT NULL, price NUMERIC);
                CREATE TABLE notes (body TEXT);
                INSERT INTO items VALUES (1, 'apple', 2.5), (2, 'pear', 3.0);
                """;
            command.ExecuteNonQuery();
        }

        File(root, "libraries/microsoft-data-sqlite/library.csx", """
            // factory: Microsoft.Data.Sqlite.SqliteFactory, Microsoft.Data.Sqlite
            #r "nuget: Microsoft.Data.Sqlite, 10.0.11"
            """);
        File(root, "drivers/sqlite.generic/driver.yaml", """
            id: sqlite.generic
            displayName: SQLite (generic)
            library: microsoft-data-sqlite
            dialect:
              quoteIdentifier: doubleQuote
              parameterPrefix: "@"
              rowLimit: limitOffset
              catalog: query
              connectionStringKeys:
                database: Data Source
                connectTimeout: Default Timeout
            metadataQueries:
              tableQuery: "SELECT 'main' AS table_schema, name AS table_name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'"
              columnQuery: "SELECT name AS column_name, type AS data_type, CASE WHEN \"notnull\" = 1 OR pk > 0 THEN 'NO' ELSE 'YES' END AS is_nullable, pk > 0 AS is_primary_key FROM pragma_table_info({{table}}) ORDER BY cid"
            capabilities:
              readers: [BatchReload]
              staging: []
              writers: []
            """);
        File(root, "config/connections/lite.yaml", $"""
            schema: connection/1
            name: lite
            engine: sqlite.generic
            connection_string: 'Mode=ReadWrite'
            auth:
              mode: none
            extensions:
              sync:
                database: '{database}'
            """);
        return database;
    }

    private static void File(string root, string relative, string text)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, text);
    }
}
