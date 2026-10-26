using DbDataKit.Connections;
using DbDataProxy.Data;

namespace DbDataProxy.Server.Data;

/// <summary>A database failure the proxy classified, as the management API shows it: the kind, the engine's code and the fixed sentence for the kind. Never the driver's text.</summary>
public sealed record RecentFailure(DateTimeOffset At, string DataSource, string Kind, string? Code, string? Engine, string Message);

/// <summary>
/// The last failures the proxy classified (plan 13, P07), newest last, kept in memory and bounded. It is told about each one the way the log is (<see cref="IFailureSink"/>), and keeps only what a client would be told: the kind and
/// the code with the public sentence, so what an administrator reads here is what the user who hit it was shown.
/// </summary>
public sealed class RecentFailures(int capacity = RecentFailures.DefaultCapacity, TimeProvider? clock = null) : IFailureSink
{
    public const int DefaultCapacity = 200;

    private readonly Queue<RecentFailure> _items = new();
    private readonly object _gate = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public void Failed(string source, ConnectionFailure failure)
    {
        var entry = new RecentFailure(_clock.GetUtcNow(), source, failure.Kind.ToString(), failure.Code?.ToString(), failure.Engine?.Value, failure.PublicMessage);
        lock (_gate)
        {
            _items.Enqueue(entry);
            while (_items.Count > capacity) _items.Dequeue();
        }
    }

    /// <summary>The newest <paramref name="limit"/> failures, newest first.</summary>
    public IReadOnlyList<RecentFailure> Newest(int limit = 50)
    {
        lock (_gate) return [.. _items.Reverse().Take(limit)];
    }
}

/// <summary>Tells every sink about a failure.</summary>
public sealed class FailureSinks(params IFailureSink?[] sinks) : IFailureSink
{
    public void Failed(string source, ConnectionFailure failure)
    {
        foreach (var sink in sinks) sink?.Failed(source, failure);
    }
}
