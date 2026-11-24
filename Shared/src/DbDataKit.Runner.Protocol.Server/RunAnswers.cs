namespace DbDataKit.Runner.Protocol.Server;

/// <summary>
/// The answers of runs a person waits for on a screen (the rows of a preview, a strategy's candidates): what a task handed the runner with <c>Answer</c>, kept in this process's memory for the console that started
/// the run to take once (<see cref="TryTake"/>). Nothing of an answer goes into the state, a step, the run's log or the audit: an answer can hold rows of a customer's table, which the suite keeps nowhere. An answer
/// that is not taken is forgotten after <see cref="Keep"/>, and the oldest answers are forgotten first when more than <see cref="MaxTotalChars"/> are held, so the memory an answer takes is bounded.
/// Design: docs/authoring-metadata.md#rows-and-trials-are-answered-never-kept
/// </summary>
public sealed class RunAnswers(TimeProvider? time = null)
{
    /// <summary>The longest answer a run may hand over, in characters of JSON.</summary>
    public const int MaxAnswerChars = 2 * 1024 * 1024;

    /// <summary>How many characters of answers are held at most; past it the oldest are forgotten.</summary>
    public const long MaxTotalChars = 64L * 1024 * 1024;

    /// <summary>How long an answer waits to be taken.</summary>
    public static readonly TimeSpan Keep = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, (string Json, DateTimeOffset At)> _held = [];
    private long _total;

    /// <summary>Holds <paramref name="json"/> as the answer of the run <paramref name="runId"/>, in place of an earlier one. Throws <see cref="ArgumentException"/> for an answer over <see cref="MaxAnswerChars"/>.</summary>
    public void Put(Guid runId, string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaxAnswerChars)
            throw new ArgumentException($"The answer is {json.Length:N0} characters; a run answers at most {MaxAnswerChars:N0}. Answer fewer rows.", nameof(json));
        lock (_gate)
        {
            Prune();
            Remove(runId);
            _held[runId] = (json, _time.GetUtcNow());
            _total += json.Length;
            while (_total > MaxTotalChars && _held.Count > 1)
                Remove(_held.Where(h => h.Key != runId).MinBy(h => h.Value.At).Key);
        }
    }

    /// <summary>The answer of <paramref name="runId"/>, which is forgotten as it is handed out; false when this process holds none (never given, taken already, or forgotten).</summary>
    public bool TryTake(Guid runId, out string json)
    {
        lock (_gate)
        {
            Prune();
            if (_held.TryGetValue(runId, out var held))
            {
                Remove(runId);
                json = held.Json;
                return true;
            }
        }

        json = "";
        return false;
    }

    /// <summary>Whether this process holds an answer of <paramref name="runId"/> now.</summary>
    public bool Holds(Guid runId)
    {
        lock (_gate)
        {
            Prune();
            return _held.ContainsKey(runId);
        }
    }

    private void Remove(Guid runId)
    {
        if (_held.Remove(runId, out var gone)) _total -= gone.Json.Length;
    }

    private void Prune()
    {
        var before = _time.GetUtcNow() - Keep;
        foreach (var old in _held.Where(h => h.Value.At < before).Select(h => h.Key).ToList()) Remove(old);
    }
}
