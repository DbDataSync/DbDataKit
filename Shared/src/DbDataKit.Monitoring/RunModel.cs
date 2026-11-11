namespace DbDataKit.Monitoring;

/// <summary>How a run ended, or that it has not.</summary>
public enum RunOutcome
{
    /// <summary>Under way.</summary>
    Running,
    Succeeded,
    /// <summary>It finished, and some of its items failed (Build's <c>completed_with_failures</c>).</summary>
    CompletedWithFailures,
    Failed,
    /// <summary>Stopped on purpose before it finished (an operator, or a failure policy).</summary>
    Stopped,
    /// <summary>Started and never finished: the process went away. The product decides when a run it recorded as started is this.</summary>
    Interrupted,
}

/// <summary>A failure, classified. <see cref="Kind"/> is the product's name for the class (the shared classifier of <c>DbDataKit.Connections</c> gives one); <see cref="Message"/> never holds a secret.</summary>
public sealed record RunFailure(string Kind, string Message);

/// <summary>One run of a job. A job is whatever a product schedules: a replication, a refresh plan.</summary>
/// <param name="JobId">The job it belongs to.</param>
/// <param name="RunId">This run.</param>
/// <param name="Started">When it began.</param>
/// <param name="Finished">When it ended; null while it is <see cref="RunOutcome.Running"/>.</param>
/// <param name="Outcome">How it ended, or that it is still running.</param>
/// <param name="Rows">How many rows it moved, when the product counts.</param>
/// <param name="FailedItem">The item that failed (a table, a load), when one did.</param>
/// <param name="Failure">Why, classified.</param>
public sealed record RunRecord(string JobId, string RunId, DateTimeOffset Started, DateTimeOffset? Finished, RunOutcome Outcome,
    long? Rows = null, string? FailedItem = null, RunFailure? Failure = null)
{
    public TimeSpan? Duration => Finished is { } end ? end - Started : null;
}

/// <summary>How far behind its source a job is, measured at a moment (Sync's replication lag; for Build, how old the data it publishes is).</summary>
public sealed record LagReading(TimeSpan Behind, DateTimeOffset MeasuredAt);

/// <summary>What a job is expected to do. Every limit is optional; a job with none has nothing to be late at.</summary>
/// <param name="JobId">The job.</param>
/// <param name="ExpectedEvery">How often it should start (the plan's <c>expected_every</c>): later than this is overdue, and a run that takes longer than this is slow.</param>
/// <param name="MaxRuntime">How long a run may go on for before it is reported as running too long.</param>
/// <param name="MaxLag">How far behind its source it may be.</param>
public sealed record JobExpectation(string JobId, TimeSpan? ExpectedEvery = null, TimeSpan? MaxRuntime = null, TimeSpan? MaxLag = null);

/// <summary>What each product supplies: Sync from its state database, Build from its tracking tables, which stay where they are.</summary>
public interface IRunHistory
{
    Task<RunRecord?> LastRunAsync(string jobId, CancellationToken ct = default);

    Task<LagReading?> LagAsync(string jobId, CancellationToken ct = default);
}
