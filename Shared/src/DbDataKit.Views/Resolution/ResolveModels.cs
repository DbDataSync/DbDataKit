using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Serving;

namespace DbDataKit.Views;

/// <summary>The codes of the resolution (DBR04), beside <see cref="ViewsCodes"/>: what a widget's error entry says when the failure is the resolver's own. A failure of the serving core keeps the core's code (<c>denied</c>, <c>timeout</c>, <c>unknown_member</c>...).</summary>
public static class ResolveCodes
{
    /// <summary>A parameter the widget uses is required and has no value and no default.</summary>
    public const string RequiredParameter = "DBD-VIEW-050";
    /// <summary>A value given for a parameter does not fit its type, or names a parameter the document does not declare.</summary>
    public const string BadParameterValue = "DBD-VIEW-051";
    /// <summary>A query form (or a part of one) this resolver does not run: the host must supply what it needs (a saved query's model, the Store's filtered read, a held result).</summary>
    public const string NotRunHere = "DBD-VIEW-052";
    /// <summary>The widget's model has no catalog here, so its request cannot be derived.</summary>
    public const string NoCatalog = "DBD-VIEW-053";
    /// <summary>An answer or an image over a limit.</summary>
    public const string TooLarge = "DBD-VIEW-054";
    /// <summary>The layout places more widgets than the resolver runs at once; the rest have this entry.</summary>
    public const string TooManyWidgets = "DBD-VIEW-055";
    /// <summary>The answer does not hold what the widget draws (a field is not a column of it).</summary>
    public const string AnswerMismatch = "DBD-VIEW-056";
    /// <summary>The layout asked for is not one of the document's.</summary>
    public const string UnknownLayout = "DBD-VIEW-057";
    /// <summary>The bytes of an image the document holds by hash were not found.</summary>
    public const string ImageNotFound = "DBD-VIEW-058";
}

/// <summary>
/// A viewer's sort of one table, asked of the server (plan 32 DBR45): the table's column (<see cref="By"/>, a catalog name the table shows) and the direction. It replaces the query's own <c>order</c> for that one run and is never stored;
/// the answer is the table's rows as the server orders all of them, which is what a click on a header means when the row limit cut the result.
/// </summary>
public sealed record WidgetOrder(string By, bool Descending);

/// <summary>A resolution that cannot start at all (a layout that does not exist, a parameter value that is not one): the caller's mistake, not one widget's.</summary>
public sealed class ViewResolutionException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>What a resolver is allowed to do, and what it needs from its host.</summary>
public sealed class ResolverOptions
{
    /// <summary>Widgets a resolution runs; the ones past it get an error entry. A layout places few (a dashboard fits one page).</summary>
    public int MaxWidgets { get; init; } = 200;

    /// <summary>Rows a widget's answer may hold (the table limit of plan 32 section 4.6); an answer over it is an error entry, never a silently cut table. Also asked of the core as the request's own limit.</summary>
    public long MaxRows { get; init; } = 100_000;

    /// <summary>The locale numbers and dates are written in when neither the request nor the document says one (plan 32 DBR46; the server's default). Null is English.</summary>
    public string? Locale { get; init; }

    /// <summary>Widgets run at once.</summary>
    public int MaxParallelism { get; init; } = 4;

    /// <summary>How long one widget may take, counted on <see cref="Time"/> (a test advances it; nothing waits). Also asked of the core as the request's own timeout.</summary>
    public TimeSpan WidgetTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The clock of the timeouts and of <c>as_of</c>.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>The largest image the resolver gives (1 MiB, the same as <c>IMAGE_MAX_BYTES</c> of the renderers).</summary>
    public int MaxImageBytes { get; init; } = ImageCheck.MaxBytes;

    /// <summary>The prefix of a semantic model as a serving source (<c>semantic:retail_sales</c>), the serving core's convention.</summary>
    public string ModelSourcePrefix { get; init; } = "semantic:";

    /// <summary>The model a saved query belongs to (a saved query's widget does not say). Null: saved queries are not run here.</summary>
    public Func<string, string?>? SavedQueryModel { get; init; }

    /// <summary>Told of a widget that failed in a way nobody classified (its exception, for the host's log). The entry shows only the kind: the exception's words can name tables, servers and files.</summary>
    public Action<string, Exception>? OnUnexpected { get; init; }

    /// <summary>
    /// The origins (<c>https://reports.example.com</c>: scheme, host and port) an <c>embed</c> widget may frame (the app instance's <c>frame</c> exception, plan 26 AP17, is where a host takes them from). An embed whose address is on
    /// none of them is shown as a link; none at all: every embed is a link. Compared as <see cref="ReportCatalogs.OriginOf"/> writes an origin.
    /// </summary>
    public IReadOnlyCollection<string> FrameOrigins { get; init; } = [];

    /// <summary>
    /// The custom views of the app the host serves (plan 32 DBR31): a custom widget's <c>component</c> (<c>&lt;app&gt;/&lt;Export&gt;</c>) to the manifest's entry that declares its queries. Null, or a component it does not know: a custom widget is an error entry
    /// (this host has no app to draw it).
    /// </summary>
    public Func<string, DbDataKit.Apps.CustomView?>? CustomViews { get; init; }

    /// <summary>The bytes of an image by hash (<c>sha256:...</c>), from the published definition's artifacts; null when it has none.</summary>
    public Func<string, byte[]?>? ImageBytes { get; init; }

    /// <summary>The result cache (opt in: null runs every request). What it holds is the rows of a successful answer, under <see cref="CacheScope"/> and <see cref="WidgetCacheKey"/>, so two viewers whose access differs never share an entry.</summary>
    public WidgetResultCache? Cache { get; init; }

    /// <summary>
    /// What the host adds to the cache key at the time of each lookup (plan 24a UC15): the state of everything that decides the rows a viewer may get besides the viewer and the request (a refreshed rules table, a published model, a changed data source). A host
    /// that changes the rules a viewer is held to without this changing lets an entry of the old rules be served until its time-to-live; null adds nothing.
    /// </summary>
    public Func<string>? CacheBasis { get; init; }

    /// <summary>What the host adds to the cache key: the definition and what it depends on (<see cref="PublishedView.CacheScope"/>), so a new model version never answers from the rows of the old one.</summary>
    public string CacheScope { get; init; } = "";
}

/// <summary>
/// What one resolution gives (plan 32 section 2.6): the widgets the layout placed (or the ones asked for), by id, each in the form of <c>rendered-widgets</c> (a metric, a table, a matrix, a chart, text, an image, a
/// control, or an error entry), and per widget the key a result cache would use. The JSON is what <c>@dbdatakit/visuals</c> reads (<c>src/views/rendered.ts</c>).
/// </summary>
public sealed class RenderedWidgets(string layout, DateTimeOffset asOf, IReadOnlyDictionary<string, JsonObject> widgets, IReadOnlyDictionary<string, string> cacheKeys)
{
    public string Layout { get; } = layout;

    public DateTimeOffset AsOf { get; } = asOf;

    /// <summary>The rendered widgets by id, in the layout's order.</summary>
    public IReadOnlyDictionary<string, JsonObject> Widgets { get; } = widgets;

    /// <summary>For each widget that ran a request, the key a cache of its rows would use: the channel, who asked (identity, roles, attributes) and the request as it ran. Two viewers whose access differs never share one.</summary>
    public IReadOnlyDictionary<string, string> CacheKeys { get; } = cacheKeys;

    public bool Failed(string id) => Widgets.TryGetValue(id, out var w) && w["type"]?.GetValue<string>() == "error";

    public JsonObject ToJson()
    {
        var all = new JsonObject();
        foreach (var (id, widget) in Widgets) all[id] = widget.DeepClone();
        return new JsonObject
        {
            ["kind"] = "rendered_widgets",
            ["layout"] = Layout,
            ["as_of"] = AsOf.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["widgets"] = all,
        };
    }
}

/// <summary>
/// The opt-in cache of widget rows (plan 24's cache, plan 32 section 3.5): the rows a request answered, kept for <c>ttl</c> under <see cref="WidgetCacheKey"/> (channel, who asked with their roles and attributes, the request as it ran) and the definition's scope.
/// Only an answer that came back whole is kept (a refusal, a timeout or an answer over the row limit is never), and what is handed out is a copy, so a renderer cannot change what the next viewer gets. Memory only: an instance's own, bounded by <c>maxEntries</c>.
/// </summary>
public sealed class WidgetResultCache(TimeSpan ttl, int maxEntries = 1000, TimeProvider? time = null)
{
    private readonly object gate = new();
    private readonly Dictionary<string, (AnswerRows Rows, DateTimeOffset Expires)> entries = new(StringComparer.Ordinal);
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private long hits;
    private long misses;

    public TimeSpan Ttl { get; } = ttl;

    public long Hits => Interlocked.Read(ref hits);

    public long Misses => Interlocked.Read(ref misses);

    public int Count { get { lock (gate) return entries.Count; } }

    public void Clear() { lock (gate) entries.Clear(); }

    internal bool TryGet(string key, out AnswerRows rows)
    {
        lock (gate)
        {
            if (entries.TryGetValue(key, out var found) && found.Expires > clock.GetUtcNow())
            {
                Interlocked.Increment(ref hits);
                rows = found.Rows.Copy();
                return true;
            }
            if (found.Rows is not null) entries.Remove(key);
        }
        Interlocked.Increment(ref misses);
        rows = null!;
        return false;
    }

    internal void Set(string key, AnswerRows rows)
    {
        if (Ttl <= TimeSpan.Zero) return;
        lock (gate)
        {
            var now = clock.GetUtcNow();
            if (entries.Count >= maxEntries)
            {
                foreach (var stale in entries.Where(e => e.Value.Expires <= now).Select(e => e.Key).ToList()) entries.Remove(stale);
                // still full: the entry that would expire first goes
                if (entries.Count >= maxEntries) entries.Remove(entries.MinBy(e => e.Value.Expires).Key);
            }
            entries[key] = (rows.Copy(), now + Ttl);
        }
    }
}

/// <summary>The key of a cached result (plan 24's opt-in cache, plan 32 section 3.5): everything that can change the rows a viewer gets, so two viewers with different access never share an entry.</summary>
public static class WidgetCacheKey
{
    public static string Of(ServingContext context, string source, string canonicalRequest)
    {
        var caller = context.Caller;
        var text = new StringBuilder()
            .Append(context.Channel).Append('\n').Append(context.Preview).Append('\n')
            .Append(caller.Id).Append('\n')
            .Append(string.Join(',', caller.Roles.Order(StringComparer.Ordinal))).Append('\n')
            .Append(string.Join(',', caller.Claims.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => $"{c.Key}={c.Value}"))).Append('\n')
            .Append(source).Append('\n').Append(canonicalRequest).ToString();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }
}
