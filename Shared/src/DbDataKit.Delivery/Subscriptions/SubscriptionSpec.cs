using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DbDataKit.Scheduling;
using DbDataKit.State;

namespace DbDataKit.Delivery;

/// <summary>A subscription request is refused: the message names the source and the key.</summary>
public sealed class SubscriptionSpecException(string message) : Exception(message);

/// <summary>
/// What a person asks for when they subscribe (plan 22a GP17): read <b>strictly</b> from the JSON of a request (an unknown key, a value of the wrong kind or a schedule that cannot be read fails naming the source and the key).
/// <code>
/// { "name": "Weekly sales", "kind": "dashboard", "view": "sales",
///   "parameters": { "region": ["east"], "year": 2026 },
///   "layout": "print",                  // optional: the layout the document names; none: the export rule of the layout selection
///   "format": "pdf",                    // pdf (default) | csv | xlsx
///   "widget": "orders-table",           // required for csv (one widget's rows); optional for xlsx; not for pdf
///   "channel": "mail",                  // a channel of the delivery: section
///   "recipient": "ana@example.com",     // optional: the subscriber's own address when left out
///   "schedule": { "cron": "0 7 * * 1", "time_zone": "Europe/Berlin" } }
/// </code>
/// A schedule is at most hourly (no occurrence within an hour of the next): a person's mailbox is not a monitoring system. The parameters are the document's own, by name, scalars or lists of scalars; the rendering checks them against the document.
/// </summary>
public sealed record SubscriptionSpec(SubscriptionFields Fields)
{
    public static IReadOnlyList<string> Kinds { get; } = ["dashboard", "report"];

    public static IReadOnlyList<string> Formats { get; } = ["pdf", "csv", "xlsx"];

    private static readonly string[] Keys = ["name", "kind", "view", "parameters", "layout", "format", "widget", "channel", "recipient", "schedule"];
    private static readonly string[] ScheduleKeys = ["cron", "time_zone"];
    private static readonly Regex Identifier = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant);

    /// <summary>The shortest time allowed between two occurrences of a schedule.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromHours(1);

    public static SubscriptionSpec Read(JsonObject json, string source)
    {
        ArgumentNullException.ThrowIfNull(json);
        foreach (var (key, _) in json)
            if (!Keys.Contains(key)) throw new SubscriptionSpecException($"{source}: a subscription has no key `{key}`. Keys: {string.Join(", ", Keys)}.");
        var name = Required(json, "name", source);
        if (name.Length > 120 || name.Any(char.IsControl)) throw new SubscriptionSpecException($"{source}: name is a line of at most 120 characters.");
        var kind = Required(json, "kind", source);
        if (!Kinds.Contains(kind)) throw new SubscriptionSpecException($"{source}: kind is `dashboard` or `report`, not `{kind}`.");
        var view = Ident(Required(json, "view", source), "view", source);
        var layout = Optional(json, "layout", source) is { } l ? Ident(l, "layout", source) : null;
        var format = Optional(json, "format", source) ?? "pdf";
        if (!Formats.Contains(format)) throw new SubscriptionSpecException($"{source}: format is `pdf`, `csv` or `xlsx`, not `{format}`.");
        var widget = Optional(json, "widget", source) is { } w ? Ident(w, "widget", source) : null;
        if (format == "csv" && widget is null) throw new SubscriptionSpecException($"{source}: format `csv` is one widget's rows: name it with `widget`.");
        if (format == "pdf" && widget is not null) throw new SubscriptionSpecException($"{source}: `widget` is for `csv` and `xlsx`; a PDF is the whole layout.");
        if (layout is not null && format != "pdf") throw new SubscriptionSpecException($"{source}: `layout` is for a PDF; `csv` and `xlsx` are the widgets' rows.");
        var channel = Ident(Required(json, "channel", source), "channel", source);
        var recipient = Optional(json, "recipient", source);
        if (recipient is not null && (recipient.Length > 254 || recipient.Contains(' ', StringComparison.Ordinal) || recipient.Count(c => c == '@') != 1 || recipient.StartsWith('@') || recipient.EndsWith('@')))
            throw new SubscriptionSpecException($"{source}: recipient is an email address.");

        JsonObject? parameters = null;
        if (json["parameters"] is { } p)
        {
            if (p is not JsonObject po) throw new SubscriptionSpecException($"{source}: parameters is a mapping of the document's parameters by name.");
            if (po.Count > 50) throw new SubscriptionSpecException($"{source}: parameters holds at most 50 values.");
            foreach (var (key, value) in po)
                if (!Scalar(value) && !(value is JsonArray a && a.Count <= 500 && a.All(Scalar)))
                    throw new SubscriptionSpecException($"{source}: parameters.{key} is a value or a list of values (text, number, true or false).");
            parameters = (JsonObject)po.DeepClone();
        }

        if (json["schedule"] is not JsonObject schedule) throw new SubscriptionSpecException($"{source}: schedule is required: {{ cron: \"0 7 * * 1\", time_zone: \"Europe/Berlin\" }}.");
        foreach (var (key, _) in schedule)
            if (!ScheduleKeys.Contains(key)) throw new SubscriptionSpecException($"{source}: schedule has no key `{key}`. Keys: {string.Join(", ", ScheduleKeys)}.");
        var cron = Required(schedule, "cron", source, "schedule.");
        var zone = Optional(schedule, "time_zone", source, "schedule.") ?? "UTC";
        if (Schedule.OnCron(cron, zone).Validate() is { } problem) throw new SubscriptionSpecException($"{source}: schedule: {problem}");
        if (Gap(cron, zone) is { } gap && gap < MinInterval)
            throw new SubscriptionSpecException($"{source}: schedule `{cron}` runs every {(gap.TotalMinutes < 1 ? "minute" : $"{gap.TotalMinutes:0} minutes")}; a subscription is at most hourly.");
        return new SubscriptionSpec(new SubscriptionFields(name, kind, view, parameters, layout, format, widget, channel, recipient, cron, zone));
    }

    /// <summary>The smallest gap between the next occurrences of a schedule (a month ahead), or null when it has fewer than two.</summary>
    private static TimeSpan? Gap(string cron, string zone)
    {
        var schedule = Schedule.OnCron(cron, zone);
        var at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        TimeSpan? smallest = null;
        var previous = ScheduleEvaluator.NextAfter(schedule, at);
        for (var i = 0; i < 400 && previous is not null; i++)
        {
            var next = ScheduleEvaluator.NextAfter(schedule, previous.Value);
            if (next is null) break;
            var gap = next.Value - previous.Value;
            if (smallest is null || gap < smallest) smallest = gap;
            previous = next;
        }

        return smallest;
    }

    /// <summary>The first time a schedule gives after <paramref name="after"/>: the subscription's next run.</summary>
    public static DateTimeOffset? Next(string cron, string timeZone, DateTimeOffset after) => ScheduleEvaluator.NextAfter(Schedule.OnCron(cron, timeZone), after);

    private static bool Scalar(JsonNode? node) => node is null || (node is JsonValue v && (v.TryGetValue<string>(out var s) ? s.Length <= 1000 : v.TryGetValue<double>(out _) || v.TryGetValue<bool>(out _)));

    private static string Ident(string value, string key, string source) =>
        Identifier.IsMatch(value) ? value : throw new SubscriptionSpecException($"{source}: {key} `{value}` is not a name (letters, digits, dot, dash and underscore, at most 128).");

    private static string Required(JsonObject o, string key, string source, string prefix = "") =>
        Optional(o, key, source, prefix) ?? throw new SubscriptionSpecException($"{source}: {prefix}{key} is required.");

    private static string? Optional(JsonObject o, string key, string source, string prefix = "") =>
        o[key] switch
        {
            null => null,
            JsonValue v when v.TryGetValue<string>(out var s) && s.Length > 0 => s,
            _ => throw new SubscriptionSpecException($"{source}: {prefix}{key} must be a non-empty text."),
        };
}
