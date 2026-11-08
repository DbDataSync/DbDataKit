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

    /// <summary>The bytes of an image by hash (<c>sha256:...</c>), from the published definition's artifacts; null when it has none.</summary>
    public Func<string, byte[]?>? ImageBytes { get; init; }
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
