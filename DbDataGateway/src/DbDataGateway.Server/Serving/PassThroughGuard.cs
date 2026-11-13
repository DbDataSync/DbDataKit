using System.Runtime.CompilerServices;
using DbDataKit.Serving;
using DbDataKit.Serving.Semantic;
using DbDataGateway.Server.Auth;
using DbDataGateway.Server.Data;

namespace DbDataGateway.Server.Serving;

/// <summary>
/// The refusal of pass-through SQL to a table that has access rules, and the catalog listing that leaves such a table out (plan 22a GP12, QG3). It wraps the host's services at the outside, so a client's statement (a <c>Sql</c> request, a prepared statement, a
/// Flight SQL update, anything an app or a view runs as SQL against a data source) is checked before it reaches the data source, while the semantic module's own scans, which run inside <see cref="SemanticModels"/>, are not: the model applies the rules to them.
/// The refusal is a <see cref="ServingDeniedException"/> naming the table, the rules and the model that holds them, and says to ask the model. A statement is checked by the conservative lexical scan <see cref="DbDataKit.Access.TableNameScan"/> (fail closed,
/// its limits are written there); a data source no model puts rules on, and a request for a model (<c>semantic:...</c>), are untouched.
/// </summary>
public static class PassThroughGuardServices
{
    /// <summary>The same services with the guard in front of them.</summary>
    public static IServingServices WithPassThroughGuard(this IServingServices inner, RuledTables ruled, DataSourceCatalog sources, ILogger? log = null, GatewayEvents? events = null) =>
        new Guarded(inner, ruled, sources, log, events);

    private sealed class Guarded(IServingServices inner, RuledTables ruled, DataSourceCatalog sources, ILogger? log, GatewayEvents? events) : IServingServices
    {
        public IServingIdentity Identity => inner.Identity;

        public IServingSessions Sessions => inner.Sessions;

        public IServingQueries Queries { get; } = new GuardedQueries(inner.Queries, ruled, sources, log, events);

        public IServingCatalog Catalog { get; } = new GuardedCatalog(inner.Catalog, ruled, sources);

        public IServingEntities? Entities => inner.Entities;

        public IServingUpdates? Updates { get; } = inner.Updates is { } updates ? new GuardedUpdates(updates, ruled, sources, log, events) : null;
    }

    /// <summary>The name of the data source a request means (an empty name is the caller's default), or null when it cannot be resolved: the inner service then says why.</summary>
    internal static string? SourceName(DataSourceCatalog sources, ServingContext context, string requested)
    {
        if (SemanticModels.IsModelSource(requested)) return null;
        try { return sources.Resolve(GatewayServing.PrincipalOf(context.Caller), requested).Name; }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    internal static void Enforce(RuledTables ruled, DataSourceCatalog sources, ILogger? log, GatewayEvents? events, ServingContext context, ServingRequest.Sql statement)
    {
        if (SourceName(sources, context, statement.Source) is not { } source || ruled.Check(source, statement.Text) is not { } refusal) return;
        log?.LogWarning("Refused a statement by {User} on data source {Source}: it names a table with access rules ({Tables})", context.Caller.Name, source, string.Join(", ", refusal.Tables.Select(t => t.Table)));
        events?.Raise(DbDataKit.Monitoring.NotificationKinds.StatementRefused, $"{context.Caller.Name}@{source}",
            $"A statement by '{context.Caller.Name}' on data source '{source}' was refused: it names a table with access rules ({string.Join(", ", refusal.Tables.Select(t => t.Table).Distinct())}); the model serves it.");
        throw new ServingDeniedException(refusal.Message);
    }

    private sealed class GuardedQueries(IServingQueries inner, RuledTables ruled, DataSourceCatalog sources, ILogger? log, GatewayEvents? events) : IServingQueries
    {
        public Task<IServingAnswer> RunAsync(ServingContext context, ServingRequest request, CancellationToken cancellationToken = default)
        {
            if (request is ServingRequest.Sql sql) Enforce(ruled, sources, log, events, context, sql);
            return inner.RunAsync(context, request, cancellationToken);
        }

        public Task<ServingStatementInfo> PrepareAsync(ServingContext context, ServingRequest.Sql statement, CancellationToken cancellationToken = default)
        {
            Enforce(ruled, sources, log, events, context, statement);
            return inner.PrepareAsync(context, statement, cancellationToken);
        }
    }

    private sealed class GuardedUpdates(IServingUpdates inner, RuledTables ruled, DataSourceCatalog sources, ILogger? log, GatewayEvents? events) : IServingUpdates
    {
        public Task<ServingWriteResult> ExecuteAsync(ServingContext context, ServingRequest.Sql statement, CancellationToken cancellationToken = default)
        {
            Enforce(ruled, sources, log, events, context, statement);
            return inner.ExecuteAsync(context, statement, cancellationToken);
        }
    }

    /// <summary>The listing of a raw data source leaves out a ruled table (and with it its columns and keys); the model lists its own datasets, which is the only way to see the table.</summary>
    private sealed class GuardedCatalog(IServingCatalog inner, RuledTables ruled, DataSourceCatalog sources) : IServingCatalog
    {
        public Task<ServingSourceFacts> FactsAsync(ServingContext context, string source, CancellationToken cancellationToken = default) => inner.FactsAsync(context, source, cancellationToken);

        public async IAsyncEnumerable<ServingObject> DescribeAsync(ServingContext context, IReadOnlyList<string> under, bool withColumns = false, bool withPrimaryKeys = false, string source = "",
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var name = SourceName(sources, context, source);
            var check = name is not null && ruled.Any(name);
            await foreach (var item in inner.DescribeAsync(context, under, withColumns, withPrimaryKeys, source, cancellationToken).ConfigureAwait(false))
            {
                if (check && item.Kind is ServingObjectKind.Table or ServingObjectKind.View && ruled.IsRuled(name!, item.Path)) continue;
                yield return item;
            }
        }
    }
}
