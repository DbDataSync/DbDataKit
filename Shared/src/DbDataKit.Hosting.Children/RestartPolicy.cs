namespace DbDataKit.Hosting.Children;

public sealed record RestartPolicy
{
    /// <summary>Restarts allowed within <see cref="Window"/> before giving up.</summary>
    public int MaxRestarts { get; init; } = 5;

    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(10);

    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromMinutes(1);

    public double Multiplier { get; init; } = 2.0;

    /// <summary>A run at least this long resets the backoff.</summary>
    public TimeSpan StableAfter { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Exit code 0 normally means "done"; set for always-on children.</summary>
    public bool RestartOnCleanExit { get; init; }

    public static RestartPolicy Never { get; } = new() { MaxRestarts = 0 };
}

public sealed record RestartDecision(bool Restart, TimeSpan Delay, string Reason);

/// <summary>Pure restart/backoff logic; time is passed in, so it is deterministic to test.</summary>
public sealed class RestartTracker(RestartPolicy policy)
{
    private readonly Queue<DateTimeOffset> _restarts = new();
    private int _consecutiveFailures;

    public int ConsecutiveFailures => _consecutiveFailures;

    public RestartDecision OnExit(DateTimeOffset now, int exitCode, TimeSpan uptime)
    {
        if (exitCode == 0 && !policy.RestartOnCleanExit)
        {
            return new RestartDecision(false, TimeSpan.Zero, "exited cleanly");
        }

        if (uptime >= policy.StableAfter)
        {
            _consecutiveFailures = 0;
            _restarts.Clear();
        }

        while (_restarts.Count > 0 && now - _restarts.Peek() > policy.Window)
        {
            _restarts.Dequeue();
        }

        if (_restarts.Count >= policy.MaxRestarts)
        {
            return new RestartDecision(false, TimeSpan.Zero, $"gave up after {_restarts.Count} restart(s) within {policy.Window.TotalMinutes:0.#} min (exit code {exitCode})");
        }

        var seconds = policy.InitialDelay.TotalSeconds * Math.Pow(policy.Multiplier, _consecutiveFailures);
        var delay = TimeSpan.FromSeconds(Math.Min(seconds, policy.MaxDelay.TotalSeconds));
        _consecutiveFailures++;
        _restarts.Enqueue(now);
        return new RestartDecision(true, delay, $"restarting in {delay.TotalSeconds:0.#}s after exit code {exitCode}");
    }
}
