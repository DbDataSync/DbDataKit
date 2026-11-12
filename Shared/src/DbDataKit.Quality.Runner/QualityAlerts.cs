using System.Globalization;
using System.Text;
using DbDataKit.Delivery;
using DbDataKit.State;

namespace DbDataKit.Quality;

/// <summary>What an alert says: a check turned unhealthy (<see cref="Failed"/>, also when it could not run) or healthy again (<see cref="Recovered"/>).</summary>
public enum QualityAlertKind { Failed, Recovered }

/// <summary>One alert (plan 23a FQ13): sent when a check's status <b>changes</b>, once per failure and once per recovery, never once per evaluation.</summary>
/// <param name="Previous">The status the check had before this evaluation; null when it had none.</param>
/// <param name="Status">What the check's result is now: <see cref="CheckOutcome.Fail"/> or <see cref="CheckOutcome.Error"/> for a failed alert, <see cref="CheckOutcome.Pass"/> for a recovered one.</param>
/// <param name="CheckKind">The kind (<c>not_null</c>, ...) when the source knows it; the console, which reads only overviews, does not.</param>
/// <param name="Source">The server that evaluated the check when more than one can (the console watches several), else null.</param>
public sealed record QualityAlert(QualityAlertKind Kind, string Dataset, string CheckId, string Severity, string OnError, CheckOutcome Status, CheckOutcome? Previous, bool Blocking, IReadOnlyList<string> Owners,
    decimal? Measured = null, decimal? Threshold = null, IReadOnlyList<string>? SampleKeys = null, string? Message = null, string? CheckKind = null, string? Source = null);

/// <summary>Where a run's alerts go. A failure to alert never fails the evaluation: the run service reports it in <see cref="QualityRunReport.AlertProblems"/>.</summary>
public interface IQualityAlerts
{
    Task SendAsync(QualityAlert alert, CancellationToken ct = default);
}

/// <summary>Where the alerts of this install go (the delivery channel, and who gets them besides a dataset's owners).</summary>
/// <param name="Channel">The delivery channel's name (the <c>delivery:</c> section).</param>
/// <param name="Recipients">Addresses that get every alert; a dataset's owners are added to them.</param>
/// <param name="ReleaseChannel">The release channel the messages are written in; null: production.</param>
/// <param name="ConsoleAddress">The console's address, for a link to its Quality page; null: no link.</param>
public sealed record QualityAlertSettings(string Channel, IReadOnlyList<string>? Recipients = null, string? ReleaseChannel = null, Uri? ConsoleAddress = null);

/// <summary>
/// Alerts through the delivery service (plan 22a GP07): one message per alert. The body names the check, the dataset, the measure and the sample keys (the keys of the rows that failed, as the evaluator took them), and never
/// a secret: an <c>error</c>'s message is the failure's class, never a data value or a connection string.
/// </summary>
public sealed class DeliveryQualityAlerts(DeliveryService delivery, QualityAlertSettings settings) : IQualityAlerts
{
    public async Task SendAsync(QualityAlert alert, CancellationToken ct = default)
    {
        var recipients = (settings.Recipients ?? []).Concat(alert.Owners).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var (subject, text) = Describe(alert);
        var link = settings.ConsoleAddress is { } console ? new Uri(console, "quality").ToString() : null;
        await delivery.SendAsync(new DeliveryRequest(settings.Channel, subject, recipients, Text: text, Link: link, Source: "alert:check", ReleaseChannel: settings.ReleaseChannel), ct).ConfigureAwait(false);
    }

    /// <summary>The subject and the text of an alert.</summary>
    public static (string Subject, string Text) Describe(QualityAlert alert)
    {
        var text = new StringBuilder();
        text.Append("Dataset: ").AppendLine(alert.Dataset);
        text.Append("Check: ").Append(alert.CheckId).Append(" (").Append(alert.CheckKind is null ? "" : alert.CheckKind + ", ").Append(alert.Severity).AppendLine(")");
        if (alert.Source is not null) text.Append("Evaluated by: ").AppendLine(alert.Source);
        string subject;
        if (alert.Kind == QualityAlertKind.Recovered)
        {
            subject = $"Recovered: {alert.CheckId} on {alert.Dataset}";
            text.AppendLine("Status: passing again.");
        }
        else if (alert.Status == CheckOutcome.Error)
        {
            subject = $"Could not check: {alert.CheckId} on {alert.Dataset}";
            text.Append("Status: the check could not run. ").AppendLine(alert.Message ?? "");
            text.AppendLine(alert.OnError == "block" ? "A check that cannot run blocks what depends on the dataset (on_error: block)." : "It does not block (on_error: alert).");
        }
        else
        {
            subject = $"{(alert.Blocking ? "Blocked" : "Failed")}: {alert.CheckId} on {alert.Dataset}";
            text.Append("Status: failed");
            if (alert.Measured is { } measured) text.Append(CultureInfo.InvariantCulture, $", measured {measured:0.##}");
            if (alert.Threshold is { } threshold) text.Append(CultureInfo.InvariantCulture, $", allowed {threshold:0.##}");
            text.AppendLine(".");
            if (alert.SampleKeys is { Count: > 0 } keys) text.Append("Sample keys: ").AppendLine(string.Join("; ", keys));
            text.AppendLine(alert.Blocking ? "What depends on the dataset waits until the check passes." : "A warning: nothing is stopped.");
        }

        return (subject, text.ToString().TrimEnd());
    }

    /// <summary>
    /// Whether a check's new status makes an alert, given the status it was last alerted at (plan 23a FQ13): one when it turned unhealthy (a fail or an error after a pass, or after nothing), one when it passes after it was not passing, none otherwise; a fail that
    /// becomes an error is still the failure that was alerted, an error that becomes a fail is a new thing to say. Null: no alert.
    /// </summary>
    public static QualityAlertKind? Transition(CheckOutcome? previous, CheckOutcome now)
    {
        if (now == CheckOutcome.Pass) return previous is { } p && p != CheckOutcome.Pass ? QualityAlertKind.Recovered : null;
        if (previous == now) return null;
        if (previous == CheckOutcome.Fail && now == CheckOutcome.Error) return null;
        return QualityAlertKind.Failed;
    }
}
