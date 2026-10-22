namespace DbDataKit.Web.Trust;

/// <summary>Remembers token ids until they could no longer be valid, so a token is accepted once.</summary>
public interface IReplayCache
{
    /// <summary>True when <paramref name="tokenId"/> had not been seen (and now has); false for a replay.</summary>
    bool TryAdd(string consoleId, string tokenId, DateTimeOffset forgetAfter);
}

/// <summary>An in-process replay cache. A server that runs as several instances behind one address needs a shared one; the interface is the seam.</summary>
public sealed class MemoryReplayCache(TimeProvider? clock = null, int maxEntries = 100_000) : IReplayCache
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private DateTimeOffset _nextSweep = DateTimeOffset.MinValue;

    public bool TryAdd(string consoleId, string tokenId, DateTimeOffset forgetAfter)
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            if (now >= _nextSweep || _seen.Count >= maxEntries)
            {
                foreach (var stale in _seen.Where(p => p.Value <= now).Select(p => p.Key).ToList()) _seen.Remove(stale);
                _nextSweep = now.AddMinutes(1);
            }
            // a flood of valid tokens past the limit is refused rather than forgotten: forgetting would let one be replayed
            if (_seen.Count >= maxEntries) return false;
            return _seen.TryAdd(consoleId + "\n" + tokenId, forgetAfter);
        }
    }

    public int Count { get { lock (_gate) return _seen.Count; } }
}
