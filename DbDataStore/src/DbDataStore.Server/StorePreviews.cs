using DbDataKit.Config.Yaml;
using DbDataKit.DataModel.Storage;
using DbDataKit.Monitoring;
using DbDataKit.Store;
using Microsoft.Extensions.Hosting;

namespace DbDataStore.Server;

/// <summary>
/// The <c>previews:</c> section of <c>server.yaml</c> (plan 21 CH07), read strictly: <c>idle_days</c> (a draft with no write for that many days is removed, 30 by default) and <c>notice_days</c> (the notice comes that many
/// days before, 7 by default). An unknown key or a value that is not a whole number of days fails naming the file and the nearest key.
/// </summary>
public sealed record PreviewSettings(int IdleDays = DraftExpiry.DefaultIdleDays, int NoticeDays = DraftExpiry.DefaultNoticeDays)
{
    public const string Section = "previews";
    private static readonly string[] Keys = ["idle_days", "notice_days"];

    /// <summary>The settings in <paramref name="path"/>; the defaults when the file or the section is absent.</summary>
    public static PreviewSettings Load(string path)
    {
        if (!File.Exists(path)) return new PreviewSettings();
        var diagnostics = new List<ConfigDiagnostic>();
        var root = StrictYamlReader.Read(File.ReadAllText(path), path, diagnostics) as YamlMapping;
        if (diagnostics.FirstOrDefault(d => d.Severity == ConfigSeverity.Error) is { } error)
            throw new InvalidOperationException($"{path}: the file is not valid YAML ({error.Message}, line {error.Line}).");
        if (root?.Get(Section) is not { } node || node is YamlScalar { Value.Length: 0 }) return new PreviewSettings();
        if (node is not YamlMapping map) throw new InvalidOperationException($"{path}: `{Section}` must be a set of keys ({string.Join(", ", Keys)}).");
        foreach (var entry in map.Entries.Where(e => !Keys.Contains(e.Key.Value)))
            throw new InvalidOperationException($"{path}: unknown key `{Section}.{entry.Key.Value}`." + (DidYouMean.Closest(entry.Key.Value, Keys) is { } near ? $" Did you mean `{near}`?" : "") + $" Keys: {string.Join(", ", Keys)}.");
        var idle = Days(path, map, "idle_days", DraftExpiry.DefaultIdleDays, 1);
        var notice = Days(path, map, "notice_days", DraftExpiry.DefaultNoticeDays, 0);
        return new PreviewSettings(idle, notice);
    }

    private static int Days(string path, YamlMapping map, string key, int fallback, int minimum)
    {
        if (map.Get(key) is not { } node || node is YamlScalar { Value.Length: 0, Quoted: false }) return fallback;
        if (node is YamlScalar scalar && int.TryParse(scalar.Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var days) && days >= minimum) return days;
        throw new InvalidOperationException($"{path}: `{Section}.{key}` must be a whole number of days, at least {minimum}.");
    }
}

/// <summary>What one pass of the expiry did: the drafts it removed and the notices it sent.</summary>
public sealed record DraftSweepResult(int Removed, int Warned);

/// <summary>
/// The expiry of idle previews on this server (plan 21 CH07): walks every model's copies, removes the drafts idle for <see cref="PreviewSettings.IdleDays"/> and puts a notice in the notification feed for each draft that came within
/// <see cref="PreviewSettings.NoticeDays"/> of that since the previous pass (so a notice is sent once, not on every pass). The clock is a parameter, so a test injects the day.
/// </summary>
public static class StoreDraftSweep
{
    public const string WarningKind = "PreviewExpiring";
    public const string RemovedKind = "PreviewRemoved";

    public static async Task<DraftSweepResult> RunOnceAsync(string directory, StoreConnections connections, INotificationFeed feed, PreviewSettings settings, DateTimeOffset now, TimeSpan sincePrevious, CancellationToken cancellationToken = default)
    {
        int removed = 0, warned = 0;
        foreach (var name in StoreModels.Names(directory))
        {
            var loaded = StoreModels.Check(name, await File.ReadAllTextAsync(StoreModels.FileOf(directory, name), cancellationToken));
            if (loaded.Model is not { } model) continue;
            var copies = new List<string?> { null };
            copies.AddRange(StoreInstances.List(directory, name).Select(i => i.Name).Where(n => n != StoreInstances.Default));
            foreach (var copy in copies)
            {
                var (connectionName, schema, instance, problem) = StoreInstances.Resolve(directory, model, copy);
                if (problem is not null || connectionName is null) continue;
                try
                {
                    await using var connection = await connections.OpenAsync(connectionName, cancellationToken);
                    var writer = new ModelWriter(model, connection, connections.EngineOf(connectionName), schema: schema, instance: instance);
                    var drafts = await writer.DraftsAsync(cancellationToken);
                    var plan = DraftExpiry.Plan(drafts, now, settings.IdleDays, settings.NoticeDays);
                    foreach (var draft in plan.Warn)
                    {
                        var noticeAt = draft.LastAt.AddDays(settings.IdleDays - Math.Min(settings.NoticeDays, settings.IdleDays - 1));
                        if (noticeAt <= now - sincePrevious) continue;
                        feed.Raise(WarningKind, $"The preview '{draft.Id}' of the model '{name}' has had no write since {draft.LastAt:yyyy-MM-dd} and is removed with its draft on {draft.LastAt.AddDays(settings.IdleDays):yyyy-MM-dd}; write to it to keep it.", mappingName: name);
                        warned++;
                    }
                    if (plan.Remove.Count > 0)
                    {
                        await writer.DiscardDraftsAsync(plan.Remove.Select(d => d.Id), cancellationToken);
                        foreach (var draft in plan.Remove)
                        {
                            feed.Raise(RemovedKind, $"The preview '{draft.Id}' of the model '{name}' was removed with its draft: it had no write for {settings.IdleDays} days.", mappingName: name);
                            removed++;
                        }
                    }
                }
                catch (Exception ex) when (ex is StoreConnectionException or StorageException) { /* a copy that cannot be reached is tried again at the next pass */ }
            }
        }
        return new DraftSweepResult(removed, warned);
    }
}

/// <summary>Runs <see cref="StoreDraftSweep"/> an hour after start and every hour after, with the settings as <c>server.yaml</c> has them at start.</summary>
public sealed class StoreDraftSweeper(string directory, StoreConnections connections, INotificationFeed feed, PreviewSettings settings, Action<string> log) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(Interval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var result = await StoreDraftSweep.RunOnceAsync(directory, connections, feed, settings, DateTimeOffset.UtcNow, Interval, stoppingToken);
                    if (result.Removed + result.Warned > 0) log($"Preview expiry: {result.Removed} removed, {result.Warned} warned.");
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { log($"Preview expiry failed: {ex.Message}"); }
            }
        }
        catch (OperationCanceledException) { }
    }
}
