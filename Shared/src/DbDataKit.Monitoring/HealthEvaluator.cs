namespace DbDataKit.Monitoring;

/// <summary>
/// The health rules, over a job's expectations and its last run: the same ones Build's <c>status</c> computes for a plan with <c>expected_every</c> (overdue when the last run began longer ago than that or the plan never ran; slow when
/// the last run finished and took longer than that; a run still going is neither until it has been going that long), plus failed, running too long and lagging. Pure: it reads nothing but its arguments and the clock, so a
/// scheduler can call it on every tick and a view can call it when someone looks, and both get the same answer.
/// </summary>
public sealed class HealthEvaluator(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public JobHealth Evaluate(JobExpectation expectation, RunRecord? last, LagReading? lag = null)
    {
        var now = _clock.GetUtcNow();
        var job = expectation.JobId;
        var findings = new List<HealthFinding>();

        if (expectation.ExpectedEvery is { } every)
        {
            if (last is null)
                findings.Add(new(job, HealthRule.Overdue, Severity.Warning, $"{job} is meant to run every {Span(every)} and has never run.", Limit: every));
            else
            {
                var age = now - last.Started;
                if (age > every)
                    findings.Add(new(job, HealthRule.Overdue, Severity.Warning, $"{job} is meant to run every {Span(every)}; its last run began {Span(age)} ago.", age, every));
                if (last.Duration is { } took && took > every)
                    findings.Add(new(job, HealthRule.Slow, Severity.Warning, $"The last run of {job} took {Span(took)}, longer than its expected {Span(every)}.", took, every));
            }
        }

        if (last is { Outcome: RunOutcome.Running } && expectation.MaxRuntime is { } max && now - last.Started > max)
            findings.Add(new(job, HealthRule.RunningTooLong, Severity.Error, $"A run of {job} has been going for {Span(now - last.Started)}, past its maximum of {Span(max)}.", now - last.Started, max));

        if (last is not null)
        {
            var failed = Failure(job, last);
            if (failed is not null) findings.Add(failed);
        }

        if (lag is not null && expectation.MaxLag is { } maxLag && lag.Behind > maxLag)
            findings.Add(new(job, HealthRule.Lagging, Severity.Warning, $"{job} is {Span(lag.Behind)} behind its source, more than the {Span(maxLag)} allowed.", lag.Behind, maxLag));

        // the worst first, then in the order the rules are declared
        return new JobHealth(job, findings.OrderByDescending(f => f.Severity).ThenBy(f => f.Rule).ToList());
    }

    /// <summary>
    /// The <see cref="HealthRule.CheckFailed"/> findings over the latest results of checks, one <see cref="JobHealth"/> per dataset that has a failing or erroring check (a dataset whose checks all pass has none, so it is not listed): the job id is the
    /// dataset's identity. A failed check is an <see cref="Severity.Error"/> when it blocks and a <see cref="Severity.Warning"/> when it only warns; a check that could not run is a warning (it did not show the data to be bad, plan 23a QF10) unless it blocks.
    /// </summary>
    public IReadOnlyList<JobHealth> EvaluateChecks(IEnumerable<CheckReading> readings)
    {
        ArgumentNullException.ThrowIfNull(readings);
        static string Number(decimal value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var all = new List<JobHealth>();
        foreach (var group in readings.GroupBy(r => r.Dataset, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var findings = new List<HealthFinding>();
            foreach (var r in group.OrderBy(r => r.CheckId, StringComparer.Ordinal))
            {
                if (string.Equals(r.Status, "pass", StringComparison.OrdinalIgnoreCase)) continue;
                var error = string.Equals(r.Status, "error", StringComparison.OrdinalIgnoreCase);
                var blocks = r.Blocking ? ", and it blocks what depends on the dataset" : "";
                var numbers = !error && r.Measured is { } m ? $" (measured {Number(m)}{(r.Threshold is { } t ? $", allowed {Number(t)}" : "")})" : "";
                var message = error ? $"The check {r.CheckId} on {group.Key} could not run{blocks}." : $"The check {r.CheckId} on {group.Key} failed{numbers}{blocks}.";
                findings.Add(new HealthFinding(group.Key, HealthRule.CheckFailed, r.Blocking ? Severity.Error : Severity.Warning, message));
            }
            if (findings.Count > 0) all.Add(new JobHealth(group.Key, findings.OrderByDescending(f => f.Severity).ToList()));
        }
        return all;
    }

    /// <summary>Evaluates every job the expectations name, asking the history for each one's last run and lag.</summary>
    public async Task<IReadOnlyList<JobHealth>> EvaluateAsync(IEnumerable<JobExpectation> expectations, IRunHistory history, CancellationToken ct = default)
    {
        var all = new List<JobHealth>();
        foreach (var expectation in expectations)
        {
            var last = await history.LastRunAsync(expectation.JobId, ct).ConfigureAwait(false);
            var lag = expectation.MaxLag is null ? null : await history.LagAsync(expectation.JobId, ct).ConfigureAwait(false);
            all.Add(Evaluate(expectation, last, lag));
        }
        return all;
    }

    private static HealthFinding? Failure(string job, RunRecord run)
    {
        var item = run.FailedItem is null ? "" : $" ({run.FailedItem})";
        var why = run.Failure is null ? "" : $": {run.Failure.Kind}: {run.Failure.Message}";
        return run.Outcome switch
        {
            RunOutcome.Failed => new(job, HealthRule.Failed, Severity.Error, $"The last run of {job} failed{item}{why}.", run.Duration),
            RunOutcome.Interrupted => new(job, HealthRule.Failed, Severity.Error, $"The last run of {job} began and never finished: the process went away{item}.", null),
            RunOutcome.Stopped => new(job, HealthRule.Failed, Severity.Warning, $"The last run of {job} was stopped before it finished{item}{why}.", run.Duration),
            RunOutcome.CompletedWithFailures => new(job, HealthRule.Failed, Severity.Warning, $"The last run of {job} finished, but some of its items failed{item}{why}.", run.Duration),
            _ => null,
        };
    }

    /// <summary>A span in the unit a person reads: seconds up to two minutes, minutes up to two hours, hours up to two days, then days.</summary>
    public static string Span(TimeSpan span)
    {
        var seconds = (long)span.TotalSeconds;
        return seconds < 120 ? $"{seconds} s" : seconds < 7200 ? $"{seconds / 60} min" : seconds < 172800 ? $"{seconds / 3600} h" : $"{seconds / 86400} d";
    }
}
