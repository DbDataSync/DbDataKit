using System.Text.Json.Nodes;
using DbDataKit.Config.Yaml;
using DbDataKit.Quality;
using DbDataKit.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DbDataKit.Server;

/// <summary>Where the console sends the alerts of failed checks (plan 23a FQ13): the delivery channel and who gets them. The section <c>quality: alerts:</c> of <c>settings.yaml</c>.</summary>
/// <param name="Channel">A delivery channel's name (the <c>delivery:</c> section and the bindings).</param>
/// <param name="Recipients">Addresses that get every alert (a webhook channel ignores them).</param>
/// <param name="ReleaseChannel">The release channel the messages are written in; null: production.</param>
/// <param name="EverySeconds">How often the servers are asked what their checks say.</param>
public sealed record ConsoleQualityAlertSettings(string Channel, IReadOnlyList<string> Recipients, string? ReleaseChannel, int EverySeconds)
{
    public const int DefaultEverySeconds = 60;
    public const int MinimumEverySeconds = 10;
}

/// <summary>
/// Reads the section <c>quality:</c> of the console's <c>settings.yaml</c> (plan 23a FQ13), strictly: an unknown key, a channel that is not text, a recipient that is not an address or an interval below ten seconds fails naming the file and the line. No section, or no
/// <c>alerts:</c> in it, means no alerts (the checks still run and show on the Quality page). A problem is shown, never thrown: the console keeps starting.
/// </summary>
public static class ConsoleQualitySettings
{
    public const string Section = "quality";

    public static (ConsoleQualityAlertSettings? Alerts, string? Problem) Load(string consoleDirectory)
    {
        var path = ConsoleState.SettingsFileIn(consoleDirectory);
        if (!File.Exists(path)) return (null, null);
        var diagnostics = new List<ConfigDiagnostic>();
        var root = StrictYamlReader.Read(File.ReadAllText(path), path, diagnostics);
        if (diagnostics.FirstOrDefault(d => d.Severity == ConfigSeverity.Error) is { } error) return (null, $"{path}: the file is not valid YAML ({error.Message}, line {error.Line}).");
        if (root is not YamlMapping mapping || mapping.GetEntry(Section) is not { } entry) return (null, null);
        if (entry.Value is YamlScalar { Value.Length: 0 }) return (null, null);
        if (entry.Value is not YamlMapping quality) return (null, $"{path}, line {entry.Key.Line}: quality is a set of settings (alerts).");
        foreach (var e in quality.Entries)
            if (e.Key.Value != "alerts") return (null, $"{path}, line {e.Key.Line}: '{e.Key.Value}' is not a setting of quality (it has alerts).");
        if (quality.GetEntry("alerts") is not { } alerts || alerts.Value is YamlScalar { Value.Length: 0 }) return (null, null);
        if (alerts.Value is not YamlMapping map) return (null, $"{path}, line {alerts.Key.Line}: alerts is a set of settings (channel, recipients, release_channel, every_seconds).");

        string? channel = null, release = null;
        var recipients = new List<string>();
        var every = ConsoleQualityAlertSettings.DefaultEverySeconds;
        foreach (var e in map.Entries)
        {
            var at = $"{path}, line {e.Key.Line}";
            switch (e.Key.Value)
            {
                case "channel":
                    if (e.Value is not YamlScalar { Value.Length: > 0 } c) return (null, $"{at}: channel is the name of a delivery channel.");
                    channel = c.Value;
                    break;
                case "release_channel":
                    if (e.Value is not YamlScalar { Value.Length: > 0 } r) return (null, $"{at}: release_channel is the name of a release channel (prod, test, dev).");
                    release = r.Value;
                    break;
                case "recipients":
                    if (e.Value is not YamlSequence items) return (null, $"{at}: recipients is a list of addresses.");
                    foreach (var item in items.Items)
                    {
                        if (item is not YamlScalar { Value: var address } || !address.Contains('@', StringComparison.Ordinal) || address.Any(char.IsWhiteSpace)) return (null, $"{at}: a recipient is an address (name@example.com).");
                        recipients.Add(address);
                    }

                    break;
                case "every_seconds":
                    if (e.Value is not YamlScalar { Value: var text } || !int.TryParse(text, out every) || every < ConsoleQualityAlertSettings.MinimumEverySeconds) return (null, $"{at}: every_seconds is a whole number of at least {ConsoleQualityAlertSettings.MinimumEverySeconds}.");
                    break;
                default:
                    return (null, $"{at}: '{e.Key.Value}' is not a setting of quality alerts (channel, recipients, release_channel, every_seconds).");
            }
        }

        return channel is null ? (null, $"{path}, line {alerts.Key.Line}: alerts needs a channel (the name of a delivery channel).") : (new ConsoleQualityAlertSettings(channel, recipients, release, every), null);
    }
}

/// <summary>What the console knows about its alerts of checks, for the setup checklist and the Quality page: configured or not, whether the channel exists, and the problem when the section could not be read.</summary>
public sealed record QualityAlertsState(bool Configured, string? Channel, bool ChannelKnown, string? Problem, IReadOnlyList<string> Recipients);

/// <summary>
/// The console's watch over the checks of the servers it manages (plan 23a FQ13): every <c>every_seconds</c> it reads the servers' overview (<see cref="ConsoleQuality"/>) and hands each check to <see cref="QualityAlertWatcher"/>, which alerts once per failure and once per recovery
/// through the console's delivery service. Settings are read again on every pass, so editing <c>settings.yaml</c> takes effect without a restart. A server that does not answer is not watched on that pass (what it said last stays remembered); a pass that fails is logged and the next one tries again.
/// </summary>
public sealed class ConsoleQualityAlerts(ConsoleSettings settings, ConsoleQuality quality, ConsoleDelivery delivery, StateDatabase state, ILogger<ConsoleQualityAlerts> log, TimeProvider? time = null) : BackgroundService
{
    public QualityAlertsState Current()
    {
        var (alerts, problem) = ConsoleQualitySettings.Load(settings.Directory);
        return new QualityAlertsState(alerts is not null, alerts?.Channel, alerts is not null && delivery.Channels.Names.Contains(alerts.Channel, StringComparer.OrdinalIgnoreCase), problem, alerts?.Recipients ?? []);
    }

    /// <summary>One pass: what the servers say now, alerted on. Null when alerts are not configured.</summary>
    public async Task<WatchReport?> PassAsync(CancellationToken ct)
    {
        var (alerts, problem) = ConsoleQualitySettings.Load(settings.Directory);
        if (alerts is null)
        {
            if (problem is not null) log.LogWarning("quality alerts: {Problem}", problem);
            return null;
        }

        var overview = await quality.OverviewAsync(ct).ConfigureAwait(false);
        var observed = new List<ObservedCheck>();
        var products = (overview["servers"]?.AsArray() ?? []).Where(x => x?["server_id"] is not null).ToDictionary(x => x!["server_id"]!.GetValue<string>(), x => x!["product"]?.GetValue<string>(), StringComparer.Ordinal);
        foreach (var dataset in overview["datasets"]?.AsArray() ?? [])
            foreach (var check in dataset?["checks"]?.AsArray() ?? [])
                if (dataset?["dataset"]?.GetValue<string>() is { } identity && check?["check_id"]?.GetValue<string>() is { } id && check["status"]?.GetValue<string>() is { } status
                    && Array.IndexOf(QualityModule.Statuses.ToArray(), status) is var at and >= 0)
                    observed.Add(new ObservedCheck(check["server_id"]?.GetValue<string>() ?? "", identity, id, check["severity"]?.GetValue<string>() ?? "warn", check["on_error"]?.GetValue<string>() ?? "alert", (CheckOutcome)at,
                        check["blocking"]?.GetValue<bool>() ?? false, check["measured"]?.GetValue<decimal>(), check["threshold"]?.GetValue<decimal>()));

        var sender = new DeliveryQualityAlerts(delivery.Service, new QualityAlertSettings(alerts.Channel, alerts.Recipients, alerts.ReleaseChannel));
        var watcher = new QualityAlertWatcher(new QualityStore(state), sender, (check, token) => quality.SampleKeysAsync(check.Evaluator, products.GetValueOrDefault(check.Evaluator), check.Dataset, check.CheckId, token), time);
        var report = await watcher.ObserveAsync(observed, ct).ConfigureAwait(false);
        foreach (var p in report.Problems) log.LogWarning("quality alerts: {Problem}", p);
        return report;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var clock = time ?? TimeProvider.System;
        while (!stoppingToken.IsCancellationRequested)
        {
            var every = ConsoleQualityAlertSettings.DefaultEverySeconds;
            try
            {
                var (alerts, _) = ConsoleQualitySettings.Load(settings.Directory);
                every = alerts?.EverySeconds ?? every;
                await PassAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                log.LogWarning("quality alerts: a pass failed: {Type}", ex.GetType().Name);
            }

            try { await Task.Delay(TimeSpan.FromSeconds(every), clock, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }
}
