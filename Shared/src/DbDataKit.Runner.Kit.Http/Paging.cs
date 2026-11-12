using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using DbDataKit.Connections;
using DbDataKit.Dialect;

namespace DbDataKit.Runner.Kit.Http;

/// <summary>How an API pages its answers. Three styles cover most APIs; anything else is a loop over <see cref="OutsideHttp.GetAsync"/>.</summary>
public abstract record PagingStyle
{
    /// <summary>Where the items are in the JSON of a page: a dotted path of property names (<c>data.items</c>); null when the page is itself the array.</summary>
    public string? ItemsPath { get; init; }
}

/// <summary>A page is <c>?offset=N&amp;limit=M</c>; the last page is the one with fewer than M items.</summary>
public sealed record OffsetPaging : PagingStyle
{
    public int PageSize { get; init; } = 100;

    public string OffsetParameter { get; init; } = "offset";

    public string LimitParameter { get; init; } = "limit";
}

/// <summary>A page names the next one by a cursor in its body (<c>next_cursor</c>); the last page has none (null, missing or empty).</summary>
public sealed record CursorPaging : PagingStyle
{
    public string CursorParameter { get; init; } = "cursor";

    /// <summary>Where the cursor is in the page's JSON, a dotted path.</summary>
    public string NextCursorPath { get; init; } = "next_cursor";

    public string? LimitParameter { get; init; }

    public int? PageSize { get; init; }
}

/// <summary>A page names the next one in the <c>Link</c> header (<c>rel="next"</c>, RFC 8288). Only a link to the same origin as the connection is followed: the connection's credentials go to the connection's system and nowhere else.</summary>
public sealed record LinkHeaderPaging : PagingStyle;

/// <summary>One page of an API's answer: its number (from 1), its items (each valid after the page is dropped), and where to resume.</summary>
/// <param name="Resume">What to give <see cref="OutsideHttpPaging.PagesAsync"/> as <c>resumeFrom</c> to continue after this page: the next offset, the next cursor or the next link. Null on the last page. Keep it as a <c>[State]</c> member of the job's progress to continue where the last run stopped.</param>
public sealed record ApiPage(int Number, IReadOnlyList<JsonElement> Items, string? Resume);

public static class OutsideHttpPaging
{
    /// <summary>
    /// The pages of an API, read one request at a time, **as the caller asks for them** (a caller that stops after one page has made one request). <paramref name="resumeFrom"/> is the <see cref="ApiPage.Resume"/> of an earlier page.
    /// </summary>
    public static async IAsyncEnumerable<ApiPage> PagesAsync(this OutsideHttp http, string path, PagingStyle style, IReadOnlyDictionary<string, string?>? query = null, string? resumeFrom = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        var number = 0;
        var offset = style is OffsetPaging && resumeFrom is not null ? long.Parse(resumeFrom, CultureInfo.InvariantCulture) : 0;
        var cursor = style is CursorPaging ? resumeFrom : null;
        var link = style is LinkHeaderPaging ? resumeFrom : null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            ApiResponse response;
            switch (style)
            {
                case OffsetPaging o:
                    response = await http.GetAsync(path, With(query, (o.OffsetParameter, offset.ToString(CultureInfo.InvariantCulture)), (o.LimitParameter, o.PageSize.ToString(CultureInfo.InvariantCulture))), ct).ConfigureAwait(false);
                    break;
                case CursorPaging c:
                    response = await http.GetAsync(path, With(query, (c.CursorParameter, cursor), (c.LimitParameter ?? string.Empty, c.PageSize?.ToString(CultureInfo.InvariantCulture))), ct).ConfigureAwait(false);
                    break;
                case LinkHeaderPaging:
                    response = await http.GetAsync(link ?? path, link is null ? query : null, ct).ConfigureAwait(false);
                    break;
                default:
                    throw new ArgumentException($"Unknown paging style {style.GetType().Name}.", nameof(style));
            }

            var json = response.Json();
            var items = Items(json, style.ItemsPath);
            number++;
            string? next = null;
            switch (style)
            {
                case OffsetPaging o:
                    if (items.Count >= o.PageSize && items.Count > 0) next = (offset + items.Count).ToString(CultureInfo.InvariantCulture);
                    offset += items.Count;
                    break;
                case CursorPaging c:
                    cursor = At(json, c.NextCursorPath) is { ValueKind: JsonValueKind.String } s && s.GetString() is { Length: > 0 } text ? text
                           : At(json, c.NextCursorPath) is { ValueKind: JsonValueKind.Number } n ? n.GetRawText() : null;
                    next = cursor;
                    break;
                case LinkHeaderPaging:
                    link = NextLink(response, http.BaseAddress);
                    next = link;
                    break;
            }

            yield return new ApiPage(number, items, next);
            if (next is null) yield break;
        }
    }

    /// <summary>The items of every page, one by one.</summary>
    public static async IAsyncEnumerable<JsonElement> ItemsAsync(this OutsideHttp http, string path, PagingStyle style, IReadOnlyDictionary<string, string?>? query = null, string? resumeFrom = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var page in http.PagesAsync(path, style, query, resumeFrom, ct).ConfigureAwait(false))
            foreach (var item in page.Items)
                yield return item;
    }

    private static IEnumerable<KeyValuePair<string, string?>> With(IReadOnlyDictionary<string, string?>? query, params (string Key, string? Value)[] paging)
    {
        var all = new List<KeyValuePair<string, string?>>();
        if (query is not null) all.AddRange(query);
        foreach (var (key, value) in paging)
            if (key.Length > 0 && value is not null) all.Add(new(key, value));
        return all;
    }

    private static IReadOnlyList<JsonElement> Items(JsonElement page, string? path)
    {
        var node = path is null ? page : At(page, path) ?? throw new ApiShapeException($"The page has no '{path}'.");
        if (node.ValueKind != JsonValueKind.Array) throw new ApiShapeException(path is null ? "The page is not a JSON array." : $"'{path}' of the page is not a JSON array.");
        return [.. node.EnumerateArray()];
    }

    private static JsonElement? At(JsonElement root, string path)
    {
        var node = root;
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(part, out node)) return null;
        }

        return node;
    }

    private static readonly Regex NextRel = new("<(?<url>[^>]+)>\\s*;[^,]*?rel=\"?(?:[^\",]*\\s)?next(?:\\s[^\",]*)?\"?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static string? NextLink(ApiResponse response, Uri? baseAddress)
    {
        if (response.Header("Link") is not { } header || NextRel.Match(header) is not { Success: true } match) return null;
        var target = new Uri(baseAddress ?? new Uri("http://invalid/"), match.Groups["url"].Value);
        if (baseAddress is not null && (target.Scheme != baseAddress.Scheme || target.Host != baseAddress.Host || target.Port != baseAddress.Port))
            throw new ConnectionFailureException(new ConnectionFailure(ConnectionFailureKind.Syntax, "link-origin", new EngineId("http")));
        if (baseAddress is null) return target.ToString();
        var relative = target.AbsolutePath.StartsWith(baseAddress.AbsolutePath, StringComparison.Ordinal) ? target.AbsolutePath[baseAddress.AbsolutePath.Length..] : target.AbsolutePath;
        return relative.TrimStart('/') + target.Query;
    }
}
