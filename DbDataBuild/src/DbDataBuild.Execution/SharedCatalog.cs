using System.Data;
using DbDataBuild.State;
using DbDataKit.Metadata;
using DbDataKit.Schema.Reading;

namespace DbDataBuild.Execution;

/// <summary>
/// The live shape of the objects under a schema name on SQL Server or PostgreSQL, read by the shared catalog readers of <c>DbDataKit.Metadata</c> (plan 13, B04) and run through the read session: the readers write the queries, the session
/// guards, observes and runs every one of them, so nothing about the gate changes. What comes back is turned into the shapes the tool hashes (<see cref="ObjectShape"/>) by the shared <see cref="CatalogShapes"/> (plan 19, H04).
/// </summary>
internal static class SharedCatalog
{
    public static async Task<IReadOnlyDictionary<string, ObjectShape>> ReadObjectsAsync(ReadSession read, bool postgres, string schema, string? objectName, CancellationToken ct)
    {
        var options = new CatalogReaderOptions { IncludeConstraintIndexes = true };
        ICatalogReader reader = postgres ? new GatedPostgresReader(read, options) : new GatedSqlServerReader(read, options);
        return await CatalogShapes.ReadAsync(reader, postgres ? ShapeEngine.Postgres : ShapeEngine.SqlServer, schema, objectName, ct);
    }

    /// <summary>
    /// The primary keys and foreign keys of every table of a schema, from the shared readers' schema-wide read (<see cref="ICatalogReader.ReadSchemaKeysAsync"/>): one primary-key query and one foreign-key query per schema, whatever the number of
    /// tables, each run through the read session. SQL Server (and Fabric, which is read as SQL Server), PostgreSQL and a DuckDB file.
    /// </summary>
    public static Task<SchemaKeys> ReadKeysAsync(ReadSession read, string target, string schema, CancellationToken ct) =>
        ReaderFor(read, target).ReadSchemaKeysAsync(null, schema, ct);

    /// <summary>The names of the schemas the origin's catalog lists, system ones included, ordered by name (ordinal). A DuckDB file lists those of its own database only: the readers see every attached one.</summary>
    public static async Task<IReadOnlyList<string>> ReadSchemaNamesAsync(ReadSession read, string target, CancellationToken ct)
    {
        string? current = null;
        if (target == "duckdb") current = (await read.QueryAsync("SELECT current_database()", null, ct)).Select(r => r[0] as string).FirstOrDefault();
        var names = new List<string>();
        await foreach (var s in ReaderFor(read, target).ListSchemasAsync(ct))
            if (target != "duckdb" || string.Equals(s.Catalog, current, StringComparison.Ordinal)) names.Add(s.Name);
        return names;
    }

    private static ICatalogReader ReaderFor(ReadSession read, string target)
    {
        var options = new CatalogReaderOptions();
        return target == "duckdb" ? new GatedDuckDbReader(read, options) : target == "postgres" ? new GatedPostgresReader(read, options) : new GatedSqlServerReader(read, options);
    }

    /// <summary>
    /// Turns the readers' <c>{0}</c> markers into this session's parameters and runs the query through it: named (<c>@p0</c>) for the engines, positional (<c>?</c>, in the order the readers write them) for DuckDB, whose connection takes no names.
    /// </summary>
    private static async Task<IReadOnlyList<CatalogRow>> RunAsync(ReadSession read, string sql, object?[] args, CancellationToken ct, bool positional = false)
    {
        var parameters = args.Select((a, i) => new GateParameter($"p{i}", a is int ? DbType.Int32 : a is bool ? DbType.Boolean : DbType.String, a)).ToList();
        for (var i = 0; i < args.Length; i++) sql = sql.Replace("{" + i + "}", positional ? "?" : "@p" + i, StringComparison.Ordinal);
        var rows = await read.QueryAsync(sql, parameters, ct);
        return [.. rows.Select(r => new CatalogRow([.. r.Select(_ => "")], [.. r]))];
    }

    private sealed class GatedSqlServerReader(ReadSession read, CatalogReaderOptions options) : SqlServerCatalogReader(new UnusedConnection(), options: options)
    {
        protected override Task<IReadOnlyList<CatalogRow>> QueryAsync(string sql, object?[] args, CancellationToken cancellationToken, CommandBehavior behavior = CommandBehavior.Default) => RunAsync(read, sql, args, cancellationToken);
    }

    private sealed class GatedDuckDbReader(ReadSession read, CatalogReaderOptions options) : DuckDbCatalogReader(new UnusedConnection(), options: options)
    {
        protected override Task<IReadOnlyList<CatalogRow>> QueryAsync(string sql, object?[] args, CancellationToken cancellationToken, CommandBehavior behavior = CommandBehavior.Default) => RunAsync(read, sql, args, cancellationToken, positional: true);
    }

    private sealed class GatedPostgresReader(ReadSession read, CatalogReaderOptions options) : PostgresCatalogReader(new UnusedConnection(), options: options)
    {
        protected override Task<IReadOnlyList<CatalogRow>> QueryAsync(string sql, object?[] args, CancellationToken cancellationToken, CommandBehavior behavior = CommandBehavior.Default) => RunAsync(read, sql, args, cancellationToken);
    }
}
