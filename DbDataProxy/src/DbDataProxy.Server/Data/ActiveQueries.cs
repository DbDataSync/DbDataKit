using System.Collections.Concurrent;
using Apache.Arrow;
using DbDataKit.Connections;
using DbDataKit.Dialect;
using DbDataProxy.Data;
using Grpc.Core;

namespace DbDataProxy.Server.Data;

/// <summary>A statement that is running now, as the management API shows it.</summary>
/// <param name="Id">Names the query to cancel it.</param>
/// <param name="Sql">The statement text, cut to <see cref="ActiveQueries.MaxSqlChars"/>; null when the proxy is set not to log SQL (<c>Proxy:LogSql</c> false).</param>
public sealed record ActiveQueryInfo(string Id, string User, string DataSource, string Kind, DateTimeOffset StartedAt, string? Sql);

/// <summary>
/// The queries running on the proxy (plan 13, P06): each is entered when it starts and left when its result is disposed (a streaming result counts for as long as it is open), and each can be cancelled by id, which cancels
/// the token the query runs under, the same way a client that went away would.
/// </summary>
public sealed class ActiveQueries(TimeProvider? clock = null)
{
    public const int MaxSqlChars = 500;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Entry> _active = new(StringComparer.Ordinal);

    private sealed class Entry(ActiveQueryInfo info, CancellationTokenSource cancellation)
    {
        public ActiveQueryInfo Info { get; } = info;
        public CancellationTokenSource Cancellation { get; } = cancellation;
    }

    /// <summary>Enters a query. Dispose the handle when it ends.</summary>
    public Handle Begin(string user, string dataSource, string kind, string? sql, CancellationToken caller)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(caller);
        var shown = sql is null ? null : sql.Length <= MaxSqlChars ? sql : sql[..MaxSqlChars] + "…";
        _active[id] = new Entry(new ActiveQueryInfo(id, user, dataSource, kind, _clock.GetUtcNow(), shown), cancellation);
        return new Handle(this, id, cancellation);
    }

    public IReadOnlyList<ActiveQueryInfo> List() => [.. _active.Values.Select(e => e.Info).OrderBy(i => i.StartedAt).ThenBy(i => i.Id, StringComparer.Ordinal)];

    /// <summary>Cancels a running query; false when there is none by that id (it ended, or never was).</summary>
    public bool Cancel(string id)
    {
        if (!_active.TryGetValue(id, out var entry)) return false;
        entry.Cancellation.Cancel();
        return true;
    }

    public sealed class Handle(ActiveQueries owner, string id, CancellationTokenSource cancellation) : IDisposable
    {
        /// <summary>Cancelled when the caller's token is, or when the query is cancelled by id.</summary>
        public CancellationToken Token => cancellation.Token;

        public void Dispose()
        {
            owner._active.TryRemove(id, out _);
            cancellation.Dispose();
        }
    }
}

/// <summary>
/// Puts a data source's executor in the registry of running queries. Outermost, so a cancelled query also stops waiting for a slot of the concurrency gate. A cancelled query ends the way a client that hung up does: the
/// call ends with the cancellation, and the gRPC status the client sees is <c>Cancelled</c>.
/// </summary>
public sealed class TrackedExecutor(IQueryExecutor inner, ActiveQueries queries, string user, string source, bool includeSql) : IQueryExecutor, IMetadataProvider
{
    public async Task<IQueryResult> ExecuteQueryAsync(QueryRequest request, CancellationToken cancellationToken)
    {
        var handle = queries.Begin(user, source, "query", includeSql ? request.Sql : null, cancellationToken);
        try
        {
            return new TrackedResult(await inner.ExecuteQueryAsync(request, handle.Token).ConfigureAwait(false), handle);
        }
        catch (OperationCanceledException) when (handle.Token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            handle.Dispose();
            throw new RpcException(new Status(StatusCode.Cancelled, "The query was cancelled by an administrator."));
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public async Task<long> ExecuteNonQueryAsync(QueryRequest request, CancellationToken cancellationToken)
    {
        using var handle = queries.Begin(user, source, "update", includeSql ? request.Sql : null, cancellationToken);
        try
        {
            return await inner.ExecuteNonQueryAsync(request, handle.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (handle.Token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new RpcException(new Status(StatusCode.Cancelled, "The statement was cancelled by an administrator."));
        }
    }

    public Task<IReadOnlyList<SchemaEntry>> ListSchemasAsync(int? commandTimeoutSeconds, CancellationToken cancellationToken) => Metadata.ListSchemasAsync(commandTimeoutSeconds, cancellationToken);

    public Task<IReadOnlyList<TableEntry>> ListTablesAsync(int? commandTimeoutSeconds, CancellationToken cancellationToken) => Metadata.ListTablesAsync(commandTimeoutSeconds, cancellationToken);

    public Task<IReadOnlyList<ColumnEntry>> ListColumnsAsync(string? catalog, string? schema, string table, CancellationToken cancellationToken) => Metadata.ListColumnsAsync(catalog, schema, table, cancellationToken);

    public Task<IReadOnlyList<PrimaryKeyEntry>> ListPrimaryKeysAsync(string? catalog, string? schema, string table, CancellationToken cancellationToken) => Metadata.ListPrimaryKeysAsync(catalog, schema, table, cancellationToken);

    public Task<DialectInfo> GetDialectAsync(CancellationToken cancellationToken) => Metadata.GetDialectAsync(cancellationToken);

    private IMetadataProvider Metadata => inner as IMetadataProvider ?? throw new UnsupportedValueException("This data source cannot list catalog metadata.");

    private sealed class TrackedResult(IQueryResult result, ActiveQueries.Handle handle) : IQueryResult
    {
        public Schema Schema => result.Schema;

        public async IAsyncEnumerable<RecordBatch> ReadBatchesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            // the registry's token and the caller's: either ends the read
            using var both = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, handle.Token);
            await foreach (var batch in result.ReadBatchesAsync(both.Token).WithCancellation(both.Token).ConfigureAwait(false))
                yield return batch;
        }

        public async ValueTask DisposeAsync()
        {
            try { await result.DisposeAsync().ConfigureAwait(false); }
            finally { handle.Dispose(); }
        }
    }
}
