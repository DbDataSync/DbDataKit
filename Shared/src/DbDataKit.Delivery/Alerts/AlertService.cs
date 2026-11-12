using DbDataKit.Monitoring;
using DbDataKit.State;
using Microsoft.Extensions.Logging;

namespace DbDataKit.Delivery;

/// <summary>One thing an alert source says is wrong right now: <paramref name="Source"/> names what (a job, a dataset, a preview; stable from one reading to the next, so the cool-down and the recovery find it again).</summary>
public sealed record AlertReading(string Kind, string Source, string Message, Severity Severity = Severity.Error);

/// <summary>Something that can say what is wrong right now (plan 22a GP16): the health rules over runs, the approvals waiting, the previews about to expire. Each host registers the ones it has.</summary>
public interface IAlertSource
{
    Task<IReadOnlyList<AlertReading>> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The health findings of <see cref="HealthEvaluator"/> as alert readings: a failed run is <see cref="AlertKinds.FailedRun"/>; overdue, lagging and a failed data check are <see cref="AlertKinds.LateData"/>; slow and running too long are not alerts (they stay on the health page).
/// </summary>
public sealed class HealthAlertSource(Func<CancellationToken, Task<IReadOnlyList<JobHealth>>> health) : IAlertSource
{
    public async Task<IReadOnlyList<AlertReading>> ReadAsync(CancellationToken cancellationToken)
    {
        var readings = new List<AlertReading>();
        foreach (var job in await health(cancellationToken).ConfigureAwait(false))
            foreach (var f in job.Findings)
            {
                var kind = f.Rule switch { HealthRule.Failed => AlertKinds.FailedRun, HealthRule.Overdue or HealthRule.Lagging or HealthRule.CheckFailed => AlertKinds.LateData, _ => null };
                if (kind is not null) readings.Add(new AlertReading(kind, $"{f.Rule.ToString().ToLowerInvariant()}:{f.JobId}", f.Message, f.Severity));
            }

        return readings;
    }
}

/// <summary>
/// The failed runs of the install's run history (the <c>runs</c> module, every service and package that wrote to it): the last run of each job within <paramref name="window"/> that failed, was interrupted or finished with failures is a reading of kind
/// <see cref="AlertKinds.FailedRun"/>, source <c>failed:&lt;job&gt;</c>; a job whose last run succeeded is not failing, so a later good run is the recovery.
/// </summary>
public sealed class RunHistoryAlertSource(RunStore runs, TimeProvider? time = null, TimeSpan? window = null) : IAlertSource
{
    public Task<IReadOnlyList<AlertReading>> ReadAsync(CancellationToken cancellationToken)
    {
        var now = (time ?? TimeProvider.System).GetUtcNow();
        var evaluator = new HealthEvaluator(time);
        var since = now - (window ?? TimeSpan.FromDays(7));
        var last = runs.Read(new RunQuery(StartedSince: since, Limit: 1000)).GroupBy(r => r.Run.JobId, StringComparer.Ordinal).Select(g => g.OrderByDescending(r => r.Run.Started).First());
        var readings = new List<AlertReading>();
        foreach (var run in last)
            foreach (var f in evaluator.Evaluate(new JobExpectation(run.Run.JobId), run.Run).Findings.Where(f => f.Rule == HealthRule.Failed))
                readings.Add(new AlertReading(AlertKinds.FailedRun, "failed:" + run.Run.JobId, f.Message, f.Severity));
        return Task.FromResult<IReadOnlyList<AlertReading>>(readings);
    }
}

/// <summary>What one pass of the alert service did: messages written, recoveries told, sources kept quiet by the cool-down, alerts shown in the feed only, and what could not be done.</summary>
public sealed record AlertRunResult(int Sent, int Recovered, int Quiet, int FeedOnly, IReadOnlyList<string> Problems);

/// <summary>
/// Turns what the alert sources say into messages (plan 22a GP16): a <b>failed run sends once, not every minute</b> (a source still failing stays quiet until the rule's cool-down has passed, then it is told again), a <b>recovered source sends one "recovered"</b>
/// and is marked ok, and an alert whose rule has <b>no channel</b> is <b>shown in the notification feed only</b>. The state is per source and recipient (<c>alert_state</c>), so a cool-down is each recipient's own. A kind that is off is neither told nor tracked.
/// The message goes through the delivery service (the outbox, the retries, the release-channel binding) with source <c>alert:&lt;kind&gt;</c>. A source that cannot be read ends the pass without telling anything as recovered.
/// </summary>
public sealed class AlertService(AlertStore store, DeliveryService delivery, IReadOnlyList<IAlertSource> sources, INotificationFeed? feed = null, TimeProvider? time = null, ILogger<AlertService>? log = null)
{
    /// <summary>The notification kinds an alert and a recovery are raised under.</summary>
    public const string FeedKind = "Alert";

    public const string RecoveredFeedKind = "AlertRecovered";

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<AlertRunResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var rules = store.Rules().ToDictionary(r => r.Kind, StringComparer.Ordinal);
        var readings = new List<AlertReading>();
        var problems = new List<string>();
        foreach (var source in sources)
        {
            try
            {
                readings.AddRange(await source.ReadAsync(cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                problems.Add($"{source.GetType().Name}: {ex.Message}");
                log?.LogWarning("An alert source failed: {Source}: {Message}", source.GetType().Name, ex.Message);
                return new AlertRunResult(0, 0, 0, 0, problems);
            }
        }

        int sent = 0, recovered = 0, quiet = 0, feedOnly = 0;
        var now = _time.GetUtcNow();
        var states = store.States().ToDictionary(s => (s.Source, s.Recipient));
        var failingNow = new HashSet<string>(StringComparer.Ordinal);

        foreach (var reading in readings.GroupBy(r => r.Source, StringComparer.Ordinal).Select(g => g.First()))
        {
            if (!rules.TryGetValue(reading.Kind, out var rule) || !rule.Enabled) continue;
            failingNow.Add(reading.Source);
            var anyTold = false;
            foreach (var recipient in Recipients(rule))
            {
                states.TryGetValue((reading.Source, recipient), out var state);
                if (state is { Status: AlertStateRow.Failing } && now - state.LastSentAt < TimeSpan.FromMinutes(rule.CooldownMinutes)) { quiet++; continue; }
                if (rule.Channel is not null)
                {
                    if (!await TrySend(rule, reading.Kind, recipient, $"Alert: {AlertKinds.Title(reading.Kind)}", reading.Message, problems, cancellationToken).ConfigureAwait(false)) continue;
                    sent++;
                }
                else feedOnly++;
                anyTold = true;
                store.Record(reading.Source, recipient, reading.Kind, AlertStateRow.Failing, reading.Message);
            }

            if (anyTold) feed?.Raise(FeedKind, reading.Message, mappingName: reading.Source);
        }

        // what was failing and is not now: one "recovered" to each recipient that was told, then the row says ok
        foreach (var state in states.Values.Where(s => s.Status == AlertStateRow.Failing && !failingNow.Contains(s.Source)))
        {
            var rule = rules.GetValueOrDefault(state.Kind);
            if (rule is { Enabled: true })
            {
                var text = $"Recovered: {state.LastMessage ?? state.Source}";
                if (rule.Channel is not null)
                {
                    if (!await TrySend(rule, state.Kind, state.Recipient, $"Recovered: {AlertKinds.Title(state.Kind)}", text, problems, cancellationToken).ConfigureAwait(false)) continue;
                    recovered++;
                }
                else
                {
                    feed?.Raise(RecoveredFeedKind, text, mappingName: state.Source);
                    recovered++;
                }
            }

            store.Record(state.Source, state.Recipient, state.Kind, AlertStateRow.Ok, state.LastMessage);
        }

        return new AlertRunResult(sent, recovered, quiet, feedOnly, problems);
    }

    /// <summary>The addresses a rule tells, or the channel's own address when it lists none (a webhook) or there is no channel (the feed).</summary>
    private static IReadOnlyList<string> Recipients(AlertRule rule) => rule.Channel is null || rule.Recipients.Count == 0 ? [AlertStateRow.ChannelRecipient] : rule.Recipients;

    private async Task<bool> TrySend(AlertRule rule, string kind, string recipient, string subject, string text, List<string> problems, CancellationToken ct)
    {
        try
        {
            await delivery.SendAsync(new DeliveryRequest(rule.Channel!, subject, recipient == AlertStateRow.ChannelRecipient ? null : [recipient], Text: text, Source: "alert:" + kind, ReleaseChannel: rule.ReleaseChannel), ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is UnknownChannelException or ChannelNotBoundException)
        {
            problems.Add($"{AlertKinds.Title(kind)}: {ex.Message}");
            return false;
        }
    }
}
