using DbDataKit.Serving;
using Apache.Arrow;
using DbDataKit.Dialect;
using DbDataProxy.Data;
using DbDataProxy.Server.Auth;
using DbDataProxy.Server.Configuration;
using DbDataProxy.Server.Data;
using Microsoft.Extensions.Options;

namespace DbDataProxy.Server.Serving;

public sealed partial class ProxyServing
{
    /// <summary>Statements and updates against a data source, run as the caller.</summary>
    private sealed class QueryService(DataSourceCatalog sources, ILogger log, ProxyEvents? events) : IServingQueries, IServingUpdates
    {
        public async Task<IServingAnswer> RunAsync(ServingContext context, ServingRequest request, CancellationToken cancellationToken = default)
        {
            if (request is not ServingRequest.Sql sql) throw new ServingInvalidException($"This server does not run {request.GetType().Name.ToLowerInvariant()} requests.");
            var principal = PrincipalOf(context.Caller);
            var source = Resolve(principal, sql.Source);
            EnforceReadOnlyText(sql.Text, principal, source);

            var result = await ProxyFailures.TranslateAsync(() => source.Executor
                .ExecuteQueryAsync(new QueryRequest(sql.Text, source.Options.BatchSize, source.Options.CommandTimeoutSeconds, Bind(sql.Parameters)), cancellationToken)).ConfigureAwait(false);
            return new Answer(result, new ServingProvenance(sql.Text, null, context.Channel, source.Name));
        }

        public Task<ServingStatementInfo> PrepareAsync(ServingContext context, ServingRequest.Sql statement, CancellationToken cancellationToken = default)
        {
            var principal = PrincipalOf(context.Caller);
            var source = Resolve(principal, statement.Source);
            // Prepare is also how some clients send updates, so only the read-only text rules are applied here (an update on a read-only source is refused when it runs)
            EnforceReadOnlyText(statement.Text, principal, source);
            return Task.FromResult(new ServingStatementInfo(ParameterMarkers.Count(statement.Text, ProviderDialects.For(source.Options.Provider))));
        }

        public async Task<ServingWriteResult> ExecuteAsync(ServingContext context, ServingRequest.Sql statement, CancellationToken cancellationToken = default)
        {
            var principal = PrincipalOf(context.Caller);
            var source = Resolve(principal, statement.Source);
            if (!source.Options.AllowWrites) throw new ServingDeniedException($"Data source '{source.Name}' is read-only.");

            long rows = await ProxyFailures.TranslateAsync(() => source.Executor
                .ExecuteNonQueryAsync(new QueryRequest(statement.Text, source.Options.BatchSize, source.Options.CommandTimeoutSeconds, Bind(statement.Parameters)), cancellationToken)).ConfigureAwait(false);
            return new ServingWriteResult(rows);
        }

        private ResolvedDataSource Resolve(ProxyPrincipal principal, string requested)
        {
            try { return sources.Resolve(principal, requested); }
            catch (Exception ex) when (ProxyFailures.ToServing(ex) is var serving && !ReferenceEquals(serving, ex)) { throw serving; }
        }

        private static IReadOnlyList<QueryParameter>? Bind(IReadOnlyList<ServingParameter>? parameters) =>
            parameters?.Select(p => new QueryParameter(p.Name, p.Type, p.Text)).ToList();

        /// <summary>
        /// On a read-only data source, checks the statement text with the shared classifier: one SELECT, no deny-listed words for the provider's dialect. Per the source's <see cref="DataSourceOptions.Classifier"/> mode a failure is refused or only logged.
        /// Defence in depth: the database login is what really enforces read-only.
        /// </summary>
        private void EnforceReadOnlyText(string sql, ProxyPrincipal principal, ResolvedDataSource source)
        {
            if (source.Options.AllowWrites || source.Options.Classifier == ClassifierMode.Off) return;
            var decision = ReadOnlyPolicy.Check(sql, ProviderDialects.For(source.Options.Provider));
            if (decision.Allowed) return;
            if (source.Options.Classifier == ClassifierMode.Strict)
            {
                log.LogWarning("Refused statement by {User} on read-only source {Source}: {Reason}", principal.Name, source.Name, decision.Reason);
                events?.Raise(DbDataKit.Monitoring.NotificationKinds.StatementRefused, $"{principal.Name}@{source.Name}",
                    $"A statement by '{principal.Name}' was refused on read-only data source '{source.Name}': {decision.Reason}.");
                throw new ServingDeniedException($"Data source '{source.Name}' is read-only and the statement was refused: {decision.Reason}.");
            }
            log.LogWarning("Statement by {User} on read-only source {Source} would be refused with Classifier=Strict: {Reason}", principal.Name, source.Name, decision.Reason);
        }
    }

    /// <summary>A query result as an answer: the batches say what the database failed with in the serving layer's words.</summary>
    private sealed class Answer(IQueryResult inner, ServingProvenance provenance) : IServingAnswer
    {
        public Schema Schema => inner.Schema;

        public ServingProvenance Provenance { get; } = provenance;

        public async IAsyncEnumerable<RecordBatch> ReadBatchesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await using var batches = inner.ReadBatchesAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                bool more;
                try { more = await batches.MoveNextAsync().ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException && ProxyFailures.ToServing(ex) is var serving && !ReferenceEquals(serving, ex)) { throw serving; }
                if (!more) yield break;
                yield return batches.Current;
            }
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
