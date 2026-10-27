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

    /// <summary>Turns the readers' <c>{0}</c> markers into this session's named parameters and runs the query through it.</summary>
    private static async Task<IReadOnlyList<CatalogRow>> RunAsync(ReadSession read, string sql, object?[] args, CancellationToken ct)
    {
        var parameters = args.Select((a, i) => new GateParameter($"p{i}", a is int ? DbType.Int32 : a is bool ? DbType.Boolean : DbType.String, a)).ToList();
        for (var i = 0; i < args.Length; i++) sql = sql.Replace("{" + i + "}", "@p" + i, StringComparison.Ordinal);
        var rows = await read.QueryAsync(sql, parameters, ct);
        return [.. rows.Select(r => new CatalogRow([.. r.Select(_ => "")], [.. r]))];
    }

    private sealed class GatedSqlServerReader(ReadSession read, CatalogReaderOptions options) : SqlServerCatalogReader(new UnusedConnection(), options: options)
    {
        protected override Task<IReadOnlyList<CatalogRow>> QueryAsync(string sql, object?[] args, CancellationToken cancellationToken, CommandBehavior behavior = CommandBehavior.Default) => RunAsync(read, sql, args, cancellationToken);
    }

    private sealed class GatedPostgresReader(ReadSession read, CatalogReaderOptions options) : PostgresCatalogReader(new UnusedConnection(), options: options)
    {
        protected override Task<IReadOnlyList<CatalogRow>> QueryAsync(string sql, object?[] args, CancellationToken cancellationToken, CommandBehavior behavior = CommandBehavior.Default) => RunAsync(read, sql, args, cancellationToken);
    }
}
