using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DbDataKit.Monitoring;
using DbDataKit.Scheduling;
using DbDataKit.State;

namespace DbDataKit.Runner.Scheduler;

/// <summary>
/// The text of a <c>task_projection</c> row's <c>schedule</c> and <c>limits</c> columns (plan 28a DR11): small JSON documents with the manifest's own key names and duration spelling (<c>15m</c>, <c>30s</c>), so what the loop reads is what the
/// manifest says. The scheduler reads them with <see cref="ParseSchedule"/> and <see cref="ParseLimits"/> and never touches a manifest; <see cref="RowsOf"/> builds the rows from one at activation (R17 calls it).
/// <list type="bullet">
/// <item><c>schedule</c>: <c>{"cron":"0 2 * * *","time_zone":"Europe/Berlin","misfire":"skip","misfire_grace":"5m"}</c> or <c>{"every":"15m"}</c>; absent or null: no schedule.</item>
/// <item><c>limits</c>: <c>{"timeout":"30m","memory_mb":512,"cpu":1.5,"concurrency":2,"attempts":3,"backoff":"30s","max_backoff":"10m","allow_overlap":false}</c>. <c>allow_overlap</c> is not a limit, but the projection has no other column for it.</item>
/// </list>
/// A text that cannot be read throws a <see cref="FormatException"/> that names the key; the scheduler skips that row and reports the message (DR11).
/// </summary>
public static partial class ProjectionDocuments
{
    [GeneratedRegex(@"^(?<n>\d+(\.\d+)?)(?<unit>ms|s|m|h|d)$", RegexOptions.CultureInvariant)]
    private static partial Regex DurationRule();

    private static readonly JsonWriterOptions Compact = new() { Indented = false };

    // ---- schedule

    public static string? ScheduleText(Schedule schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        if (!schedule.HasSchedule) return null;
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, Compact))
        {
            w.WriteStartObject();
            if (schedule.Cron is { } cron)
            {
                w.WriteString("cron", cron);
                w.WriteString("time_zone", schedule.TimeZoneId);
            }
            else if (schedule.Every is { } every) w.WriteString("every", DurationText(every));

            w.WriteString("misfire", schedule.Misfire == MisfirePolicy.Skip ? "skip" : "run_once");
            w.WriteString("misfire_grace", DurationText(schedule.MisfireGrace));
            w.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The schedule a row's text says; the default (no schedule) for null or empty. Throws <see cref="FormatException"/> for text that is not a schedule, and for a schedule that does not validate.</summary>
    public static Schedule ParseSchedule(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Trim() == "null") return new Schedule();
        using var doc = Open(text, "schedule");
        var map = Object(doc, "schedule");
        var allowed = new[] { "cron", "every", "time_zone", "misfire", "misfire_grace" };
        Keys(map, "schedule", allowed);
        var defaults = new Schedule();
        var schedule = new Schedule
        {
            Cron = Text(map, "cron", "schedule"),
            Every = Duration(map, "every", "schedule"),
            TimeZoneId = Text(map, "time_zone", "schedule") ?? defaults.TimeZoneId,
            Misfire = Text(map, "misfire", "schedule") switch
            {
                null or "run_once" => MisfirePolicy.RunOnce,
                "skip" => MisfirePolicy.Skip,
                var other => throw new FormatException($"schedule.misfire is '{other}': write run_once or skip."),
            },
            MisfireGrace = Duration(map, "misfire_grace", "schedule") ?? defaults.MisfireGrace,
        };
        if (schedule.Validate() is { } problem) throw new FormatException($"schedule: {problem}");
        return schedule;
    }

    // ---- limits

    public static string? LimitsText(TaskLimits limits, bool allowOverlap = false)
    {
        ArgumentNullException.ThrowIfNull(limits);
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, Compact))
        {
            w.WriteStartObject();
            if (limits.Timeout is { } timeout) w.WriteString("timeout", DurationText(timeout));
            if (limits.MemoryMb is { } memory) w.WriteNumber("memory_mb", memory);
            if (limits.Cpu is { } cpu) w.WriteNumber("cpu", cpu);
            if (limits.Concurrency is { } concurrency) w.WriteNumber("concurrency", concurrency);
            if (limits.Attempts is { } attempts) w.WriteNumber("attempts", attempts);
            if (limits.Backoff is { } backoff) w.WriteString("backoff", DurationText(backoff));
            if (limits.MaxBackoff is { } max) w.WriteString("max_backoff", DurationText(max));
            if (allowOverlap) w.WriteBoolean("allow_overlap", true);
            w.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The limits and the overlap flag a row's text says; none for null or empty. Every number written must be above zero, as in the manifest.</summary>
    public static (TaskLimits Limits, bool AllowOverlap) ParseLimits(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Trim() == "null") return (TaskLimits.None, false);
        using var doc = Open(text, "limits");
        var map = Object(doc, "limits");
        Keys(map, "limits", ["timeout", "memory_mb", "cpu", "concurrency", "attempts", "backoff", "max_backoff", "allow_overlap"]);
        var limits = new TaskLimits(
            Duration(map, "timeout", "limits"), Whole(map, "memory_mb", "limits"), Number(map, "cpu", "limits"), Whole(map, "concurrency", "limits"),
            Whole(map, "attempts", "limits"), Duration(map, "backoff", "limits"), Duration(map, "max_backoff", "limits"));
        var overlap = false;
        if (map.TryGetProperty("allow_overlap", out var flag))
            overlap = flag.ValueKind is JsonValueKind.True or JsonValueKind.False ? flag.GetBoolean() : throw new FormatException("limits.allow_overlap is true or false.");
        return (limits, overlap);
    }

    // ---- triggers (R13)

    private static readonly (string Word, RunOutcome Outcome)[] OutcomeWords =
    [
        ("succeeded", RunOutcome.Succeeded), ("completed_with_failures", RunOutcome.CompletedWithFailures), ("failed", RunOutcome.Failed), ("stopped", RunOutcome.Stopped), ("interrupted", RunOutcome.Interrupted),
    ];

    /// <summary>
    /// The text of a row's <c>triggers</c> column: a JSON array with one object per trigger, the kind as its only key and the manifest's own key names inside:
    /// <c>[{"after":{"task":"extract","package":"other","on":["failed"]}},{"webhook":{"source":"shop","secret":"shop_hook"}},{"file":{"connection":"inbox","path":"in/*.csv","poll":"1m"}},{"store_change":{"model":"orders"}},{"manual":true}]</c>.
    /// Null for a task with no trigger. <c>on</c> is the outcomes (<c>any</c> for every one); a missing <c>on</c> means succeeded only.
    /// </summary>
    public static string? TriggersText(IReadOnlyList<TaskTrigger> triggers)
    {
        ArgumentNullException.ThrowIfNull(triggers);
        if (triggers.Count == 0) return null;
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, Compact))
        {
            w.WriteStartArray();
            foreach (var t in triggers)
            {
                w.WriteStartObject();
                switch (t.Kind)
                {
                    case TriggerKind.AfterRun:
                        w.WriteStartObject("after");
                        w.WriteString("task", t.Task);
                        if (t.Package is not null) w.WriteString("package", t.Package);
                        if (t.On is { Count: > 0 } on)
                        {
                            w.WriteStartArray("on");
                            if (OutcomeWords.All(o => on.Contains(o.Outcome))) w.WriteStringValue("any");
                            else foreach (var outcome in on) w.WriteStringValue(OutcomeWords.First(o => o.Outcome == outcome).Word);
                            w.WriteEndArray();
                        }

                        w.WriteEndObject();
                        break;
                    case TriggerKind.Webhook:
                        w.WriteStartObject("webhook");
                        w.WriteString("source", t.Source);
                        if (t.Secret is not null) w.WriteString("secret", t.Secret);
                        w.WriteEndObject();
                        break;
                    case TriggerKind.File:
                        w.WriteStartObject("file");
                        w.WriteString("connection", t.Connection);
                        w.WriteString("path", t.Path);
                        if (t.Poll is { } poll) w.WriteString("poll", DurationText(poll));
                        w.WriteEndObject();
                        break;
                    case TriggerKind.StoreChange:
                        w.WriteStartObject("store_change");
                        w.WriteString("model", t.Model);
                        if (t.Entity is not null) w.WriteString("entity", t.Entity);
                        w.WriteEndObject();
                        break;
                    default:
                        w.WriteBoolean("manual", true);
                        break;
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The triggers a row's text says; none for null or empty. Throws <see cref="FormatException"/> naming the key for text that is not triggers.</summary>
    public static IReadOnlyList<TaskTrigger> ParseTriggers(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Trim() == "null") return [];
        using var doc = Open(text, "triggers");
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new FormatException("triggers is a JSON array, like [{\"manual\":true}].");
        var list = new List<TaskTrigger>();
        var index = 0;
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var at = $"triggers[{index++}]";
            if (item.ValueKind != JsonValueKind.Object) throw new FormatException($"{at} is an object with one key, such as {{\"manual\":true}}.");
            var properties = item.EnumerateObject().ToList();
            if (properties.Count != 1) throw new FormatException($"{at} has exactly one key: after, webhook, file, store_change or manual.");
            var (kind, body) = (properties[0].Name, properties[0].Value);
            if (kind == "manual")
            {
                list.Add(TaskTrigger.ByHand());
                continue;
            }

            if (body.ValueKind != JsonValueKind.Object) throw new FormatException($"{at}.{kind} is an object.");
            string Required(string key, string name) => Text(body, key, name) ?? throw new FormatException($"{name}.{key} is missing.");
            switch (kind)
            {
                case "after":
                    Keys(body, $"{at}.after", ["task", "package", "on"]);
                    list.Add(new TaskTrigger(TriggerKind.AfterRun, Task: Required("task", $"{at}.after"), Package: Text(body, "package", $"{at}.after"), On: Outcomes(body, $"{at}.after")));
                    break;
                case "webhook":
                    Keys(body, $"{at}.webhook", ["source", "secret"]);
                    list.Add(new TaskTrigger(TriggerKind.Webhook, Source: Required("source", $"{at}.webhook"), Secret: Text(body, "secret", $"{at}.webhook")));
                    break;
                case "file":
                    Keys(body, $"{at}.file", ["connection", "path", "poll"]);
                    list.Add(new TaskTrigger(TriggerKind.File, Connection: Required("connection", $"{at}.file"), Path: Required("path", $"{at}.file"), Poll: Duration(body, "poll", $"{at}.file")));
                    break;
                case "store_change":
                    Keys(body, $"{at}.store_change", ["model", "entity"]);
                    list.Add(new TaskTrigger(TriggerKind.StoreChange, Model: Required("model", $"{at}.store_change"), Entity: Text(body, "entity", $"{at}.store_change")));
                    break;
                default:
                    throw new FormatException($"{at}.{kind} is not a trigger; the kinds are after, webhook, file, store_change and manual.");
            }
        }

        return list;
    }

    private static List<RunOutcome>? Outcomes(JsonElement body, string name)
    {
        if (!body.TryGetProperty("on", out var on) || on.ValueKind == JsonValueKind.Null) return null;
        if (on.ValueKind != JsonValueKind.Array) throw new FormatException($"{name}.on is a list of outcomes.");
        var found = new List<RunOutcome>();
        foreach (var word in on.EnumerateArray())
        {
            var text = word.ValueKind == JsonValueKind.String ? word.GetString() : null;
            if (text == "any")
            {
                foreach (var every in OutcomeWords) if (!found.Contains(every.Outcome)) found.Add(every.Outcome);
                continue;
            }

            var hit = OutcomeWords.FirstOrDefault(o => o.Word == text);
            if (hit.Word is null) throw new FormatException($"{name}.on has '{text}': the outcomes are {string.Join(", ", OutcomeWords.Select(o => o.Word))} and any.");
            if (!found.Contains(hit.Outcome)) found.Add(hit.Outcome);
        }

        return found;
    }

    /// <summary>
    /// The first trigger loop among <paramref name="rows"/> as they would be once <paramref name="package"/> on <paramref name="channel"/> is replaced by <paramref name="replacement"/> (null: removed): the rows of every other package of
    /// the channel as stored, the new rows in place of the package's own. A row whose triggers cannot be read is left out of the check (the scheduler reports it). Null when there is no loop.
    /// </summary>
    public static TriggerCycle? FindCycle(IEnumerable<TaskProjection> rows, string package, string channel, IReadOnlyList<TaskProjectionRow>? replacement)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var all = new List<ProjectedTriggers>();
        foreach (var row in rows.Where(r => r.Channel == channel && r.Package != package))
            if (TryTriggers(row.Triggers) is { } triggers) all.Add(new ProjectedTriggers(row.Package, row.Channel, row.Task, triggers));
        foreach (var row in replacement ?? [])
            if (TryTriggers(row.Triggers) is { } triggers) all.Add(new ProjectedTriggers(package, channel, row.Task, triggers));
        return TriggerGraph.FindCycle(all);
    }

    /// <summary>
    /// Refuses a replacement of a package's rows that would make tasks start each other for ever: throws <see cref="TriggerCycleException"/> naming the loop. Call it with the rows read from the projection (<c>TaskProjections.ListByChannel</c>)
    /// and the rows about to replace the package's, before <c>TaskProjections.Replace</c>; the publish path does (plan 28a R13).
    /// </summary>
    public static void RequireNoCycle(IEnumerable<TaskProjection> rows, string package, string channel, IReadOnlyList<TaskProjectionRow>? replacement)
    {
        if (FindCycle(rows, package, channel, replacement) is { } cycle) throw new TriggerCycleException(cycle);
    }

    private static IReadOnlyList<TaskTrigger>? TryTriggers(string? text)
    {
        try { return ParseTriggers(text); }
        catch (FormatException) { return null; }
    }

    // ---- rows

    /// <summary>The projection rows of a manifest's tasks at <paramref name="version"/>: schedule, triggers and limits as the documents above, the required labels and no progress hash yet (R17).</summary>
    public static IReadOnlyList<TaskProjectionRow> RowsOf(PackageManifest manifest, string version)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrEmpty(version);
        return [.. manifest.Tasks.Select(t => new TaskProjectionRow(t.Name, version, ScheduleText(t.Schedule), TriggersText(t.Triggers), LimitsText(t.Limits, t.AllowOverlap), t.Requires, null))];
    }

    // ---- durations

    /// <summary>A duration the way the manifest writes it: the largest unit that makes a whole number (<c>90s</c> is <c>90s</c>, 120 seconds is <c>2m</c>).</summary>
    public static string DurationText(TimeSpan span)
    {
        var ms = (long)Math.Round(span.TotalMilliseconds);
        if (ms % 1000 != 0) return ms.ToString(CultureInfo.InvariantCulture) + "ms";
        var s = ms / 1000;
        if (s % 86400 == 0 && s != 0) return (s / 86400).ToString(CultureInfo.InvariantCulture) + "d";
        if (s % 3600 == 0 && s != 0) return (s / 3600).ToString(CultureInfo.InvariantCulture) + "h";
        if (s % 60 == 0 && s != 0) return (s / 60).ToString(CultureInfo.InvariantCulture) + "m";
        return s.ToString(CultureInfo.InvariantCulture) + "s";
    }

    private static JsonDocument Open(string text, string name)
    {
        try { return JsonDocument.Parse(text); }
        catch (JsonException ex) { throw new FormatException($"{name} is not JSON: {ex.Message}"); }
    }

    private static JsonElement Object(JsonDocument doc, string name) =>
        doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement : throw new FormatException($"{name} is a JSON object, like {{\"every\":\"15m\"}}.");

    private static void Keys(JsonElement map, string name, string[] allowed)
    {
        foreach (var property in map.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
                throw new FormatException($"{name}.{property.Name} is not a key of {name}; the keys are {string.Join(", ", allowed)}.");
    }

    private static string? Text(JsonElement map, string key, string name)
    {
        if (!map.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : throw new FormatException($"{name}.{key} is text.");
    }

    private static TimeSpan? Duration(JsonElement map, string key, string name)
    {
        if (Text(map, key, name) is not { } text) return null;
        if (DurationRule().Match(text) is not { Success: true } m) throw new FormatException($"{name}.{key} is '{text}': write a duration such as 500ms, 30s, 15m, 2h or 1d.");
        var n = double.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
        var span = m.Groups["unit"].Value switch
        {
            "ms" => TimeSpan.FromMilliseconds(n),
            "s" => TimeSpan.FromSeconds(n),
            "m" => TimeSpan.FromMinutes(n),
            "h" => TimeSpan.FromHours(n),
            _ => TimeSpan.FromDays(n),
        };
        // a misfire grace of zero is allowed (the schedule's own rule); every other duration is above zero
        return span == TimeSpan.Zero && key is not "misfire_grace" and not "every" ? throw new FormatException($"{name}.{key} is above zero.") : span;
    }

    private static int? Whole(JsonElement map, string key, string name)
    {
        if (!map.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var n)) throw new FormatException($"{name}.{key} is a whole number.");
        return n > 0 ? n : throw new FormatException($"{name}.{key} is above zero.");
    }

    private static decimal? Number(JsonElement map, string key, string name)
    {
        if (!map.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var n)) throw new FormatException($"{name}.{key} is a number.");
        return n > 0 ? n : throw new FormatException($"{name}.{key} is above zero.");
    }
}
