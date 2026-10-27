using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace DbDataKit.Serving.FlightSql;

/// <summary>
/// A query that has been run for <c>GetFlightInfo</c> (so its schema is known) and is waiting for the client's <c>DoGet</c>. Holding the open answer avoids running the statement twice.
/// </summary>
public sealed class PendingAnswer(string owner, string source, IServingAnswer answer, DateTimeOffset expiresAt)
{
    public string Owner { get; } = owner;

    /// <summary>The name of the source it came from, as the host reported it (settings that belong to the source, such as its compression, are looked up by it).</summary>
    public string Source { get; } = source;

    public IServingAnswer Answer { get; } = answer;
    public DateTimeOffset ExpiresAt { get; } = expiresAt;
}

public sealed class PendingAnswerStore(TimeProvider time) : IAsyncDisposable
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    public const int MaxPerOwner = 32;

    private readonly ConcurrentDictionary<string, PendingAnswer> _pending = new();

    /// <summary>Refuses new queries from a caller that has too many run-but-unfetched results open.</summary>
    public void EnsureCapacity(string owner)
    {
        int open = _pending.Values.Count(p => string.Equals(p.Owner, owner, StringComparison.OrdinalIgnoreCase));
        if (open >= MaxPerOwner)
            throw new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.ResourceExhausted, $"Too many queries are waiting to be fetched ({open}); fetch or abandon some first."));
    }

    public byte[] Add(string owner, string source, IServingAnswer answer)
    {
        byte[] handle = RandomNumberGenerator.GetBytes(16);
        _pending[Convert.ToHexString(handle)] = new PendingAnswer(owner, source, answer, time.GetUtcNow() + Lifetime);
        return handle;
    }

    /// <summary>Removes and returns the query if it exists, has not expired and belongs to <paramref name="owner"/>.</summary>
    public PendingAnswer? Take(byte[] handle, string owner)
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
                await removed.Answer.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var key in _pending.Keys.ToList())
            if (_pending.TryRemove(key, out var q))
                await q.Answer.DisposeAsync().ConfigureAwait(false);
    }
}
