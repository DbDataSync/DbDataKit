using DbDataKit.State;

namespace DbDataKit.Quality;

/// <summary>A check as another server reported it (the console reads overviews, plan 23a FQ17), for the watcher.</summary>
/// <param name="Evaluator">The server that evaluated it; the same check on two servers is two things to watch.</param>
public sealed record ObservedCheck(string Evaluator, string Dataset, string CheckId, string Severity, string OnError, CheckOutcome Status, bool Blocking, decimal? Measured = null, decimal? Threshold = null, string? Message = null);

/// <summary>What one pass of the watcher did.</summary>
public sealed record WatchReport(int Observed, int Alerts, IReadOnlyList<string> Problems);

/// <summary>
/// Alerts for checks that are evaluated somewhere else (plan 23a FQ13): the console does not evaluate, the Sync and Build servers do, and the delivery channels are the console's, so the console watches what the servers report and alerts on the <b>change</b>. It remembers
/// the outcome each check was last alerted at in the <c>quality</c> module (<see cref="QualityStore.AlertedOutcome"/>), so a failure is alerted once however often it is observed, a recovery once, and a restart of the console says nothing again. A check seen for the first time
/// that passes is only remembered; one seen failing is alerted. An alert that could not be written is not remembered, so the next pass tries again; a failure to alert is a problem in the report and never throws.
/// </summary>
public sealed class QualityAlertWatcher(QualityStore store, IQualityAlerts alerts, Func<ObservedCheck, CancellationToken, Task<IReadOnlyList<string>>>? sampleKeys = null, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<WatchReport> ObserveAsync(IEnumerable<ObservedCheck> observed, CancellationToken ct = default)
    {
        var seen = 0;
        var sent = 0;
        var problems = new List<string>();
        foreach (var check in observed)
        {
            ct.ThrowIfCancellationRequested();
            seen++;
            var previous = store.AlertedOutcome(check.Evaluator, check.Dataset, check.CheckId);
            if (DeliveryQualityAlerts.Transition(previous, check.Status) is not { } kind)
            {
                if (previous is null) store.SetAlerted(check.Evaluator, check.Dataset, check.CheckId, check.Status, _time.GetUtcNow());   // first seen and passing
                continue;
            }

            try
            {
                IReadOnlyList<string> keys = [];
                if (kind == QualityAlertKind.Failed && check.Status == CheckOutcome.Fail && sampleKeys is not null)
                {
                    try { keys = await sampleKeys(check, ct).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { keys = []; }      // the alert is worth sending without them
                }

                await alerts.SendAsync(new QualityAlert(kind, check.Dataset, check.CheckId, check.Severity, check.OnError, check.Status, previous, check.Blocking, [], check.Measured, check.Threshold, keys, check.Message, Source: check.Evaluator), ct).ConfigureAwait(false);
                store.SetAlerted(check.Evaluator, check.Dataset, check.CheckId, check.Status, _time.GetUtcNow());
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                problems.Add($"{check.CheckId} ({check.Dataset}, {check.Evaluator}): the alert was not sent: {ex.Message}");
            }
        }

        return new WatchReport(seen, sent, problems);
    }
}
