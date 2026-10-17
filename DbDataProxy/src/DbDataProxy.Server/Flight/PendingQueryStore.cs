using System.Collections.Concurrent;
using System.Security.Cryptography;
using DbDataProxy.Data;
using DbDataProxy.Server.Data;

namespace DbDataProxy.Server.Flight;

/// <summary>
/// A query that has been executed for <c>GetFlightInfo</c> (so its schema is known) and is waiting for the client's
/// <c>DoGet</c>. Holding the open reader avoids running the statement twice.
/// </summary>
public sealed class PendingQuery(string owner, ResolvedDataSource source, IQueryResult result, DateTimeOffset expiresAt)
{
    public string Owner { get; } = owner;
    public ResolvedDataSource Source { get; } = source;
    public IQueryResult Result { get; } = result;
    public DateTimeOffset ExpiresAt { get; } = expiresAt;
}

public sealed class PendingQueryStore(TimeProvider time) : IAsyncDisposable
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<string, PendingQuery> _pending = new();

    public const int MaxPerOwner = 32;

    /// <summary>Refuses new queries from a caller that has too many executed-but-unfetched results open.</summary>
    public void EnsureCapacity(string owner)
    {
        int open = _pending.Values.Count(p => string.Equals(p.Owner, owner, StringComparison.OrdinalIgnoreCase));
        if (open >= MaxPerOwner)
            throw new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.ResourceExhausted,
                $"Too many queries are waiting to be fetched ({open}); fetch or abandon some first."));
    }

    public byte[] Add(string owner, ResolvedDataSource source, IQueryResult result)
    {
        byte[] handle = RandomNumberGenerator.GetBytes(16);
        _pending[Convert.ToHexString(handle)] = new PendingQuery(owner, source, result, time.GetUtcNow() + Lifetime);
        return handle;
    }

    /// <summary>Removes and returns the query if it exists, has not expired and belongs to <paramref name="owner"/>.</summary>
    public PendingQuery? Take(byte[] handle, string owner)
    {
        string key = Convert.ToHexString(handle);
        if (!_pending.TryGetValue(key, out var query)) return null;
        if (!string.Equals(query.Owner, owner, StringComparison.OrdinalIgnoreCase)) return null;
        if (!_pending.TryRemove(key, out query)) return null;
        return query.ExpiresAt > time.GetUtcNow() ? query : null;
    }

    /// <summary>Disposes queries that were never fetched.</summary>
    public async Task SweepAsync()
    {
        var now = time.GetUtcNow();
        foreach (var (key, query) in _pending)
            if (query.ExpiresAt <= now && _pending.TryRemove(key, out var removed))
                await removed.Result.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var key in _pending.Keys.ToList())
            if (_pending.TryRemove(key, out var q))
                await q.Result.DisposeAsync().ConfigureAwait(false);
    }
}
